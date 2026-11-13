using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;

namespace Bearing.Sessions;

/// <summary>
/// The one connect recipe shared by <see cref="ConnectionSessionManager"/> (query sessions) and
/// <see cref="SchemaBrowser"/> (metadata reads): resolve the credential (stored password / prompt / Entra
/// token, keyed by connection id so a database-cloned <see cref="ConnectionInfo"/> reuses the same
/// credential), resolve the provider, and create a connection factory. The returned
/// <see cref="Credential"/> carries an optional expiry the caller records for disconnect-before-expiry.
/// Callers keep their own pooling/lifecycle.
/// </summary>
public static class ConnectionFactoryBuilder
{
    public static async Task<(IDbProvider Provider, IDbConnectionFactory Factory, Credential Credential, SshTunnelHandle? Tunnel)> BuildAsync(
        IProviderRegistry providers, CredentialResolver? credentials, ConnectionInfo info,
        bool forceRefresh, CancellationToken ct)
    {
        var credential = credentials is null
            ? new Credential(null, null)
            : await credentials.ResolveAsync(info, forceRefresh, ct);
        var provider = providers.Get(info.ProviderId);
        var (target, tunnel) = await RouteAsync(credentials, credential.ApplyTo(info), ct);
        try
        {
            return (provider, provider.CreateConnectionFactory(target, credential.Secret), credential, tunnel);
        }
        catch
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
            throw;
        }
    }

    /// <summary>
    /// What the driver should dial: <paramref name="info"/> itself, or — for a connection with an
    /// <see cref="ConnectionInfo.SshTunnel"/> — the same record pointed at a freshly opened tunnel's loopback
    /// end. The caller owns the returned tunnel and disposes it after the factory built on it. Only the
    /// factory sees the rewritten host; sessions, messages and pool identity keep the real one.
    /// </summary>
    public static async Task<(ConnectionInfo Target, SshTunnelHandle? Tunnel)> RouteAsync(
        CredentialResolver? credentials, ConnectionInfo info, CancellationToken ct, string? typedSshSecret = null)
    {
        if (info.SshTunnel is null)
        {
            return (info, null);
        }
        if (credentials is null)
        {
            throw new ConnectionFailedException($"'{info.Name}' needs an SSH tunnel, and nothing here can open one.");
        }
        var tunnel = await credentials.Ssh.OpenAsync(info, ct, typedSshSecret);
        return (tunnel.Route(info), tunnel);
    }
}
