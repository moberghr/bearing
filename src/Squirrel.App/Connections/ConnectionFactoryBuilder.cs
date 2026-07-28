using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;
using Squirrel.Core.Workspace;

namespace Squirrel.App.Connections;

/// <summary>
/// The one connect recipe shared by <see cref="ConnectionSessionManager"/> (query sessions) and
/// <see cref="SchemaBrowser"/> (metadata reads): fetch the password from the secret store (keyed by
/// connection id, so a database-cloned <see cref="ConnectionInfo"/> reuses the same credential),
/// resolve the provider, and create a connection factory. Callers keep their own pooling/lifecycle.
/// </summary>
internal static class ConnectionFactoryBuilder
{
    public static async Task<(IDbProvider Provider, IDbConnectionFactory Factory)> BuildAsync(
        IProviderRegistry providers, ISecretStore? secrets, ConnectionInfo info, CancellationToken ct)
    {
        var password = secrets is null ? null : await secrets.GetPasswordAsync(info.Id, ct);
        var provider = providers.Get(info.ProviderId);
        var factory = provider.CreateConnectionFactory(info, password);
        return (provider, factory);
    }
}
