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
    /// When true, writes on this connection are refused rather than confirmed (#99). Auto-enabled for the
    /// "production" preset. Where <see cref="RequireWriteConfirmation"/> asks, this refuses.
    /// <para>
    /// <b>Who refuses depends on the engine</b> — <see cref="IDbProvider.EnforcesReadOnlyOnServer"/>. On
    /// Postgres the sessions start with <c>default_transaction_read_only=on</c>, so the <b>server</b> refuses
    /// and catches what a lexer cannot: a function that writes, dynamic SQL in a <c>DO</c> block,
    /// <c>COPY … TO</c>. SQL Server has no session read-only to ask for, so there the refusal is Bearing's
    /// alone and a write hidden from the lexer still reaches the server. The dialog's note says which, and
    /// must keep saying it.</para>
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
    /// When true, a write on this connection opens a transaction that stays open until the user commits or
    /// rolls it back, rather than committing itself (#131). A read on its own still auto-commits: it is the
    /// first <i>write</i> that opens one, because a transaction left open by nothing but browsing is the
    /// commonest way to end up holding locks on a production server.
    /// <para>
    /// <b>Client-side, and unlike the other two safety settings it reaches no server.</b>
    /// <see cref="ReadOnly"/> and <see cref="StatementTimeoutSeconds"/> ride the startup packet and are
    /// things the server is asked to enforce (§1.9); this one is a connection Bearing holds open and a
    /// <c>COMMIT</c> it declines to send. So it is not part of what defines a pool — see
    /// <c>SamePool</c> — and toggling it never rebuilds one.
    /// </para>
    /// <para>
    /// <b>It does not relax the write guard.</b> A connection with both this and
    /// <see cref="RequireWriteConfirmation"/> confirms the write <i>and</i> holds it; §1.2 is not narrowed
    /// because a write became undoable. On a connection that is also <see cref="ReadOnly"/> nothing ever
    /// opens, because nothing ever writes.
    /// </para>
    /// </summary>
    public bool ManualCommit { get; init; }

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
