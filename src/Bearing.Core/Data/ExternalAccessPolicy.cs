namespace Bearing.Core.Data;

/// <summary>
/// What <see cref="ConnectionInfo.ExternalAccess"/> means in practice: whether a connection is exposed at
/// all, and the <see cref="ConnectionInfo"/> an external host must actually connect with. Pure, so both the
/// exposure decision and the settings it forces are testable without a server or a host (§2.5) — the shape
/// <see cref="SessionPolicy"/>, <see cref="TlsPolicy"/> and <see cref="CommitPolicy"/> already have.
/// <para>
/// <b>The gate is the connection, and it is the only gate this layer has.</b> An external host runs as the
/// user, which means it can read the same keychain the app reads; what stops it querying a server is that
/// the connection was never marked. Do not describe this as a sandbox anywhere — the boundary is "anything
/// running as you", and saying otherwise would be exactly the invented guarantee §1.1 forbids.
/// </para>
/// </summary>
public static class ExternalAccessPolicy
{
    /// <summary>Whether an external host may see this connection at all.</summary>
    public static bool IsExposed(ConnectionInfo info) => info.ExternalAccess != ExternalAccess.None;

    /// <summary>
    /// The connection an external host connects with, or <c>null</c> when it may not connect at all. The
    /// only route to an exposed connection: a host that used the saved record directly would be running
    /// with the user's own settings rather than the exposed ones.
    /// <para>
    /// Three things are decided here rather than trusted to the saved record:
    /// </para>
    /// <list type="bullet">
    /// <item><b><see cref="ConnectionInfo.ReadOnly"/> is forced on</b>, whatever the connection is for the
    /// user. On an engine with <see cref="IDbProvider.EnforcesReadOnlyOnServer"/> this rides the startup
    /// packet, so the <i>server</i> refuses the write and catches what a lexer cannot (§1.9); where it does
    /// not, the client-side refusal is the whole of it and the host has to say so rather than imply a
    /// server-side one (§1.9a). Forcing it here rather than requiring the user to also tick read-only is
    /// what keeps the two independent — the point of exposing a connection is usually that you still write
    /// to it yourself.</item>
    /// <item><b><see cref="ConnectionInfo.ManualCommit"/> is forced off.</b> Inert today, because nothing
    /// ever writes on a read-only session and it is the first <i>write</i> that opens a transaction (§1.10)
    /// — but a transaction nobody can reach is the failure mode that rule is entirely about, and an
    /// external host has no Commit button, no chip and no quit guard. The setting that cannot be honoured
    /// is cleared rather than left to be inert for a reason that might stop being true.</item>
    /// <item><b>A missing statement timeout is filled in</b> with <see cref="SessionPolicy.PresetTimeoutSeconds"/>.
    /// A timeout the user <i>set</i> is kept, including a long one: the fill exists to stop a runaway
    /// outliving the caller, not to second-guess a connection deliberately pointed at slow analytical work.
    /// The value goes through <see cref="SessionPolicy.TimeoutSeconds"/> first, so a hand-edited
    /// <c>project.json</c> cannot hand a host a value the server refuses at startup.</item>
    /// </list>
    /// <para>
    /// <see cref="ConnectionInfo.RequireWriteConfirmation"/> is left exactly as saved. There is nobody to
    /// confirm to, but the write it would confirm is already refused, and clearing a safety flag to tidy up
    /// a record is how one stops being set when it starts mattering again.
    /// </para>
    /// </summary>
    public static ConnectionInfo? ForExternalHost(ConnectionInfo info)
    {
        if (!IsExposed(info)) return null;

        var timeout = SessionPolicy.TimeoutSeconds(info);
        return info with
        {
            ReadOnly = true,
            ManualCommit = false,
            StatementTimeoutSeconds = timeout > SessionPolicy.NoTimeout
                ? timeout
                : SessionPolicy.PresetTimeoutSeconds,
        };
    }

    /// <summary>
    /// What exposing this connection lets in, in its own words — empty when it is not exposed, so the
    /// dialog says nothing rather than something reassuring about a connection nobody opened up.
    /// <para>
    /// The register is <see cref="TlsPolicy.Advice"/>'s and <see cref="SessionPolicy.Advice"/>'s: name what
    /// was actually arranged and do not overclaim. In particular it says the boundary is "anything running
    /// as you" rather than implying a sandbox, and it points at a database role, because that is the only
    /// control here that a determined caller cannot talk its way past (§1.11).
    /// </para>
    /// </summary>
    /// <param name="serverEnforcesReadOnly">
    /// <see cref="IDbProvider.EnforcesReadOnlyOnServer"/> for this engine. It changes who refuses a write,
    /// which is the difference between describing an arrangement and inventing one (§1.1): on an engine
    /// with no session read-only, only Bearing refuses, and a caller should be told that before deciding
    /// to expose a server.
    /// </param>
    public static string Advice(ExternalAccess access, bool serverEnforcesReadOnly = true)
    {
        if (access == ExternalAccess.None) return "";

        var refusal = serverEnforcesReadOnly
            ? "Only reads are sent, and the session is opened read-only, so the server refuses a write the "
              + "client did not catch."
            : "Only reads are sent. This engine has no session read-only to ask the server for, so that "
              + "check is the whole of it — a write hidden inside a procedure or built as dynamic SQL would "
              + "still reach the server.";

        return "The `bearing` command can list this connection and run reads on it, with Bearing closed. It "
             + "is given the name, engine and environment — never the host, user, database or password. "
             + refusal
             + " Anything running as you can use it, so for a server that matters, point the connection at a "
             + "database role that has only the privileges an agent should have.";
    }

    /// <summary>
    /// Why a host with no window cannot use this connection even though it is exposed, or <c>null</c> when
    /// nothing about it rules that out.
    /// <para>
    /// This answers only what the <see cref="CredentialKind"/> settles — a kind whose whole mechanism is
    /// asking the user cannot work where there is no user. It deliberately does <b>not</b> answer whether
    /// the credential can be obtained <i>right now</i>: an unreachable keyring, an <c>az</c> session that
    /// has expired and a password that was never stored are runtime facts, and reporting them from a pure
    /// function would be asserting a cause nobody checked (§1.1). A host reports those when the connect
    /// fails, with what the attempt actually said.
    /// </para>
    /// </summary>
    public static string? UnavailableReason(ConnectionInfo info) => info.CredentialKind switch
    {
        CredentialKind.Prompt =>
            $"'{info.Name}' asks for its password each session, so it can only be opened in Bearing.",
        _ => null,
    };
}
