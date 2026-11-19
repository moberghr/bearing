using System;
using System.Globalization;
using System.Linq;
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
internal static class ConnectionFactoryBuilder
{
    public static async Task<(IDbProvider Provider, IDbConnectionFactory Factory, Credential Credential, SshTunnelHandle? Tunnel)> BuildAsync(
        IProviderRegistry providers, CredentialResolver? credentials, ConnectionInfo info,
        bool forceRefresh, CancellationToken ct)
    {
        var credential = credentials is null
            ? new Credential(null, null)
            : await credentials.ResolveAsync(info, forceRefresh, ct);
        var provider = providers.Get(info.ProviderId);
        var (target, tunnel) = await RouteAsync(credentials, provider, credential.ApplyTo(info), ct);
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
    /// Build a throwaway factory with the credential the caller has in hand — the connection dialog's Test, with
    /// a typed password and SSH secret not saved yet — test it, and tear it all down. Same route as
    /// <see cref="BuildAsync"/>, so a test reaches the server the way a connect will.
    /// </summary>
    public static async Task<bool> TestAsync(IProviderRegistry providers, CredentialResolver? credentials,
        ConnectionInfo info, string? password, string? sshSecret, CancellationToken ct)
    {
        var provider = providers.Get(info.ProviderId);
        var (target, tunnel) = await RouteAsync(credentials, provider, info, ct, sshSecret);
        try
        {
            var factory = provider.CreateConnectionFactory(target, password);
            try
            {
                return await factory.TestConnectionAsync(ct);
            }
            finally
            {
                await factory.DisposeAsync();
            }
        }
        finally
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// What the driver should dial: <paramref name="info"/> itself, or — for a connection with an
    /// <see cref="ConnectionInfo.SshTunnel"/> — the same record pointed at a freshly opened tunnel's loopback
    /// end. The caller owns the returned tunnel and disposes it after the factory built on it. Only the
    /// factory sees the rewritten host; sessions, messages and pool identity keep the real one.
    /// </summary>
    internal static async Task<(ConnectionInfo Target, SshTunnelHandle? Tunnel)> RouteAsync(
        CredentialResolver? credentials, IDbProvider provider, ConnectionInfo info, CancellationToken ct,
        string? typedSshSecret = null)
    {
        if (info.SshTunnel is null)
        {
            return (info, null);
        }
        if (credentials is null)
        {
            throw new ConnectionFailedException($"'{info.Name}' needs an SSH tunnel, and nothing here can open one.");
        }
        var (host, port) = ForwardTarget(provider, info);
        var tunnel = await credentials.Ssh.OpenAsync(info, host, port, ct, typedSshSecret);
        return (tunnel.Route(info), tunnel);
    }

    /// <summary>
    /// Where the SSH server should forward to: the connection's host and port as the SSH server sees them. A
    /// port of 0 (SQL Server's "default") is the engine's declared default port, since a forward needs a real
    /// one. A SQL Server named instance (<c>host\instance</c>) is refused: its port is found through the SQL
    /// Browser service over UDP, which a TCP forward cannot carry, and the loopback address would drop the
    /// instance name anyway.
    /// </summary>
    internal static (string Host, int Port) ForwardTarget(IDbProvider provider, ConnectionInfo info)
    {
        var host = info.Host.Trim();
        if (ConnectionEndpoint.IsNamedInstance(host))
        {
            throw new ConnectionFailedException(
                $"'{info.Name}' names a SQL Server instance ({host}), which can't be reached through an SSH tunnel: "
                + "its port is looked up through the SQL Browser service. Give the server's host and the instance's port instead.");
        }
        if (info.Port is > 0 and <= 65535)
        {
            return (host, info.Port);
        }
        var declared = provider.ConnectionFields
            .FirstOrDefault(f => string.Equals(f.Key, "Port", StringComparison.OrdinalIgnoreCase))?.Default;
        if (int.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
        {
            return (host, port);
        }
        throw new ConnectionFailedException($"'{info.Name}' has no port to forward to through the SSH tunnel.");
    }
}
