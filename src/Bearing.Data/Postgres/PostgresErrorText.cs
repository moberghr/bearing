using System;
using Bearing.Core.Data;

namespace Bearing.Data.Postgres;

/// <summary>
/// Turns a driver-level failure into something a person can act on.
/// <para>
/// Npgsql reports a command that ran out of time as <c>Exception while reading from stream</c>, with the
/// <see cref="TimeoutException"/> buried in the inner chain. That message describes the driver's plumbing
/// rather than what happened: the query was still running and something stopped waiting for it. Reported by a
/// user whose query simply took a while.
/// </para>
/// </summary>
public static class PostgresErrorText
{
    /// <summary>
    /// A message for <paramref name="ex"/>, naming the cause where it can be identified and otherwise
    /// falling through to the redacted driver text (<see cref="SafeErrorText.Of"/>, which strips credentials).
    /// </summary>
    public static string Explain(Exception ex)
    {
        if (FindTimeout(ex) is not null)
            return "The query ran longer than the command timeout, so the connection stopped waiting for it. "
                 + "The query itself may still be running on the server. Bearing sets no timeout by default — "
                 + "if one is set, it is a \"CommandTimeout\" entry in this connection's options. Press Esc to "
                 + "cancel a running query instead.";

        return SafeErrorText.Of(ex);
    }

    /// <summary>
    /// The <see cref="TimeoutException"/> anywhere in the chain, or null.
    /// <para>
    /// Walked rather than type-checked at the top, because the timeout is what Npgsql wraps: the exception
    /// the caller sees is an <c>NpgsqlException</c> about a stream, and the timeout is its inner cause. A
    /// bounded walk, so a cyclic or absurdly deep chain cannot spin.
    /// </para>
    /// </summary>
    private static TimeoutException? FindTimeout(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; depth++, ex = ex.InnerException)
            if (ex is TimeoutException timeout) return timeout;
        return null;
    }
}
