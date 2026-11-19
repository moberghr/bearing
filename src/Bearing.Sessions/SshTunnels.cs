using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Renci.SshNet;
using Renci.SshNet.Common;
using DbConnectionInfo = Bearing.Core.Data.ConnectionInfo;

namespace Bearing.Sessions;

/// <summary>
/// Opens the SSH local forward a <see cref="DbConnectionInfo.SshTunnel"/> connection needs (SSH.NET,
/// in-process). One forward per connection factory: it lives as long as the pool built on it and is disposed
/// after it (<see cref="ConnectionSession"/>, the schema browser's readers).
/// <para>
/// <b>One SSH session per connection and route.</b> Every pool and schema reader of a connection shares one
/// <see cref="SshClient"/>, each with its own forward on it, so a tab connecting while the tree loads (or
/// several databases) does one handshake and asks for the secret once — not one prompt and one SSH login per
/// pool, which would also run into the server's <c>MaxStartups</c>. First opens for the same connection queue
/// behind each other, so the second finds the first's session (and its secret) instead of prompting again.
/// The session is disconnected when its last forward is disposed.
/// </para>
/// <para>
/// <b>Host key: accept-new.</b> An unpinned key is accepted and handed to <see cref="HostKeyPinned"/> so the
/// host can write it into the connection; a key that differs from the pin is refused. With no
/// <see cref="HostKeyPinned"/> (the CLI, which cannot save the project) an unpinned key is refused too —
/// never trusted silently (§1.4). The pin is the one saved now (<see cref="SavedTunnel"/>), not the caller's
/// copy, whichever way the two differ.
/// </para>
/// <para>
/// <b>Secret:</b> the stored one (<see cref="SshSecretId"/>), else one typed earlier this session, else —
/// for password auth, or a key that turns out to need a passphrase, or a refused login — asked for once
/// through <see cref="ICredentialPrompt"/> and saved to the keychain when it worked. A connection whose
/// password is <see cref="CredentialKind.Prompt"/> asked for nothing to be saved, so its SSH secret follows
/// suit: never read from or written to the keychain, held in memory for the session only
/// (<see cref="KeepsSecretInMemoryOnly"/>). Never logged, never in an error message.
/// </para>
/// </summary>
public sealed class SshTunnels
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly Func<ISecretStore?> _secrets;
    private readonly ICredentialPrompt? _prompt;
    private readonly ConcurrentDictionary<Guid, string> _typed = new();
    private readonly object _gate = new();
    private readonly Dictionary<ClientKey, SharedSshClient> _clients = new();
    private readonly ConcurrentDictionary<ClientKey, SemaphoreSlim> _opening = new();

    public SshTunnels(Func<ISecretStore?> secrets, ICredentialPrompt? prompt)
    {
        _secrets = secrets;
        _prompt = prompt;
    }

    /// <summary>Called with the connection and the <c>SHA256:…</c> fingerprint when a host key is seen for
    /// the first time and accepted. Null = refuse unpinned keys.</summary>
    public Func<DbConnectionInfo, string, Task>? HostKeyPinned { get; set; }

    /// <summary>
    /// The connection's tunnel as saved <i>now</i>, by connection id — so a caller holding an older copy of the
    /// record (an explorer node built before the first key was pinned, or before a pin was forgotten) is held
    /// to the pin as it is now. Without it an unpinned copy would accept any key, and a pinned copy would refuse
    /// the key the user just re-trusted. Must be safe to call off the UI thread. Null = the caller's copy is the
    /// record (the CLI).
    /// </summary>
    public Func<Guid, SshTunnel?>? SavedTunnel { get; set; }

    /// <summary>SSH sessions open now, for tests: one per connection and route, however many forwards.</summary>
    internal int OpenSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Open a forward to <paramref name="targetHost"/>:<paramref name="targetPort"/> — the database
    /// as the SSH server sees it — for <paramref name="info"/>. Throws <see cref="ConnectionFailedException"/>
    /// naming the SSH leg on any failure.</summary>
    /// <para><paramref name="typedSecret"/> is a secret typed into the dialog and not yet saved (its Test
    /// button): used as-is on a session of its own, never prompted over, never stored and never shared.</para>
    public async Task<SshTunnelHandle> OpenAsync(DbConnectionInfo info, string targetHost, int targetPort,
        CancellationToken ct, string? typedSecret = null)
    {
        var ssh = info.SshTunnel ?? throw new ArgumentException("Connection has no SSH tunnel.", nameof(info));

        if (!string.IsNullOrEmpty(typedSecret))
        {
            // The Test button checks what was typed, so it must not ride a session logged in with another secret.
            var own = await ConnectAsync(info, ssh, typedSecret, offered: true, ct);
            return Forward(own, info, ssh, targetHost, targetPort);
        }

        var key = new ClientKey(info.Id, ssh with { HostKeyFingerprint = null });
        var opening = _opening.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await opening.WaitAsync(ct);
        SharedSshClient shared;
        try
        {
            shared = TryReuse(key, info, ssh) ?? Register(key, await ConnectAsync(info, ssh, typed: null, offered: false, ct));
        }
        finally
        {
            opening.Release();
        }
        return Forward(shared, info, ssh, targetHost, targetPort);
    }

    /// <summary>The live session for <paramref name="key"/>, with a lease taken — if it is still connected and
    /// the key it met is the one pinned now (a pin saved or forgotten since it connected re-checks the server).
    /// </summary>
    private SharedSshClient? TryReuse(ClientKey key, DbConnectionInfo info, SshTunnel ssh)
    {
        var pinned = PinFor(info.Id, ssh);
        lock (_gate)
        {
            if (!_clients.TryGetValue(key, out var shared))
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(pinned) && !string.Equals(pinned.Trim(), shared.Seen, StringComparison.Ordinal))
            {
                return null;
            }
            return shared.TryAcquire() ? shared : null;
        }
    }

    private SharedSshClient Register(ClientKey key, SharedSshClient shared)
    {
        lock (_gate)
        {
            // A session it replaces (lost, or held to an older pin) stays with the forwards already on it.
            _clients[key] = shared;
        }
        shared.Ended += () => Unregister(key, shared);
        return shared;
    }

    private void Unregister(ClientKey key, SharedSshClient shared)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var current) && ReferenceEquals(current, shared))
            {
                _clients.Remove(key);
            }
        }
    }

    private static SshTunnelHandle Forward(SharedSshClient shared, DbConnectionInfo info, SshTunnel ssh, string targetHost, int targetPort)
    {
        try
        {
            return shared.Forward(targetHost, targetPort);
        }
        catch (Exception ex)
        {
            shared.Release();
            throw new ConnectionFailedException(
                $"SSH to {ssh.User}@{ssh.Host}:{ssh.Port} could not forward to {targetHost}:{targetPort} for '{info.Name}': {SafeErrorText.Of(ex)}", ex);
        }
    }

    /// <summary>Log in to the SSH server, asking for the secret when there is none or it was refused. The
    /// returned session holds one lease, for the forward its caller is about to open.</summary>
    private async Task<SharedSshClient> ConnectAsync(DbConnectionInfo info, SshTunnel ssh, string? typed, bool offered, CancellationToken ct)
    {
        var where = $"{ssh.User}@{ssh.Host}:{ssh.Port}";
        var secretId = SshSecretId.For(info.Id);

        var secret = offered ? typed
            : _typed.TryGetValue(secretId, out var t) ? t
            : KeepsSecretInMemoryOnly(info) ? null
            : await StoredAsync(secretId, ct);
        var prompted = offered;
        if (secret is null && !ssh.UsesPrivateKey)
        {
            secret = await AskAsync(info, $"SSH password for {where}", ct);
            prompted = true;
        }

        while (true)
        {
            try
            {
                var shared = await LogInAsync(info, ssh, secret, ct);
                if (prompted && !offered && secret is not null)
                {
                    await RememberAsync(info, secretId, secret);
                }
                return shared;
            }
            catch (Exception ex) when (!prompted && IsCredentialProblem(ex))
            {
                var what = ssh.UsesPrivateKey ? $"Passphrase for SSH key {ssh.PrivateKeyPath}" : $"SSH password for {where}";
                secret = await AskAsync(info, $"{what} (the last one was refused)", ct);
                prompted = true;
            }
            catch (Exception ex) when (ex is not (ConnectionFailedException or OperationCanceledException))
            {
                throw new ConnectionFailedException($"SSH to {where} failed: {SafeErrorText.Of(ex)}", ex);
            }
        }
    }

    private static bool IsCredentialProblem(Exception ex)
        => ex is SshAuthenticationException or SshPassPhraseNullOrEmptyException
           // A wrong passphrase surfaces from the key parser as a plain SshException.
           || (ex is SshException && ex is not SshConnectionException && ex is not SshOperationTimeoutException
               && ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase));

    private async Task<SharedSshClient> LogInAsync(DbConnectionInfo info, SshTunnel ssh, string? secret, CancellationToken ct)
    {
        AuthenticationMethod auth;
        if (ssh.UsesPrivateKey)
        {
            var path = KeyFile(ssh);
            // Off the caller's thread: an encrypted OpenSSH key runs its bcrypt KDF in this constructor, and the
            // dialog's Test reaches here on the UI thread.
            var key = await Task.Run(
                () => string.IsNullOrEmpty(secret) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, secret), ct);
            auth = new PrivateKeyAuthenticationMethod(ssh.User, key);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(ssh.User, secret ?? "");
        }

        var connection = new Renci.SshNet.ConnectionInfo(ssh.Host.Trim(), ssh.Port, ssh.User, auth) { Timeout = ConnectTimeout };
        // Read before anything is opened: a lookup failure must not leave a client behind.
        var pinned = PinFor(info.Id, ssh);
        var client = new SshClient(connection) { KeepAliveInterval = KeepAlive };
        string? seen = null;
        string? refusal = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256;
            if (string.IsNullOrWhiteSpace(pinned))
            {
                e.CanTrust = HostKeyPinned is not null;
                if (!e.CanTrust)
                {
                    refusal = $"The SSH host key of {ssh.Host} ({seen}) is not trusted yet. Connect once from the Bearing app to pin it.";
                }
            }
            else
            {
                e.CanTrust = string.Equals(pinned.Trim(), seen, StringComparison.Ordinal);
                if (!e.CanTrust)
                {
                    refusal = $"The SSH host key of {ssh.Host} has CHANGED: pinned {pinned.Trim()}, server sent {seen}. "
                              + "Refused. If the change is expected, use \"Forget host key\" on the connection's SSH tab and save.";
                }
            }
        };

        try
        {
            await client.ConnectAsync(ct);
        }
        // Only the handshake sets a refusal, so this names a refused host key rather than the socket error.
        catch (Exception) when (refusal is not null)
        {
            client.Dispose();
            throw new ConnectionFailedException(refusal);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        if (string.IsNullOrWhiteSpace(pinned) && seen is not null && HostKeyPinned is { } pin)
        {
            try
            {
                await pin(info, seen);
            }
            catch
            {
                // Pinning is best-effort; the next connect re-pins.
            }
        }
        return new SharedSshClient(client, seen ?? "");
    }

    /// <summary>The pin this connect is held to: the one saved now for the same server, else the caller's. The
    /// saved record wins both ways — an unpinned copy is held to a pin saved since, and a pinned copy gives way
    /// to a pin forgotten or replaced since. A lookup that fails refuses the connect rather than reading as
    /// "unpinned".</summary>
    private string? PinFor(Guid connectionId, SshTunnel ssh)
    {
        if (SavedTunnel is not { } lookup)
        {
            return ssh.HostKeyFingerprint;
        }
        SshTunnel? saved;
        try
        {
            saved = lookup(connectionId);
        }
        catch (Exception ex)
        {
            throw new ConnectionFailedException($"Could not read the pinned SSH host key of {ssh.Host}: {SafeErrorText.Of(ex)}", ex);
        }
        // A host key belongs to the server, whoever logs in; a pin for another server says nothing here, and
        // neither does a connection not saved (yet) — a dialog's Test.
        return saved is not null && string.Equals(saved.Host.Trim(), ssh.Host.Trim(), StringComparison.OrdinalIgnoreCase)
               && saved.Port == ssh.Port
            ? saved.HostKeyFingerprint
            : ssh.HostKeyFingerprint;
    }

    private async Task<string?> StoredAsync(Guid secretId, CancellationToken ct)
    {
        var store = _secrets();
        if (store is null)
        {
            return null;
        }
        try
        {
            return await store.GetPasswordAsync(secretId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<string> AskAsync(DbConnectionInfo info, string message, CancellationToken ct)
    {
        if (_prompt is null)
        {
            throw new ConnectionFailedException($"{message}: none is stored in the keychain and there is no prompt here.");
        }
        return await _prompt.RequestPasswordAsync(info, message, ct)
               ?? throw new ConnectionFailedException($"SSH secret entry cancelled for '{info.Name}'.");
    }

    /// <summary>The connection's password is asked for at every connect and never saved, so its SSH secret is
    /// not saved either.</summary>
    public static bool KeepsSecretInMemoryOnly(DbConnectionInfo info) => info.CredentialKind == CredentialKind.Prompt;

    private async Task RememberAsync(DbConnectionInfo info, Guid secretId, string secret)
    {
        _typed[secretId] = secret;
        var store = _secrets();
        if (KeepsSecretInMemoryOnly(info) || store is null || !store.CanStore)
        {
            return;
        }
        try
        {
            await store.SetPasswordAsync(secretId, secret, CancellationToken.None);
        }
        catch
        {
            // Kept in memory for the session; the keychain is best-effort here.
        }
    }

    /// <summary>Forget a typed secret (and the stored one) — the connection was deleted.</summary>
    public async Task ForgetAsync(Guid connectionId)
    {
        var secretId = SshSecretId.For(connectionId);
        _typed.TryRemove(secretId, out _);
        var store = _secrets();
        if (store is null)
        {
            return;
        }
        try
        {
            await store.DeleteAsync(secretId, CancellationToken.None);
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>The key file to use: the configured one, or the first default key that exists.</summary>
    private static string KeyFile(SshTunnel ssh)
    {
        if (!string.IsNullOrWhiteSpace(ssh.PrivateKeyPath))
        {
            var path = ExpandHome(ssh.PrivateKeyPath.Trim());
            if (!File.Exists(path))
            {
                throw new ConnectionFailedException($"SSH key file not found: {path}");
            }
            return path;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in DefaultKeys)
        {
            var candidate = Path.Combine(home, ".ssh", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new ConnectionFailedException(
            $"No SSH key file given and none of ~/.ssh/{string.Join(", ", DefaultKeys)} exists.");
    }

    private static readonly string[] DefaultKeys = ["id_ed25519", "id_ecdsa", "id_rsa"];

    /// <summary>Use <paramref name="secret"/> for this connection for the rest of the session — the dialog
    /// saved it (and it may not have reached a keychain). Null or empty forgets it.</summary>
    public void Remember(Guid connectionId, string? secret)
    {
        var secretId = SshSecretId.For(connectionId);
        if (string.IsNullOrEmpty(secret))
        {
            _typed.TryRemove(secretId, out _);
        }
        else
        {
            _typed[secretId] = secret;
        }
    }

    /// <summary>Whether a secret is held in memory for this connection this session, for tests.</summary>
    internal bool RemembersSecretFor(Guid connectionId) => _typed.ContainsKey(SshSecretId.For(connectionId));

    internal static string ExpandHome(string path)
        => path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;

    /// <summary>Which sessions may be shared: the same connection reaching the server the same way. The pin is
    /// left out — it is checked against the key the session met (<see cref="TryReuse"/>).</summary>
    private readonly record struct ClientKey(Guid ConnectionId, SshTunnel Route);
}

/// <summary>
/// One logged-in SSH session and the forwards on it, counted: each <see cref="SshTunnelHandle"/> holds a lease,
/// and the last one released disconnects the session. When the session ends on its own (server closed, a
/// keepalive went unanswered) every forward on it is told (<see cref="SshTunnelHandle.WhenLost"/>).
/// </summary>
internal sealed class SharedSshClient
{
    private readonly SshClient _client;
    private readonly object _gate = new();
    private readonly List<SshTunnelHandle> _handles = [];
    private int _leases = 1;
    private bool _ended;
    private bool _lost;

    internal SharedSshClient(SshClient client, string seen)
    {
        _client = client;
        Seen = seen;
        // SSH.NET raises this only when its message loop ends (server closed, socket error) — the session is
        // over. Our own Disconnect is filtered out by SSH.NET, and by _ended here.
        _client.ErrorOccurred += (_, _) => MarkLost();
    }

    /// <summary>The host key this session met.</summary>
    public string Seen { get; }

    /// <summary>Raised once, when the session is lost or its last lease released: nothing new may use it.</summary>
    public event Action? Ended;

    public bool IsConnected => _client.IsConnected;

    /// <summary>Take a lease for one more forward — false once the session is lost or closing.</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_ended || !_client.IsConnected)
            {
                return false;
            }
            _leases++;
            return true;
        }
    }

    /// <summary>Open a loopback forward on this session, carrying a lease the caller already took.</summary>
    public SshTunnelHandle Forward(string targetHost, int targetPort)
    {
        var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
        SshTunnelHandle handle;
        lock (_gate)
        {
            // SshClient's forwarded-port list is not thread-safe, and forwards come and go from many pools.
            _client.AddForwardedPort(forward);
            handle = new SshTunnelHandle(this, forward);
            _handles.Add(handle);
        }
        try
        {
            forward.Start();
        }
        catch
        {
            Remove(handle, forward);
            throw;
        }
        return handle;
    }

    internal void Remove(SshTunnelHandle handle, ForwardedPortLocal forward)
    {
        lock (_gate)
        {
            _handles.Remove(handle);
            try
            {
                _client.RemoveForwardedPort(forward);
            }
            catch
            {
                // Best-effort: a session already gone has nothing to remove it from.
            }
        }
        forward.Dispose();
    }

    /// <summary>Give back a lease; the last one disconnects the session.</summary>
    public void Release()
    {
        bool last;
        bool raise;
        lock (_gate)
        {
            _leases--;
            last = _leases == 0;
            raise = last && !_ended;
            if (last)
            {
                _ended = true;
            }
        }
        if (raise)
        {
            Ended?.Invoke();
        }
        if (last)
        {
            try
            {
                _client.Disconnect();
            }
            catch
            {
                // Best-effort.
            }
            _client.Dispose();
        }
    }

    /// <summary>A forward failed with the session gone, or SSH.NET's loop ended: tell every forward on it.</summary>
    internal void MarkLost()
    {
        SshTunnelHandle[] handles;
        bool raise;
        lock (_gate)
        {
            if (_lost || (_ended && _leases == 0))
            {
                return;
            }
            _lost = true;
            raise = !_ended;
            _ended = true;
            handles = [.. _handles];
        }
        if (raise)
        {
            Ended?.Invoke();
        }
        foreach (var handle in handles)
        {
            handle.MarkLost();
        }
    }
}

/// <summary>
/// A loopback forward on an open SSH session. Dispose after the pool that uses it.
/// <para>
/// A direct connection survives a network blip because the driver dials the real host again; a pool built on a
/// tunnel cannot — its loopback port leads nowhere once the SSH session is gone (laptop sleep, bastion
/// restart, a keepalive that went unanswered). <see cref="IsAlive"/> and <see cref="WhenLost"/> let the pool's
/// owner drop it so the next acquire opens a fresh tunnel.
/// </para>
/// </summary>
public sealed class SshTunnelHandle : IAsyncDisposable
{
    private readonly SharedSshClient _session;
    private readonly ForwardedPortLocal _forward;
    private readonly object _gate = new();
    private readonly List<Action> _onLost = [];
    private int _disposed;
    private bool _lost;

    internal SshTunnelHandle(SharedSshClient session, ForwardedPortLocal forward)
    {
        _session = session;
        _forward = forward;
        _forward.Exception += (_, _) =>
        {
            if (!_session.IsConnected)
            {
                _session.MarkLost();
            }
        };
    }

    /// <summary>The loopback port the driver should connect to.</summary>
    public int LocalPort => (int)_forward.BoundPort;

    /// <summary>False once the SSH session has ended (or the handle was disposed): nothing more will reach the
    /// database through <see cref="LocalPort"/>.</summary>
    public bool IsAlive
    {
        get
        {
            lock (_gate)
            {
                return !_lost && Volatile.Read(ref _disposed) == 0 && _session.IsConnected && _forward.IsStarted;
            }
        }
    }

    /// <summary>Run <paramref name="onLost"/> once when the SSH session ends — straight away if it already has.
    /// Runs on an SSH.NET thread. Not run for a dispose.</summary>
    public void WhenLost(Action onLost)
    {
        lock (_gate)
        {
            if (!_lost)
            {
                _onLost.Add(onLost);
                return;
            }
        }
        onLost();
    }

    internal void MarkLost()
    {
        Action[] handlers;
        lock (_gate)
        {
            if (_lost || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            _lost = true;
            handlers = [.. _onLost];
            _onLost.Clear();
        }
        foreach (var handler in handlers)
        {
            try
            {
                handler();
            }
            catch
            {
                // An owner's cleanup must not take down SSH.NET's listener thread.
            }
        }
    }

    /// <summary><paramref name="info"/> pointed at this tunnel's loopback end.</summary>
    public DbConnectionInfo Route(DbConnectionInfo info) => info with { Host = "127.0.0.1", Port = LocalPort };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        lock (_gate)
        {
            _onLost.Clear();
        }
        return new ValueTask(Task.Run(() =>
        {
            try
            {
                _forward.Stop();
            }
            catch
            {
                // Best-effort.
            }
            _session.Remove(this, _forward);
            _session.Release();
        }));
    }
}
