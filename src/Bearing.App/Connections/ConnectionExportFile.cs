using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence.Import;

namespace Bearing.App.Connections;

/// <summary>
/// "Export connections…" / "Import connections from a file…": a project's connections written to a
/// <c>.json</c> file so they can be carried to another machine without being set up again.
///
/// <para><b>An envelope around the clipboard payload, not a second format.</b> The connections inside are
/// exactly what <see cref="ConnectionClipboard.Write"/> produces and are read back by
/// <see cref="ConnectionClipboard.TryRead"/>, so everything that record decides holds here unchanged: no
/// password, no SSH secret, no id (import mints fresh ones) and no <see cref="ConnectionInfo.ExternalAccess"/>
/// (§1.1, §1.11), while every safety setting — TLS, read-only, timeout, manual commit, full-scan
/// confirmation, session time zone, the SSH route and its pinned host key — travels. The envelope adds what a
/// clipboard paste never needed: a format marker and version, so a file that isn't ours is refused with a
/// sentence rather than half-imported, and the declared folders, so empty folders arrive too.</para>
///
/// <para>Pure: text in, records out — no file I/O, no window (§2.5).</para>
/// </summary>
public static class ConnectionExportFile
{
    /// <summary>The envelope's marker. Distinct from <see cref="ConnectionClipboard.Kind"/>, which marks
    /// the payload nested inside it.</summary>
    public const string Format = "bearing.connections-export";

    /// <summary>The envelope version this build writes and the newest it reads.</summary>
    public const int Version = 1;

    public const string Extension = "json";

    /// <summary>What to export: these connections and the folders that give them their structure.</summary>
    public sealed record Selection(IReadOnlyList<ConnectionInfo> Connections, IReadOnlyList<string> Folders);

    /// <summary>A file read: either connections (fresh ids, no secrets) and their folders, or a
    /// <see cref="Refusal"/> sentence saying why the file was not imported.</summary>
    public sealed record ReadResult(
        IReadOnlyList<ConnectionInfo> Connections, IReadOnlyList<string> Folders, string? Refusal)
    {
        public bool Ok => Refusal is null;

        internal static ReadResult Refused(string why) => new([], [], why);
    }

    /// <summary>The sentence the review dialog shows about what the file does not carry. Worded on what the
    /// code does (CredentialResolver): a stored-password connection asks when its first connect is refused,
    /// and saves the answer to the keychain once a connect with it worked. With no keychain reachable it is
    /// kept until Bearing closes, and the status line says so then.</summary>
    public const string SecretsNote =
        "Passwords and SSH secrets are never written to an export file. Each connection asks for its password "
        + "once, the first time it connects, and saves it to the keychain once it has worked. "
        + "Access for the bearing command-line tool is not carried either; turn it on again where you want it.";

    // ---- choosing what to export --------------------------------------------------------------------

    /// <summary>
    /// The connections and folders one export covers: a single connection (<paramref name="connectionId"/>),
    /// everything under a folder (<paramref name="folder"/>, nested folders included), or — with neither —
    /// the whole project. Folder paths stay as they are in the project rather than being re-rooted, so an
    /// import rebuilds the same tree.
    /// </summary>
    public static Selection Select(ProjectManifest manifest, string? folder = null, Guid? connectionId = null)
    {
        if (connectionId is { } id)
        {
            var one = manifest.Connections.Where(c => c.Id == id).ToList();
            return new Selection(one, FoldersOf(one, []));
        }

        var root = FolderPath.Normalize(folder);
        var connections = manifest.Connections.Where(c => FolderPath.IsWithin(c.Folder, root)).ToList();
        var declared = manifest.ConnectionFolders.Where(f => FolderPath.IsWithin(f, root));
        return new Selection(connections, FoldersOf(connections, declared));
    }

    // ---- writing -------------------------------------------------------------------------------------

    /// <summary>Render an export file. <paramref name="exportedAt"/> is informational — it tells two files
    /// apart — and is never read back.</summary>
    public static string Write(Selection selection, DateTimeOffset exportedAt)
    {
        var envelope = new JsonObject
        {
            ["Format"] = Format,
            ["Version"] = Version,
            ["ExportedAt"] = exportedAt.ToString("O"),
            ["Folders"] = new JsonArray(selection.Folders.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            // The clipboard payload, verbatim: the one serializer for a connection's shareable shape.
            ["Connections"] = JsonNode.Parse(ConnectionClipboard.Write(selection.Connections)),
        };
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ---- reading -------------------------------------------------------------------------------------

    private const string NotOurs =
        "This is not a Bearing connections file. Make one with “Export connections…” in Bearing; "
        + "a DBeaver data-sources.json goes through Import connections → From DBeaver.";

    /// <summary>
    /// Read an export file. Also accepts a bare clipboard payload saved to a file, which is ours too. Anything
    /// else — garbage, another application's JSON, a file from a newer Bearing — is refused with a sentence.
    /// </summary>
    public static ReadResult Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ReadResult.Refused("The file is empty.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return ReadResult.Refused(NotOurs); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ReadResult.Refused(NotOurs);

            if (StringProp(root, "Format") is { } format)
            {
                if (!string.Equals(format, Format, StringComparison.Ordinal)) return ReadResult.Refused(NotOurs);
                return ReadEnvelope(root);
            }

            // A clipboard payload saved as a file: ours, just without declared folders.
            if (string.Equals(StringProp(root, "Kind"), ConnectionClipboard.Kind, StringComparison.Ordinal))
                return ReadPayload(root, []);

            return ReadResult.Refused(NotOurs);
        }
    }

    private static ReadResult ReadEnvelope(JsonElement root)
    {
        if (Prop(root, "Version") is not { ValueKind: JsonValueKind.Number } v || !v.TryGetInt32(out var version)
            || version < 1)
            return ReadResult.Refused(NotOurs);
        if (version > Version)
            return ReadResult.Refused(
                $"This file was exported by a newer Bearing (format version {version}); update Bearing to import it.");

        var declared = Prop(root, "Folders") is { ValueKind: JsonValueKind.Array } folders
            ? folders.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String)
                .Select(f => f.GetString()!)
                .ToList()
            : [];

        if (Prop(root, "Connections") is not { ValueKind: JsonValueKind.Object } payload)
            return ReadResult.Refused("This Bearing export is damaged: it has no connections section.");
        return ReadPayload(payload, declared);
    }

    private static ReadResult ReadPayload(JsonElement payload, IReadOnlyList<string> declared)
    {
        if (Prop(payload, "Connections") is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() == 0)
            return ReadResult.Refused("The file holds no connections.");
        if (!ConnectionClipboard.TryRead(payload.GetRawText(), out var connections))
            return ReadResult.Refused("This Bearing export is damaged: its connections could not be read.");
        return new ReadResult(connections, FoldersOf(connections, declared), null);
    }

    // ---- reviewing and importing ---------------------------------------------------------------------

    /// <summary>
    /// The read file in the shape the existing import review takes (<c>ImportConnectionsDialog</c>). A
    /// connection whose engine this build doesn't have is listed as not imported rather than added as a row
    /// that could never connect.
    /// </summary>
    public static DBeaverImportResult Review(ReadResult read, Func<string, bool> isKnownProvider)
    {
        var importable = new List<ConnectionInfo>();
        var skipped = new List<SkippedConnection>();
        foreach (var c in read.Connections)
        {
            if (isKnownProvider(c.ProviderId)) importable.Add(c);
            else skipped.Add(new SkippedConnection(c.Name, $"its engine ('{c.ProviderId}') isn't in this build of Bearing"));
        }
        return new DBeaverImportResult(importable, read.Folders, skipped, [SecretsNote]);
    }

    /// <summary>
    /// The folders to create for the connections the user ticked: every folder holding one of them, and
    /// every folder the file declared that held no connection at all (an empty folder is part of the
    /// structure that was exported). A folder whose every connection was unticked is left out — creating it
    /// would leave an empty folder behind from the part of the file the user declined.
    /// </summary>
    public static IReadOnlyList<string> FoldersFor(ReadResult read, IReadOnlyList<ConnectionInfo> chosen)
        => read.Folders
            .Where(f => chosen.Any(c => FolderPath.IsWithin(c.Folder, f))
                        || !read.Connections.Any(c => FolderPath.IsWithin(c.Folder, f)))
            .ToList();

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>Declared folders plus every folder a connection sits in, normalised, de-duplicated
    /// case-insensitively (the project's own rule), in first-seen order.</summary>
    private static IReadOnlyList<string> FoldersOf(IEnumerable<ConnectionInfo> connections, IEnumerable<string> declared)
        => declared.Concat(connections.Select(c => c.Folder ?? ""))
            .Select(FolderPath.Normalize)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static JsonElement? Prop(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? StringProp(JsonElement obj, string name)
        => Prop(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
