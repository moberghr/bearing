namespace Bearing.Core.Data;

/// <summary>
/// Non-secret connection settings, as persisted in a project file. The password is NEVER here —
/// it is fetched from <c>ISecretStore</c> keyed by <see cref="Id"/>.
/// </summary>
public sealed record ConnectionInfo
{
    /// <summary>Stable identity; also the secret-store lookup key. Travels with the project.</summary>
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Provider id, e.g. "postgres".</summary>
    public required string ProviderId { get; init; }

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = "";
    public string User { get; init; } = "";

    /// <summary>How the secret (password / token) is obtained at connect time. Default
    /// <see cref="CredentialKind.StoredPassword"/> — a missing value in an older project file
    /// deserializes to the classic stored-password behaviour.</summary>
    public CredentialKind CredentialKind { get; init; } = CredentialKind.StoredPassword;

    /// <summary>
    /// Where this connection is filed in the connections panel: a "/"-separated folder path
    /// ("Aur/Production"), or null for the panel's root. Purely organisational — it never reaches a
    /// connection string, and it is deliberately orthogonal to <see cref="Environment"/>: a folder is where
    /// you filed it, an environment is how dangerous it is.
    /// </summary>
    public string? Folder { get; init; }

    /// <summary>Free-form environment label (e.g. "local", "staging", "production"); null = untagged.</summary>
    public string? Environment { get; init; }

    /// <summary>Hex color for the environment badge (e.g. "#E53935"); null = neutral.</summary>
    public string? EnvironmentColor { get; init; }

    /// <summary>
    /// When true, running a statement that writes data (INSERT/UPDATE/DELETE/MERGE) or alters schema
    /// (DROP/TRUNCATE/ALTER) against this connection asks for confirmation first. Auto-enabled for the
    /// "production" preset; a guard against fat-fingering a destructive query at prod.
    /// </summary>
    public bool RequireWriteConfirmation { get; init; }

    /// <summary>
    /// What this connection demands of the transport (#23). Default <see cref="TlsMode.Prefer"/> — the
    /// driver's own default, so a missing value in an older project file keeps the behaviour it already had.
    /// <para>
    /// A field rather than an <see cref="Options"/> entry because it is a security setting, and a bag that
    /// travels in a shared project.json is the wrong place for one: see <see cref="TlsPolicy.Resolve"/> for
    /// the precedence that keeps older projects working without leaving two sources of truth.
    /// </para>
    /// </summary>
    public TlsMode Tls { get; init; } = TlsPolicy.Default;

    /// <summary>Provider-specific extra options (e.g. search_path). <c>sslmode</c> used to live here and is
    /// still read from here for older projects — see <see cref="Tls"/>.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>Describes one field in a provider's connection dialog (drives the UI generically).</summary>
/// <param name="Choices">
/// The values a <see cref="ConnectionFieldKind.Choice"/> field offers, in display order; null or empty for
/// every other kind — and for a Choice field with nothing to offer, which the dialog then renders as a text
/// box rather than as an empty dropdown.
/// <para>
/// Added last and optional so every existing positional <c>new ConnectionField(…)</c> still compiles: this
/// record is constructed by each provider, and a required parameter in the middle would be a breaking change
/// to a <c>Core</c> contract for the sake of a field kind nothing declares yet.
/// </para>
/// <para>
/// <b>No shipped provider declares one.</b> Both engines' would-be dropdowns became typed fields —
/// <c>sslmode</c> and SQL Server's <c>Encrypt</c>/<c>TrustServerCertificate</c> are all
/// <see cref="ConnectionInfo.Tls"/> now (#23) — so this is a capability with no production user. It exists
/// because the kind already existed and rendered as a text box, which is a worse answer than either a
/// dropdown or no kind at all: a Choice field whose candidates the dialog could not show let the user type
/// a value the provider never offered.
/// </para>
/// </param>
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldKind Kind,
    bool Required,
    string? Default = null,
    IReadOnlyList<string>? Choices = null);

public enum ConnectionFieldKind
{
    Text,
    Number,
    Password,
    Boolean,

    /// <summary>One of a fixed set of values — see <see cref="ConnectionField.Choices"/>, which supplies
    /// them. Without candidates the dialog falls back to a text box, so a provider adding this kind must
    /// add the list with it.</summary>
    Choice,
}
