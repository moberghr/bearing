namespace Bearing.Core.Data;

/// <summary>
/// What the app should <em>do</em> about a failed operation, decided by the provider that raised it.
/// Deliberately tiny and engine-neutral: every member exists because some caller branches on it, and
/// nothing here names a SQLSTATE, an error number or a driver — those are the provider's business
/// (<see cref="IDbProvider.Classify"/>), which is the whole point of the type.
/// </summary>
public enum DbErrorKind
{
    /// <summary>Nothing actionable was recognised — report the message as-is. The safe default: a
    /// provider that can't place an error must land here rather than guess at one of the others.</summary>
    Unknown,

    /// <summary>We weren't let in. The caller may offer to re-acquire the credential (re-prompt,
    /// re-mint a token) and retry, instead of just showing the failure.</summary>
    Authentication,

    /// <summary>The operation stopped because it was cancelled, not because it failed. The caller
    /// reports it as the user's own cancel and must not toast it as an error.</summary>
    Canceled,

    /// <summary>The statement's <em>shape</em> was rejected — it couldn't be parsed, or couldn't be used
    /// where the caller tried to use it (wrapped in a count, run as a single statement). The caller
    /// degrades quietly: hide the total, skip the count, don't claim the connection is broken.</summary>
    SyntaxOrShape,
}
