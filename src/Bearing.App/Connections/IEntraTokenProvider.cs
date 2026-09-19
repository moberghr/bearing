using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>Obtains a Microsoft Entra access token for a connection to authenticate with — Azure Database
/// for PostgreSQL and Azure SQL both, minted for that engine's own audience
/// (<see cref="ProviderTraits.EntraResource"/>). The returned <see cref="Credential"/> carries the token's
/// expiry. Where the token then goes is the provider's choice, not this interface's: it is handed over as
/// the connection's secret and Npgsql reads it as the password while SqlClient wants it on the connection
/// object.</summary>
public interface IEntraTokenProvider
{
    Task<Credential> GetTokenAsync(ConnectionInfo info, CancellationToken ct);
}
