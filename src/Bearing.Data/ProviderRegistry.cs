using Bearing.Core.Data;
using Bearing.Data.Postgres;

namespace Bearing.Data;

/// <summary>Default provider registry. v1 registers only PostgreSQL.</summary>
public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IDbProvider> _providers;

    public ProviderRegistry(IEnumerable<IDbProvider>? providers = null)
    {
        var list = providers?.ToList() ?? new List<IDbProvider> { new PostgresProvider() };
        _providers = list.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IDbProvider Get(string providerId) =>
        _providers.TryGetValue(providerId, out var p)
            ? p
            : throw new KeyNotFoundException($"No database provider registered for '{providerId}'.");

    public IReadOnlyCollection<IDbProvider> All => _providers.Values;
}
