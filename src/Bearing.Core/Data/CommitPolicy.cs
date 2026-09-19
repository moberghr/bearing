using System;
using System.Globalization;

namespace Bearing.Core.Data;

/// <summary>
/// What <see cref="ConnectionInfo.ManualCommit"/> means, and the two clocks that bound a transaction left
/// open (#131). Pure, so the wording a user is warned with and the thresholds it resolves to are testable
/// without a server (§2.5).
/// <para>
/// <b>Deliberately not part of <see cref="SessionPolicy"/>.</b> That class holds the two settings that ride
/// the <i>startup packet</i> — read-only and the statement timeout — and the whole point of §1.9 is that
/// they reach every physical connection because the server applies them. Manual-commit reaches no server at
/// all: it is a connection Bearing keeps checked out of the pool and a <c>COMMIT</c> it declines to send.
/// Folding it in would blur the one distinction that note exists to draw.
/// </para>
/// </summary>
public static class CommitPolicy
{
    /// <summary>Whether writes on this connection are held rather than committed.</summary>
    public static bool IsManualCommit(ConnectionInfo info) => info.ManualCommit;

    /// <summary>
    /// How many transactions may be open at once on one (connection, database) pool.
    /// <para>
    /// A held transaction is a pooled connection checked out for as long as the user leaves it open, and a
    /// pool holds <c>PostgresConnectionString.DefaultMaxPoolSize</c> (10) physical connections. Without a
    /// ceiling, tabs that have each written once exhaust the pool and every other operation on that database
    /// — a page fetch, a count, the schema tree — blocks with nothing on screen to say why. The cap leaves
    /// most of the pool for ordinary work and refuses the next transaction with a sentence instead.
    /// </para>
    /// </summary>
    public const int MaxOpenPerPool = 4;

    /// <summary>Minutes of inactivity after which an open transaction is called out, or 0 for never.</summary>
    public const int DefaultIdleWarnMinutes = 5;

    /// <summary>
    /// Minutes of inactivity after which an open transaction is rolled back, or 0 for never.
    /// <para>
    /// The second clock, not the first: a warning the user never sees is no bound on how long locks are held,
    /// and that is the hazard the panel in #101 exists to make visible. A rollback is loud and durable when
    /// it happens — the user is by definition not watching, so a status line that scrolls away is not enough.
    /// </para>
    /// </summary>
    public const int DefaultIdleRollbackMinutes = 15;

    /// <summary>A threshold as a timespan, or null when it is off (zero or negative — a clock that has
    /// already run out is not a setting anyone meant, the reading <see cref="SessionPolicy.TimeoutSeconds"/>
    /// already takes).</summary>
    public static TimeSpan? Threshold(int minutes)
        => minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

    /// <summary>Whether an open transaction idle for <paramref name="idle"/> has earned the amber mark.</summary>
    public static bool IsStale(TimeSpan idle, int warnMinutes)
        => Threshold(warnMinutes) is { } t && idle >= t;

    /// <summary>Whether an open transaction idle for <paramref name="idle"/> is to be rolled back.</summary>
    public static bool IsAbandoned(TimeSpan idle, int rollbackMinutes)
        => Threshold(rollbackMinutes) is { } t && idle >= t;

    /// <summary>How long a transaction has been open, in the chip's register: whole minutes past a minute,
    /// seconds below it. Never "0m", which reads as stopped.</summary>
    public static string AgeLabel(TimeSpan age)
        => age < TimeSpan.FromMinutes(1)
            ? ((int)Math.Max(age.TotalSeconds, 0)).ToString(CultureInfo.InvariantCulture) + "s"
            : ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";

    /// <summary>
    /// The one-sentence "what this leaves open" for the connection dialog, in the register
    /// <see cref="SessionPolicy.Advice"/> and <see cref="TlsPolicy.Advice"/> established: it names what was
    /// actually arranged and does not overclaim. Empty when the setting is off.
    /// </summary>
    public static string Advice(bool manualCommit, bool readOnly = false)
    {
        if (!manualCommit) return "";
        return readOnly
            ? "Writes are held until you commit them — though nothing will open a transaction on this "
              + "connection while it is also read-only, because nothing writes."
            : "A write opens a transaction and stays uncommitted until you press Commit. Reading does not "
              + "open one. An open transaction holds locks on the server until it ends.";
    }
}
