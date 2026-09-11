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
    /// When true, this connection's sessions start with <c>default_transaction_read_only=on</c>, so the
    /// <b>server</b> refuses a write rather than Bearing asking about one (#99). Auto-enabled for the
    /// "production" preset. Where <see cref="RequireWriteConfirmation"/> asks, this refuses — and it catches
    /// what a lexer cannot: a function that writes, dynamic SQL in a <c>DO</c> block, <c>COPY … TO</c>.
    /// <para>
    /// <b>Not a privilege boundary.</b> <c>default_transaction_read_only</c> is a <c>USERSET</c> GUC, so a
    /// user who types <c>SET default_transaction_read_only = off</c> or <c>BEGIN READ WRITE</c> turns it off.
    /// It stops mistakes, which is what it is for; a boundary a user cannot lift is a role without write
    /// privileges, and that is the server admin's to grant. Describe it as the former and never the latter —
    /// the same line <see cref="TlsPolicy"/> draws between encrypting and verifying.
    /// </para>
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Seconds Postgres will let one statement run on this connection before cancelling it, or
    /// <see cref="SessionPolicy.NoTimeout"/> (the default) for no limit (#105).
    /// <para>
    /// Not the same control as the driver's <c>CommandTimeout</c>, which 0.5.3 (#93) set to 0: that one
    /// stopped Bearing <i>waiting</i>, and a query it gave up on kept running on the server. This one stops
    /// the server <i>running</i>, so it protects the server rather than the user's patience. Applied through
    /// the startup packet, not a <c>SET</c> — see <see cref="SessionPolicy.StartupOptions"/> for why that
    /// distinction is load-bearing with a connection pool.
    /// </para>
    /// </summary>
    public int StatementTimeoutSeconds { get; init; } = SessionPolicy.NoTimeout;

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
public sealed record ConnectionField(
    string Key,
    string Label,
    ConnectionFieldKind Kind,
    bool Required,
    string? Default = null);

public enum ConnectionFieldKind
{
    Text,
    Number,
    Password,
    Boolean,
    Choice,
}
