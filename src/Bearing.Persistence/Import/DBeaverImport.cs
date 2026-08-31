using System.Text.Json;
using Bearing.Core.Data;

namespace Bearing.Persistence.Import;

/// <summary>One connection that was not imported, and why — shown in the summary rather than dropped
/// silently. A real workspace is mixed: the one this was verified against is 4 postgres and 6 not.</summary>
public sealed record SkippedConnection(string Name, string Reason);

/// <summary>What a parse produced: connections to add, the folders they are filed in, what was skipped, and
/// anything worth mentioning about the rows that did come through.</summary>
public sealed record DBeaverImportResult(
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<string> Folders,
    IReadOnlyList<SkippedConnection> Skipped,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads DBeaver's <c>data-sources.json</c> into <see cref="ConnectionInfo"/> records (#72). Pure: a string
/// in, records out — no file I/O, no UI, no network — so the whole mapping is testable against a checked-in
/// fixture (§2.5, §4.1).
///
/// <para><b>Passwords are never imported.</b> DBeaver keeps them in <c>credentials-config.json</c>, an
/// encrypted blob under its own static key. Decrypting another application's credential store is not
/// something to do quietly, and Bearing's posture (§1.1) is that a password reaches the OS keychain and
/// nowhere else. Imported connections arrive without one — and, in practice, without a user name either:
/// that lives in the same encrypted file, so it is absent from every row of a real workspace.</para>
///
/// <para>Version-tolerance over completeness: unknown keys are ignored, missing keys defaulted, and a
/// connection that cannot be read is skipped with a reason rather than failing the file. The format has
/// changed shape across DBeaver majors and the useful behaviour is to import what is recognisable.</para>
/// </summary>
public static class DBeaverImport
{
    /// <summary>DBeaver's provider id for PostgreSQL. Everything else is skipped and reported.</summary>
    private const string PostgresProvider = "postgresql";

    /// <summary>
    /// The only <c>configuration.properties</c> keys carried across. The rest of that bag is JDBC-specific
    /// (<c>connectTimeout</c>, <c>escapeSyntaxCallMode</c>, <c>loginTimeout</c>) and means nothing to Npgsql
    /// — <c>NpgsqlConnectionFactory</c> would ignore it anyway, so importing it would only put noise in the
    /// shared project.json. Dropped keys are named in the warnings instead of vanishing.
    /// </summary>
    private static readonly HashSet<string> PortableOptions =
        new(StringComparer.OrdinalIgnoreCase) { "sslmode", "search_path" };

    /// <summary>
    /// DBeaver writes an Eclipse theme resource id where a colour is expected for its built-in connection
    /// types (<c>org.jkiss.dbeaver.color.connectionType.prod.background</c>), not an <c>R,G,B</c> triple.
    /// Mapped to Bearing's own environment presets by the type key, which is the meaning the id carries.
    /// </summary>
    private static readonly Dictionary<string, string> PresetByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prod"] = "#E5484D",
        ["production"] = "#E5484D",
        ["test"] = "#D29922",
        ["staging"] = "#D29922",
        ["qa"] = "#D29922",
        ["dev"] = "#3FB950",
        ["development"] = "#3FB950",
        ["local"] = "#3FB950",
    };

    /// <summary>Parse a <c>data-sources.json</c> document.</summary>
    /// <param name="json">The file's contents.</param>
    /// <param name="newId">Identity source, injectable so tests are deterministic. Defaults to
    /// <see cref="Guid.NewGuid"/> — DBeaver's own key is never reused, because an id is Bearing's
    /// secret-store lookup key and means nothing outside this project.</param>
    public static DBeaverImportResult Parse(string json, Func<Guid>? newId = null)
    {
        newId ??= Guid.NewGuid;
        var connections = new List<ConnectionInfo>();
        var skipped = new List<SkippedConnection>();
        var warnings = new List<string>();
        var folders = new List<string>();

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            return new DBeaverImportResult([], [], [],
                [$"This is not a readable data-sources.json: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new DBeaverImportResult([], [], [], ["This is not a readable data-sources.json."]);

            var types = ReadConnectionTypes(root);

            if (Obj(root, "folders") is { } folderMap)
                foreach (var folder in folderMap.EnumerateObject())
                    if (!string.IsNullOrWhiteSpace(folder.Name)) folders.Add(folder.Name.Trim());

            if (Obj(root, "connections") is { } connectionMap)
            {
                foreach (var entry in connectionMap.EnumerateObject())
                {
                    try { ReadConnection(entry, types, newId, connections, skipped, warnings); }
                    catch (Exception ex)
                    {
                        // One unreadable row must not cost the user the other nine.
                        skipped.Add(new SkippedConnection(
                            Str(entry.Value, "name") ?? entry.Name, $"could not be read ({ex.Message})"));
                    }
                }
            }
        }

        // Folders a connection claims but the file never declared: materialised so nothing lands somewhere
        // the panel would not draw.
        foreach (var c in connections)
            if (!string.IsNullOrWhiteSpace(c.Folder) && !folders.Contains(c.Folder!, StringComparer.OrdinalIgnoreCase))
                folders.Add(c.Folder!);

        if (connections.Count > 0)
            warnings.Add(connections.Count == 1
                ? "The imported connection has no user name or password — DBeaver keeps both in an encrypted file. Add them in the connection dialog."
                : $"The {connections.Count} imported connections have no user name or password — DBeaver keeps both in an encrypted file. Add them in each connection's dialog.");

        return new DBeaverImportResult(connections, folders, skipped, warnings);
    }

    private static void ReadConnection(
        JsonProperty entry,
        IReadOnlyDictionary<string, ConnectionType> types,
        Func<Guid> newId,
        List<ConnectionInfo> connections,
        List<SkippedConnection> skipped,
        List<string> warnings)
    {
        var value = entry.Value;
        var name = Str(value, "name") ?? entry.Name;
        var provider = Str(value, "provider") ?? "";

        if (!string.Equals(provider, PostgresProvider, StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add(new SkippedConnection(name, provider.Length == 0
                ? "no provider recorded"
                : $"unsupported provider {provider}"));
            return;
        }

        var config = Obj(value, "configuration");
        if (config is null)
        {
            skipped.Add(new SkippedConnection(name, "no connection settings recorded"));
            return;
        }

        var cfg = config.Value;
        var typeKey = Str(cfg, "type");
        types.TryGetValue(typeKey ?? "", out var type);

        var (options, dropped) = ReadOptions(cfg);
        if (dropped.Count > 0)
            warnings.Add($"{name}: ignored {string.Join(", ", dropped)} — not settings Bearing can apply.");

        connections.Add(new ConnectionInfo
        {
            Id = newId(),
            Name = name,
            ProviderId = "postgres",
            Host = Str(cfg, "host") ?? "localhost",
            // A row with no port is real — one sqlserver entry in the verified workspace has none.
            Port = Port(cfg) ?? 5432,
            Database = Str(cfg, "database") ?? "",
            // Absent from every connection in a real workspace: it lives in credentials-config.json, which
            // is encrypted and deliberately not read. Blank rather than a failed row.
            User = Str(cfg, "user") ?? "",
            Folder = Str(value, "folder"),
            Environment = type?.Name,
            EnvironmentColor = type?.Color,
            RequireWriteConfirmation = type?.ConfirmDataChange ?? false,
            // No password can be imported, so a stored-password connection would fail with an unhelpful
            // error. Prompting is both honest and what actually works until the user saves one.
            CredentialKind = CredentialKind.Prompt,
            Options = options,
        });
    }

    /// <summary>The portable half of <c>configuration.properties</c>, plus the names of what was left.</summary>
    private static (Dictionary<string, string> Options, List<string> Dropped) ReadOptions(JsonElement cfg)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dropped = new List<string>();
        if (Obj(cfg, "properties") is not { } properties) return (options, dropped);

        foreach (var property in properties.EnumerateObject())
        {
            var text = Text(property.Value);
            if (text is null) continue;
            if (!PortableOptions.Contains(property.Name)) { dropped.Add(property.Name); continue; }

            // JDBC spells these with hyphens (verify-ca, verify-full); Npgsql's SslMode enum does not.
            options[property.Name] = property.Name.Equals("sslmode", StringComparison.OrdinalIgnoreCase)
                ? text.Replace("-", "")
                : text;
        }
        return (options, dropped);
    }

    private sealed record ConnectionType(string? Name, string? Color, bool ConfirmDataChange);

    private static Dictionary<string, ConnectionType> ReadConnectionTypes(JsonElement root)
    {
        var types = new Dictionary<string, ConnectionType>(StringComparer.OrdinalIgnoreCase);
        if (Obj(root, "connection-types") is not { } map) return types;

        foreach (var entry in map.EnumerateObject())
        {
            var value = entry.Value;
            types[entry.Name] = new ConnectionType(
                // Lowercased to match Bearing's own presets ("local", "staging", "production"), which is
                // what the environment chip and the accent are styled around.
                Str(value, "name")?.ToLowerInvariant(),
                Color(Str(value, "color"), entry.Name),
                Bool(value, "confirm-data-change"));
        }
        return types;
    }

    /// <summary>
    /// A connection type's colour as <c>#RRGGBB</c>, or null for "untinted".
    /// <para>Three shapes arrive here. An <c>R,G,B</c> triple converts directly. An Eclipse theme resource
    /// id maps to Bearing's preset for that type key. And plain white is DBeaver's <i>no tint</i> — its
    /// stock Development type is <c>255,255,255</c> — so importing it as a colour would paint every
    /// development row white rather than leaving it neutral.</para>
    /// </summary>
    private static string? Color(string? raw, string typeKey)
    {
        if (string.IsNullOrWhiteSpace(raw)) return PresetByType.GetValueOrDefault(typeKey);

        var parts = raw.Split(',');
        if (parts.Length != 3)
            return PresetByType.GetValueOrDefault(typeKey);   // a theme resource id, or something unexpected

        if (!byte.TryParse(parts[0].Trim(), out var r)
            || !byte.TryParse(parts[1].Trim(), out var g)
            || !byte.TryParse(parts[2].Trim(), out var b))
            return PresetByType.GetValueOrDefault(typeKey);

        return r == 255 && g == 255 && b == 255 ? null : $"#{r:X2}{g:X2}{b:X2}";
    }

    // ---- defensive readers: a wrong type is a missing value, never a throw --------------------------

    private static JsonElement? Obj(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value : null;

    private static string? Str(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;
        var text = Text(value);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>A JSON scalar as text. DBeaver writes ports as strings, but a hand-edited or older file may
    /// hold a number, so both are read.</summary>
    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static int? Port(JsonElement cfg)
        => int.TryParse(Str(cfg, "port"), out var port) && port is > 0 and <= 65535 ? port : null;

    private static bool Bool(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;
}
