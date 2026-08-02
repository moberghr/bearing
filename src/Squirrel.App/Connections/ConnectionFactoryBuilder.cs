using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;

namespace Squirrel.App.Connections;

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
    public static async Task<(IDbProvider Provider, IDbConnectionFactory Factory, Credential Credential)> BuildAsync(
        IProviderRegistry providers, CredentialResolver? credentials, ConnectionInfo info,
        bool forceRefresh, CancellationToken ct)
    {
        var credential = credentials is null
            ? new Credential(null, null)
            : await credentials.ResolveAsync(info, forceRefresh, ct);
        var provider = providers.Get(info.ProviderId);
        var factory = provider.CreateConnectionFactory(info, credential.Secret);
        return (provider, factory, credential);
    }
}
