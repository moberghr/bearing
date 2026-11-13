using System;
using System.Collections.Concurrent;
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
/// in-process). One tunnel per connection factory: it lives as long as the pool built on it and is disposed
/// after it (<see cref="ConnectionSession"/>, the schema browser's readers).
/// <para>
/// <b>Host key: accept-new.</b> An unpinned key is accepted and handed to <see cref="HostKeyPinned"/> so the
/// host can write it into the connection; a key that differs from the pin is refused. With no
/// <see cref="HostKeyPinned"/> (the CLI, which cannot save the project) an unpinned key is refused too —
/// never trusted silently (§1.4).
/// </para>
/// <para>
/// <b>Secret:</b> the stored one (<see cref="SshSecretId"/>), else one typed earlier this session, else —
/// for password auth, or a key that turns out to need a passphrase, or a refused login — asked for once
/// through <see cref="ICredentialPrompt"/> and saved to the keychain when it worked. Never logged, never in
/// an error message.
/// </para>
/// </summary>
public sealed class SshTunnels
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    private readonly Func<ISecretStore?> _secrets;
    private readonly ICredentialPrompt? _prompt;
    private readonly ConcurrentDictionary<Guid, string> _typed = new();

    public SshTunnels(Func<ISecretStore?> secrets, ICredentialPrompt? prompt)
    {
        _secrets = secrets;
        _prompt = prompt;
    }

    /// <summary>Called with the connection and the <c>SHA256:…</c> fingerprint when a host key is seen for
    /// the first time and accepted. Null = refuse unpinned keys.</summary>
    public Func<DbConnectionInfo, string, Task>? HostKeyPinned { get; set; }

    /// <summary>Open the tunnel for <paramref name="info"/>. Throws <see cref="ConnectionFailedException"/>
    /// naming the SSH leg on any failure.</summary>
    /// <para><paramref name="typedSecret"/> is a secret typed into the dialog and not yet saved (its Test
    /// button): used as-is, never prompted over and never stored.</para>
    public async Task<SshTunnelHandle> OpenAsync(DbConnectionInfo info, CancellationToken ct, string? typedSecret = null)
    {
        var ssh = info.SshTunnel ?? throw new ArgumentException("Connection has no SSH tunnel.", nameof(info));
        var where = $"{ssh.User}@{ssh.Host}:{ssh.Port}";
        var secretId = SshSecretId.For(info.Id);

        var offered = !string.IsNullOrEmpty(typedSecret);
        var secret = offered ? typedSecret
            : _typed.TryGetValue(secretId, out var t) ? t
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
                var handle = await ConnectAsync(info, ssh, secret, ct);
                if (prompted && !offered && secret is not null)
                {
                    await RememberAsync(secretId, secret);
                }
                return handle;
            }
            catch (Exception ex) when (!prompted && IsCredentialProblem(ex))
            {
                var what = ssh.UsesPrivateKey ? $"Passphrase for SSH key {ssh.PrivateKeyPath}" : $"SSH password for {where}";
                secret = await AskAsync(info, $"{what} (the last one was refused)", ct);
                prompted = true;
            }
            catch (ConnectionFailedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
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

    private async Task<SshTunnelHandle> ConnectAsync(DbConnectionInfo info, SshTunnel ssh, string? secret, CancellationToken ct)
    {
        AuthenticationMethod auth;
        if (ssh.UsesPrivateKey)
        {
            var path = KeyFile(ssh);
            auth = new PrivateKeyAuthenticationMethod(ssh.User,
                string.IsNullOrEmpty(secret) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, secret));
        }
        else
        {
            auth = new PasswordAuthenticationMethod(ssh.User, secret ?? "");
        }

        var connection = new Renci.SshNet.ConnectionInfo(ssh.Host.Trim(), ssh.Port, ssh.User, auth) { Timeout = ConnectTimeout };
        var client = new SshClient(connection) { KeepAliveInterval = KeepAlive };

        string? seen = null;
        string? refusal = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = "SHA256:" + e.FingerPrintSHA256;
            var pinned = ssh.HostKeyFingerprint;
            if (string.IsNullOrWhiteSpace(pinned))
            {
                e.CanTrust = HostKeyPinned is not null;
                if (!e.CanTrust)
                    refusal = $"The SSH host key of {ssh.Host} ({seen}) is not trusted yet. Connect once from the Bearing app to pin it.";
            }
            else
            {
                e.CanTrust = string.Equals(pinned.Trim(), seen, StringComparison.Ordinal);
                if (!e.CanTrust)
                    refusal = $"The SSH host key of {ssh.Host} has CHANGED: pinned {pinned.Trim()}, server sent {seen}. "
                              + "Refused. If the change is expected, use \"Forget host key\" on the connection's SSH tab.";
            }
        };

        try
        {
            await client.ConnectAsync(ct);
        }
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

        try
        {
            var forward = new ForwardedPortLocal("127.0.0.1", 0, info.Host.Trim(), (uint)info.Port);
            client.AddForwardedPort(forward);
            forward.Start();
            if (string.IsNullOrWhiteSpace(ssh.HostKeyFingerprint) && seen is not null && HostKeyPinned is { } pin)
            {
                try { await pin(info, seen); } catch { /* pinning is best-effort; the next connect re-pins */ }
            }
            return new SshTunnelHandle(client, forward);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<string?> StoredAsync(Guid secretId, CancellationToken ct)
    {
        var store = _secrets();
        if (store is null)
        {
            return null;
        }
        try { return await store.GetPasswordAsync(secretId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<string> AskAsync(DbConnectionInfo info, string message, CancellationToken ct)
    {
        if (_prompt is null)
            throw new ConnectionFailedException($"{message}: none is stored in the keychain and there is no prompt here.");
        return await _prompt.RequestPasswordAsync(info, message, ct)
               ?? throw new ConnectionFailedException($"SSH secret entry cancelled for '{info.Name}'.");
    }

    private async Task RememberAsync(Guid secretId, string secret)
    {
        _typed[secretId] = secret;
        var store = _secrets();
        if (store is null || !store.CanStore)
        {
            return;
        }
        try { await store.SetPasswordAsync(secretId, secret, CancellationToken.None); }
        catch { /* kept in memory for the session; the keychain is best-effort here */ }
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
        try { await store.DeleteAsync(secretId, CancellationToken.None); } catch { /* best-effort */ }
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

    internal static string ExpandHome(string path)
        => path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}

/// <summary>An open SSH session with its loopback forward. Dispose after the pool that uses it.</summary>
public sealed class SshTunnelHandle : IAsyncDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;
    private int _disposed;

    internal SshTunnelHandle(SshClient client, ForwardedPortLocal forward)
    {
        _client = client;
        _forward = forward;
    }

    /// <summary>The loopback port the driver should connect to.</summary>
    public int LocalPort => (int)_forward.BoundPort;

    /// <summary><paramref name="info"/> pointed at this tunnel's loopback end.</summary>
    public DbConnectionInfo Route(DbConnectionInfo info) => info with { Host = "127.0.0.1", Port = LocalPort };

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        return new ValueTask(Task.Run(() =>
        {
            try { _forward.Stop(); } catch { /* best-effort */ }
            try { _client.Disconnect(); } catch { /* best-effort */ }
            _client.Dispose();
        }));
    }
}
