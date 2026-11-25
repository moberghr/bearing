using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    // Ids whose next resolve must re-ask rather than serve what it has (set by Invalidate — the retry after an
    // authentication failure, or an expiry). One-shot: the resolve that honours it clears it.
    private readonly ConcurrentDictionary<Guid, bool> _refresh = new();
    // A password typed at the prompt for a stored-password connection, waiting for proof that it works before
    // it is written to the keychain (SaveIfWorkedAsync). Never written at prompt time: a mistyped password must
    // not be stored.
    private readonly ConcurrentDictionary<Guid, string> _pendingSave = new();
    // Bumped by every Invalidate: lets a caller that failed with an older credential see that someone else
    // already refreshed it, rather than refreshing (and prompting) a second time for the same failure.
    private readonly ConcurrentDictionary<Guid, int> _generation = new();
    // The password prompt in flight for a connection, so callers resolving at the same time wait for its answer
    // instead of asking again — one prompt per failure, however many pools or tree reads hit it at once.
    private readonly ConcurrentDictionary<Guid, Task<Credential>> _prompting = new();

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
        Ssh = new SshTunnels(secrets, prompt);
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

            // With someone to ask, a retry asks: the saved password (or the passwordless first attempt, when
            // nothing is saved — a connection just imported on a new machine) was refused, and the answer is
            // saved to the keychain once it works (SaveIfWorkedAsync), replacing the stale one. Without a prompt
            // (the CLI) a stored password that didn't change would just fail identically — unless the store
            // can't hold passwords at all, the arrangement before prompt-and-save existed.
            // A domain account's password is kept the same way, so the same answer.
            CredentialKind.StoredPassword or CredentialKind.DomainPassword
                => _prompt is not null || _secrets() is { CanStore: false },

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

    /// <summary>SSH tunnels for connections that have one, sharing this resolver's secret store and prompt
    /// (so the CLI, which has no prompt, gets none there either).</summary>
    public SshTunnels Ssh { get; }

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

        // An Invalidate since the last resolve is a forced refresh too: the connect recipe always resolves with
        // forceRefresh false, so this is how the retry after an authentication failure reaches the prompt. The
        // password kinds take it under the prompt lock below, together with joining or claiming the prompt.
        if (info.CredentialKind is not (CredentialKind.StoredPassword or CredentialKind.DomainPassword)
            && _refresh.TryRemove(info.Id, out _))
            forceRefresh = true;

        switch (info.CredentialKind)
        {
            case CredentialKind.StoredPassword:
            case CredentialKind.DomainPassword:
                {
                    // One lock over taking the refresh flag and joining or claiming the prompt, so no resolve can slip
                    // between them: a caller either waits for the prompt in flight, or is the one asking.
                    Task<Credential>? asking;
                    TaskCompletionSource<Credential>? mine;
                    lock (_promptGate)
                    {
                        if (_refresh.TryRemove(info.Id, out _)) forceRefresh = true;
                        // A forced refresh with someone to ask goes straight to the prompt: whatever the keychain
                        // holds is what was just refused, and reading it again would fail identically.
                        asking = JoinOrClaim(info.Id, claim: forceRefresh && _prompt is not null, out mine);
                    }
                    // Someone is already being asked for this connection's password: that answer is the one to use.
                    if (asking is not null) return await asking.WaitAsync(ct);

                    var store = _secrets();
                    if (mine is not null) return await AskAsync(info, store, mine, ct);

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

                    // No stored secret: either none was ever set (a connection imported on a new machine), or
                    // this machine has no keyring. Reuse one typed earlier this session if we have it.
                    //
                    // The first attempt deliberately goes out with no password rather than prompting, so a
                    // passwordless connection (trust auth, .pgpass) still connects without being interrogated;
                    // the retry after it is refused asks (CanRefresh).
                    //
                    // A domain account has no passwordless login, and its factory refuses to open without a
                    // password (it would otherwise log in as the OS identity), so that kind asks at once
                    // instead of sending a first attempt that cannot succeed.
                    if (!forceRefresh)
                    {
                        if (_cache.TryGetValue(info.Id, out var remembered)) return remembered;
                        if (info.CredentialKind != CredentialKind.DomainPassword) return new Credential(null, null);
                    }

                    if (_prompt is null) return new Credential(null, null);
                    lock (_promptGate) asking = JoinOrClaim(info.Id, claim: true, out mine);
                    if (asking is not null) return await asking.WaitAsync(ct);
                    return await AskAsync(info, store, mine!, ct);
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
                // The AWS SDK resolves the profile itself, in this process (SSO, assumed role, or keys in
                // ~/.aws) when the provider's factory builds its client, so there is nothing for Bearing to hand over. The
                // Integrated arrangement for the same reason: ahead of any store or prompt code, because this
                // kind must never read the secret store, never write to it, and never ask the user for
                // anything — no Bearing code holds an AWS secret (§1.1, §1.9c). No expiry: the SDK refreshes what it
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

    private readonly object _promptGate = new();

    /// <summary>Under <see cref="_promptGate"/>: the prompt already in flight for <paramref name="id"/> (join it),
    /// or — when <paramref name="claim"/> — a fresh one registered for the caller to answer.</summary>
    private Task<Credential>? JoinOrClaim(Guid id, bool claim, out TaskCompletionSource<Credential>? mine)
    {
        mine = null;
        if (_prompting.TryGetValue(id, out var inFlight)) return inFlight;
        if (!claim) return null;
        mine = new TaskCompletionSource<Credential>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompting[id] = mine.Task;
        // Observed, so a cancelled prompt nobody else waited on is not an unobserved task exception.
        _ = mine.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return null;
    }

    /// <summary>Ask for a stored-password connection's password, as the claimant of <paramref name="answer"/>;
    /// everyone who joined meanwhile gets the same answer (or the same cancellation).</summary>
    private async Task<Credential> AskAsync(
        ConnectionInfo info, ISecretStore? store, TaskCompletionSource<Credential> answer, CancellationToken ct)
    {
        try
        {
            var typed = await _prompt!.RequestPasswordAsync(info, null, ct)
                        ?? throw new ConnectionFailedException($"Password entry cancelled for '{info.Name}'.");
            var prompted = new Credential(typed, null);
            _cache[info.Id] = prompted;
            // Saved once a connect with it succeeds — never now. A store that can't keep a secret (no keyring,
            // demo) gets nothing to save: the password stays in memory for the session.
            if (store is { CanStore: true }) _pendingSave[info.Id] = typed;
            else _pendingSave.TryRemove(info.Id, out _);
            answer.SetResult(prompted);
            return prompted;
        }
        catch (Exception ex)
        {
            answer.TrySetException(ex);
            throw;
        }
        finally
        {
            lock (_promptGate) _prompting.TryRemove(new KeyValuePair<Guid, Task<Credential>>(info.Id, answer.Task));
        }
    }

    /// <summary>Drop the cached credential for a connection so the next resolve re-prompts / re-mints — and,
    /// for a stored-password connection with a prompt, asks rather than re-reading the keychain.</summary>
    public void Invalidate(Guid id)
    {
        _cache.TryRemove(id, out _);
        _refresh[id] = true;
        _generation.AddOrUpdate(id, 1, (_, g) => g + 1);
    }

    /// <summary>How many times this connection's credential has been invalidated. Read before an attempt and
    /// handed to <see cref="InvalidateIfUnchanged"/> after it failed.</summary>
    public int Generation(Guid id) => _generation.TryGetValue(id, out var g) ? g : 0;

    /// <summary>
    /// <see cref="Invalidate"/>, unless another caller already did since <paramref name="generation"/> was read
    /// — then the credential that failed here has been replaced (or is being asked for) and the retry should
    /// use that one, not ask again. This is what keeps two failures racing on one connection to one prompt.
    /// </summary>
    public bool InvalidateIfUnchanged(Guid id, int generation)
    {
        lock (_invalidateGate)
        {
            if (Generation(id) != generation) return false;
            Invalidate(id);
            return true;
        }
    }

    private readonly object _invalidateGate = new();

    /// <summary>Where a password typed at the prompt was or wasn't saved, as a sentence for the status line.
    /// Called off the UI thread. The sentence never holds the password.</summary>
    public Action<ConnectionInfo, string>? PasswordSaveNotice { get; set; }

    /// <summary>
    /// The connect recipe proved <paramref name="used"/> logs in: if it is a password typed at the prompt for a
    /// stored-password connection, write it to the keychain now (replacing a stale one), so the next session
    /// doesn't ask. Anything else — a password read from the keychain, an Entra token, the
    /// <see cref="CredentialKind.Prompt"/> kind, a store that can't keep secrets — has nothing pending and
    /// returns at once. A store that refuses or fails keeps the password in memory for the session and says so
    /// once; it never fails the connect that just worked.
    /// </summary>
    public async Task SaveIfWorkedAsync(ConnectionInfo info, Credential used)
    {
        if (used.Secret is not { } secret) return;
        // Only the exact password that just worked, and only once: the pair is removed as it is claimed, so two
        // pools connecting with it at once save it once.
        if (!_pendingSave.TryRemove(new KeyValuePair<Guid, string>(info.Id, secret))) return;
        if (_secrets() is not { CanStore: true } store) return;

        try
        {
            await store.SetPasswordAsync(info.Id, secret, CancellationToken.None);
            PasswordSaveNotice?.Invoke(info, $"Saved the password for '{info.Name}' to the keychain.");
        }
        catch (SecretStorageRefusedException)
        {
            // §1.1: the keychain couldn't be reached — with its own reason, never a guessed cause.
            var why = store.UnavailableReason is { Length: > 0 } reason ? $" ({reason})" : "";
            PasswordSaveNotice?.Invoke(info,
                $"Connected to '{info.Name}', but the keychain couldn't be reached{why}, so the password was not "
                + "saved — it is kept until Bearing closes.");
        }
        catch (Exception ex)
        {
            PasswordSaveNotice?.Invoke(info,
                $"Connected to '{info.Name}', but the password could not be saved to the keychain: "
                + $"{SafeErrorText.Of(ex)}. It is kept until Bearing closes.");
        }
    }

    /// <summary>True when an expiry is set and now is at or within <paramref name="skew"/> of it. A null
    /// expiry (fixed / prompted password) never expires. Shared by the resolver's refresh check and the
    /// session manager's disconnect-before-expiry sweep.</summary>
    public static bool IsExpiring(DateTimeOffset? expiresAt, DateTimeOffset now, TimeSpan skew)
        => expiresAt is { } exp && now >= exp - skew;
}
