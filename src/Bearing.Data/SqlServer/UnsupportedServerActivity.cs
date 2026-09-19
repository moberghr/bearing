using Bearing.Core.Data;

namespace Bearing.Data.SqlServer;

/// <summary>
/// The <see cref="IServerActivity"/> of an engine that does not implement one. Every member throws, which is
/// the point: a caller reaching this has ignored <see cref="IDbProvider.SupportsServerActivity"/>, and a
/// quiet empty list would turn that bug into a sentence about the user's server instead of an exception in
/// the one place that can be fixed.
/// </summary>
internal sealed class UnsupportedServerActivity : IServerActivity
{
    public static UnsupportedServerActivity Instance { get; } = new();

    private static NotSupportedException Unsupported()
        => new("This engine does not report server activity; check IDbProvider.SupportsServerActivity first.");

    public Task<ServerActivity> GetActivityAsync(ActivityFilter filter, CancellationToken ct) => throw Unsupported();

    public Task<bool> CancelBackendAsync(int pid, CancellationToken ct) => throw Unsupported();

    public Task<bool> TerminateBackendAsync(int pid, CancellationToken ct) => throw Unsupported();
}
