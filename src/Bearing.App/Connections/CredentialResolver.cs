using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Workspace;

namespace Bearing.App.Connections;

/// <summary>
/// Resolves the secret a connection authenticates with, according to its <see cref="CredentialKind"/>:
/// a fixed <see cref="ISecretStore"/> password, a value prompted from the user, or a freshly-minted Entra
/// token. Prompted passwords and tokens are cached <b>in memory only</b>, keyed by connection id, for the
/// life of the app session — never persisted. <see cref="Invalidate"/> drops a cache entry so the next
/// resolve re-prompts / re-mints (used on auth failure and expiry eviction).
/// </summary>
public sealed class CredentialResolver
{
    /// <summary>How far ahead of a token's real expiry we treat it as needing a refresh on resolve.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly Func<ISecretStore?> _secrets;
    private readonly ICredentialPrompt? _prompt;
    private readonly IEntraTokenProvider _tokens;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<Guid, Credential> _cache = new();

    public CredentialResolver(
        Func<ISecretStore?> secrets, ICredentialPrompt? prompt, IEntraTokenProvider tokens,
        Func<DateTimeOffset>? now = null)
    {
        _secrets = secrets;
        _prompt = prompt;
        _tokens = tokens;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Resolve a credential for <paramref name="info"/>. With <paramref name="forceRefresh"/> the
    /// in-memory cache is bypassed (re-prompt / re-mint). Throws <see cref="ConnectionFailedException"/> when
    /// a prompt is cancelled or unavailable.</summary>
    public async Task<Credential> ResolveAsync(ConnectionInfo info, bool forceRefresh, CancellationToken ct)
    {
        switch (info.CredentialKind)
        {
            case CredentialKind.StoredPassword:
                {
                    var store = _secrets();
                    var pw = store is null ? null : await store.GetPasswordAsync(info.Id, ct);
                    if (pw is not null) return new Credential(pw, null);

                    // No stored secret: either none was ever set, or this machine has no keyring and the password
                    // was deliberately not written (see ISecretStore.CanStore). Reuse one typed earlier this
                    // session if we have it, and on a forced refresh — i.e. the retry after an authentication
                    // failure — ask for one. Kept in memory only, exactly like CredentialKind.Prompt.
                    //
                    // The first attempt deliberately goes out with no password rather than prompting, so a
                    // passwordless connection (trust auth, .pgpass) still connects without being interrogated.
                    if (!forceRefresh)
                        return _cache.TryGetValue(info.Id, out var remembered) ? remembered : new Credential(null, null);

                    if (_prompt is null) return new Credential(null, null);
                    var typed = await _prompt.RequestPasswordAsync(info, null, ct);
                    if (typed is null)
                        throw new ConnectionFailedException($"Password entry cancelled for '{info.Name}'.");
                    var prompted = new Credential(typed, null);
                    _cache[info.Id] = prompted;
                    return prompted;
                }

            case CredentialKind.Prompt:
                {
                    if (!forceRefresh && _cache.TryGetValue(info.Id, out var cached)) return cached;
                    if (_prompt is null)
                        throw new ConnectionFailedException($"No password prompt is available for '{info.Name}'.");
                    var pw = await _prompt.RequestPasswordAsync(info, null, ct);
                    if (pw is null)
                        throw new ConnectionFailedException($"Password entry cancelled for '{info.Name}'.");
                    var cred = new Credential(pw, null);
                    _cache[info.Id] = cred;
                    return cred;
                }

            case CredentialKind.EntraToken:
                {
                    if (!forceRefresh && _cache.TryGetValue(info.Id, out var cached)
                        && !IsExpiring(cached.ExpiresAt, _now(), RefreshSkew))
                        return cached;
                    var cred = await _tokens.GetTokenAsync(info, ct);
                    _cache[info.Id] = cred;
                    return cred;
                }

            default:
                throw new ConnectionFailedException($"Unknown credential kind '{info.CredentialKind}'.");
        }
    }

    /// <summary>Drop the cached credential for a connection so the next resolve re-prompts / re-mints.</summary>
    public void Invalidate(Guid id) => _cache.TryRemove(id, out _);

    /// <summary>True when an expiry is set and now is at or within <paramref name="skew"/> of it. A null
    /// expiry (fixed / prompted password) never expires. Shared by the resolver's refresh check and the
    /// session manager's disconnect-before-expiry sweep.</summary>
    public static bool IsExpiring(DateTimeOffset? expiresAt, DateTimeOffset now, TimeSpan skew)
        => expiresAt is { } exp && now >= exp - skew;
}
