using System;
using System.Globalization;

namespace Bearing.Core.Data;

/// <summary>
/// The two per-connection safety settings that are applied to a <i>session</i> rather than to a statement:
/// read-only (#99) and a statement timeout (#105). Pure, so both the wording a user is warned with and the
/// values it resolves to are unit-testable without a server (§2.5).
/// <para>
/// This layer holds only what the settings <i>mean</i>. How they reach a server is the provider's business:
/// see <c>PostgresConnectionString.StartupOptionsFor</c>, which composes Postgres' own startup syntax and
/// documents why a <c>SET</c> cannot do the job here. That split is <see cref="TlsPolicy"/>'s — it stops at
/// the neutral <see cref="TlsMode"/> and leaves the driver's spelling to <c>SslModeOf</c> (§2.1).
/// </para>
/// </summary>
public static class SessionPolicy
{
    /// <summary>No statement timeout — what a connection has unless one is set, matching 0.5.3's stance that
    /// a clock Bearing imposes can only get in the way of the slow analytical query that is the point of the
    /// tool (#93).</summary>
    public const int NoTimeout = 0;

    /// <summary>The seconds a preset offers when it turns the timeout on. Long enough that no interactive
    /// query reaches it, short enough that a runaway one does not outlive the person who left.</summary>
    public const int PresetTimeoutSeconds = 30;

    /// <summary>
    /// The longest timeout that can actually be asked for: Postgres' <c>statement_timeout</c> is milliseconds
    /// in an <c>int</c>, so this is that ceiling in seconds (~24.8 days).
    /// <para>
    /// Clamped rather than rejected, and the clamp is not tidiness. <c>project.json</c> is hand-editable, so a
    /// value like 3,000,000 is reachable — and it would overflow the conversion to milliseconds into a
    /// negative number, which Postgres refuses at startup. The connection would then stop opening **at all**,
    /// turning a silly number in a settings file into a connection nobody can use.
    /// </para>
    /// </summary>
    public const int MaxTimeoutSeconds = int.MaxValue / 1000;

    /// <summary>This connection's timeout in seconds: negative or zero is no timeout — a clock that has
    /// already run out is not a setting anyone meant, and refusing every statement is a worse answer than
    /// imposing no limit — and anything past <see cref="MaxTimeoutSeconds"/> is clamped to it.</summary>
    public static int TimeoutSeconds(ConnectionInfo info)
        => info.StatementTimeoutSeconds <= NoTimeout
            ? NoTimeout
            : Math.Min(info.StatementTimeoutSeconds, MaxTimeoutSeconds);

    /// <summary>Whether this connection asks the server to refuse writes. See
    /// <see cref="ConnectionInfo.ReadOnly"/> for what that does and does not promise.</summary>
    public static bool IsReadOnly(ConnectionInfo info) => info.ReadOnly;

    /// <summary>How this connection's timeout reads in a sentence — "no limit" rather than "0 seconds",
    /// because an absent limit is a fact about the connection and not a number (§1.7).</summary>
    public static string TimeoutLabel(ConnectionInfo info)
        => TimeoutSeconds(info) is var seconds && seconds > NoTimeout
            ? seconds.ToString(CultureInfo.InvariantCulture) + " s"
            : "no limit";

    /// <summary>
    /// The one-sentence "what this leaves open" for the dialog, in the register
    /// <see cref="TlsPolicy.Advice"/> established: it names what was actually arranged and does not overclaim.
    /// Empty when neither setting is on, so the hint is absent rather than reassuring.
    /// </summary>
    public static string Advice(ConnectionInfo info)
        => Advice(IsReadOnly(info), TimeoutSeconds(info));

    /// <summary>The same advice from the two values alone, so a dialog can describe what the user has typed
    /// without assembling a whole <see cref="ConnectionInfo"/> on every keystroke.</summary>
    public static string Advice(bool readOnly, int timeoutSeconds)
    {
        var seconds = timeoutSeconds > NoTimeout ? timeoutSeconds : NoTimeout;
        if (!readOnly && seconds == NoTimeout) return "";
        if (readOnly && seconds > NoTimeout)
            return $"The server refuses writes on this connection and cancels a statement after {seconds} s. "
                   + "Read-only stops mistakes, not a determined user: it can be lifted with a SET, so it is "
                   + "not a substitute for a role without write privileges.";
        if (readOnly)
            return "The server refuses writes on this connection. That stops mistakes, not a determined "
                   + "user: it can be lifted with a SET, so it is not a substitute for a role without write "
                   + "privileges.";
        return $"The server cancels a statement on this connection after {seconds} s. Bearing itself still "
               + "waits as long as a query takes — this is the server's limit, not the client's.";
    }
}
