using System.Threading;
using System.Threading.Tasks;
using Squirrel.Core.Data;

namespace Squirrel.App.Connections;

/// <summary>Obtains a Microsoft Entra access token to use as the database password (Azure Database for
/// PostgreSQL with Entra auth). The returned <see cref="Credential"/> carries the token's expiry.</summary>
public interface IEntraTokenProvider
{
    Task<Credential> GetTokenAsync(ConnectionInfo info, CancellationToken ct);
}
