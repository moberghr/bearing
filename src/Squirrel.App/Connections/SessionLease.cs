using System;
using System.Threading;

namespace Squirrel.App.Connections;

/// <summary>
/// A hold on a <see cref="ConnectionSession"/> that keeps it alive for the duration of a running query:
/// while any lease is outstanding the manager will not dispose the session (on evict, connection edit,
/// database switch, idle timeout, or a competing rebuild). Dispose exactly once when the work completes —
/// the session is torn down promptly if it was retired while in use. Idempotent.
/// </summary>
public sealed class SessionLease : IDisposable
{
    private ConnectionSessionManager? _manager;

    internal SessionLease(ConnectionSessionManager manager, ConnectionSession session)
    {
        _manager = manager;
        Session = session;
    }

    /// <summary>The leased session; use its executor/metadata while this lease is held.</summary>
    public ConnectionSession Session { get; }

    public void Dispose()
    {
        // Release at most once even if Dispose is called twice.
        Interlocked.Exchange(ref _manager, null)?.ReleaseLease(Session);
    }
}
