using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Workspace;

namespace Bearing.Sessions;

/// <summary>
/// Resolves the secret a connection authenticates with, according to its <see cref="CredentialKind"/>:
/// a fixed <see cref="ISecretStore"/> password, a value prompted from the user, or a freshly-minted Entra
/// token — or, for <see cref="CredentialKind.Integrated"/> and <see cref="CredentialKind.AwsProfile"/>, no
/// secret at all. Prompted passwords and
/// tokens are cached <b>in memory only</b>, keyed by connection id, for the life of the app session —
/// never persisted. <see cref="Invalidate"/> drops a cache entry so the next
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
    private readonly Func<ConnectionInfo, bool> _takesPassword;
    private readonly ConcurrentDictionary<Guid, Credential> _cache = new();

    /// <param name="takesPassword">Whether the engine <c>info</c> connects to takes a password at all
    /// (<see cref="IDbProvider.SupportsPasswordCredentials"/>). Null = every engine does — the behaviour before
    /// an engine without one existed. Where it answers false, a password kind (a hand-edited
    /// <see cref="CredentialKind.StoredPassword"/> or <see cref="CredentialKind.Prompt"/>) resolves to no secret
    /// without reading the store or asking anyone, and <see cref="CanRefresh"/> says no.</param>
    public CredentialResolver(
        Func<ISecretStore?> secrets, ICredentialPrompt? prompt, IEntraTokenProvider tokens,
        Func<DateTimeOffset>? now = null, Func<ConnectionInfo, bool>? takesPassword = null)
    {
        _secrets = secrets;
        _prompt = prompt;
        _tokens = tokens;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _takesPassword = takesPassword ?? (_ => true);
    }

    /// <summary>
    /// Whether resolving <paramref name="info"/> again after an authentication failure could produce something
    /// different — the run's one retry. A kind that resolves to the same value twice must not be retried: the
    /// second attempt fails identically, and the user reads "Reauthenticating…" for an authentication that never
    /// happened. One arm per kind, with its reason, because the arms are not obvious from the outside.
    /// </summary>
    public bool CanRefresh(ConnectionInfo info)
    {
        // An engine with no password (DynamoDB) has nothing to re-ask for, whatever kind a hand-edited
        // project.json names: ResolveAsync hands it no secret either way.
        if (!_takesPassword(info)) return false;

        return info.CredentialKind switch
        {
            // Re-asking the user, and re-minting a token, are both things a retry can actually change.
            CredentialKind.Prompt or CredentialKind.EntraToken => true,

            // A stored password that didn't change would just fail identically — unless the store can't hold
            // passwords at all (no keyring), where "stored password" really means "there is nothing stored" and
            // the retry is what gets the user prompted for one.
            CredentialKind.StoredPassword => _secrets() is { CanStore: false },

            // The OS identity: nothing to re-prompt for and nothing to re-mint, so a retry would send the exact
            // same handshake. Spelled out rather than left to the default arm because the case it must not fall
            // into is right above it — an Integrated connection swept into the no-keyring branch would
            // "reauthenticate" by doing nothing, twice.
            CredentialKind.Integrated => false,

            // The AWS SDK resolves the profile; Bearing holds nothing to re-ask for or re-mint. A retry would
            // sign the same request with what the SDK already has — an expired SSO session is renewed by
            // `aws sso login` outside Bearing, not by a second attempt here.
            CredentialKind.AwsProfile => false,

            _ => false,
        };
    }

    /// <summary>Resolve a credential for <paramref name="info"/>. With <paramref name="forceRefresh"/> the
    /// in-memory cache is bypassed (re-prompt / re-mint). Throws <see cref="ConnectionFailedException"/> when
    /// a prompt is cancelled or unavailable.</summary>
    public async Task<Credential> ResolveAsync(ConnectionInfo info, bool forceRefresh, CancellationToken ct)
    {
        // An engine that takes no password gets none, whatever kind the record names: never a store read and
        // never a prompt for a secret the provider would ignore (§1.1). Ahead of the switch so a hand-edited
        // StoredPassword / Prompt on such an engine cannot reach either.
        if (info.CredentialKind is CredentialKind.StoredPassword or CredentialKind.Prompt && !_takesPassword(info))
            return new Credential(null, null);

        switch (info.CredentialKind)
        {
            case CredentialKind.StoredPassword:
                {
                    var store = _secrets();
                    string? pw;
                    try
                    {
                        pw = store is null ? null : await store.GetPasswordAsync(info.Id, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The store *has* a password for this connection but could not hand it over (a keyring
                        // that errored rather than answered "no such item"). Failing loudly is the point: the
                        // alternative — treating it as "none stored" — sends a passwordless connect and then
                        // prompts the user for a password already sitting in their keyring.
                        throw new ConnectionFailedException(
                            $"Could not read the stored password for '{info.Name}': {SafeErrorText.Of(ex)}", ex);
                    }
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

            case CredentialKind.Integrated:
                // The OS identity authenticates the connection, so there is nothing to resolve: the driver
                // negotiates it (SqlClient's Integrated Security=true) and the factory deliberately sends
                // neither user nor password. Deliberately ahead of any store or prompt code — this kind
                // must never read the secret store, never write to it, and never ask the user for anything
                // (§1.1). No expiry either: the process's own token is refreshed by the OS, not by us, so
                // the session manager has no disconnect-before-expiry to schedule.
                //
                // Not cached, because there is nothing to cache; and a forceRefresh retry would re-mint
                // nothing, which is why CanRefresh excludes this kind.
                return new Credential(null, null);

            case CredentialKind.AwsProfile:
                // The AWS SDK resolves the profile itself (SSO, assumed role, or keys in ~/.aws) when the
                // provider's factory builds its client, so there is nothing for Bearing to hand over. The
                // Integrated arrangement for the same reason: ahead of any store or prompt code, because this
                // kind must never read the secret store, never write to it, and never ask the user for
                // anything — Bearing holds no AWS secret at all (§1.1). No expiry: the SDK refreshes what it
                // resolved, so the session manager has no disconnect-before-expiry to schedule.
                return new Credential(null, null);

            case CredentialKind.EntraToken:
                {
                    // A token cached before the az-login user box was ticked carries no user, and serving it
                    // would connect with an empty one — the edit that ticked it doesn't reach this cache.
                    if (!forceRefresh && _cache.TryGetValue(info.Id, out var cached)
                        && !IsExpiring(cached.ExpiresAt, _now(), RefreshSkew)
                        && (cached.User is not null || !EntraTokenProvider.WantsUser(info)))
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
