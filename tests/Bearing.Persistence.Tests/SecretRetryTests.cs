using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// The probe retries, and why. A healthy libsecret refuses roughly one secret transfer in 80 (measured
/// 2026-08-13: 3 failures in 250 store→read→delete cycles, all
/// <c>"The secret was transferred or encrypted in an invalid way."</c>). The probe used to treat one refusal as
/// "this machine has no credential store", which demoted the session to the password-refusing file fallback —
/// the actual cause of the "No system keyring found" reports on a machine whose keyring worked.
/// </summary>
public class SecretRetryTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task A_store_that_fails_once_then_works_is_accepted()
    {
        // The whole point: the 1%-per-call fault must not decide the session's posture.
        var store = new FlakyStore(failures: 1);

        Assert.Null(await SecretStoreFactory.ProbeFailureAsync(store, Ct));
        Assert.Equal(2, store.Attempts);
    }

    [Fact]
    public async Task A_store_that_fails_twice_then_works_is_still_accepted()
    {
        var store = new FlakyStore(failures: 2);

        Assert.Null(await SecretStoreFactory.ProbeFailureAsync(store, Ct));
        Assert.Equal(3, store.Attempts);
    }

    [Fact]
    public async Task A_consistently_broken_store_is_rejected_after_a_bounded_number_of_tries()
    {
        // Retrying must not become an unbounded stall at startup on a machine that really has no keychain.
        var store = new FlakyStore(failures: int.MaxValue);

        var reason = await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.NotNull(reason);
        Assert.Equal(3, store.Attempts);
    }

    [Fact]
    public async Task Every_attempt_cleans_up_after_itself()
    {
        // Three attempts must not leave three probe credentials behind — they are visible in the Windows and
        // macOS credential UIs, where they read as a bug.
        var store = new FlakyStore(failures: 2);

        await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.Empty(store.Secrets);
        Assert.Equal(store.Attempts, store.Deletes);
    }

    [Fact]
    public async Task Each_attempt_uses_a_fresh_probe_id()
    {
        // Reusing one id would let a stale entry from a previous attempt satisfy the read-back.
        var store = new FlakyStore(failures: 2);

        await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.Equal(store.Attempts, store.SeenIds.Count);
    }

    [Fact]
    public async Task A_dismissed_unlock_prompt_is_final_and_is_not_asked_again()
    {
        // The user already said no. Retrying re-raises the keyring's unlock prompt twice more.
        var store = new FlakyStore(failures: int.MaxValue, message: "secret-tool: Prompt was dismissed");

        var reason = await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.Equal(1, store.Attempts);
        Assert.Contains("dismissed", reason);
    }

    [Fact]
    public async Task Cancellation_stops_the_retry_loop_rather_than_burning_the_attempts()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = new FlakyStore(failures: int.MaxValue);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SecretStoreFactory.ProbeFailureAsync(store, cts.Token));
        Assert.Equal(1, store.Attempts);
    }

    [Theory]
    [InlineData("Couldn't create item: The secret was transferred or encrypted in an invalid way.", false)]
    [InlineData("secret-tool: Prompt was dismissed", true)]
    [InlineData("The operation was cancelled", true)]
    [InlineData(null, false)]
    public void LooksFinal_only_claims_the_answers_the_user_already_gave(string? detail, bool expected)
        => Assert.Equal(expected, SecretRetry.LooksFinal(detail));

    /// <summary>Fails its first <c>failures</c> store attempts, then behaves. Counts what the probe did to it.</summary>
    private sealed class FlakyStore(int failures, string? message = null) : ISecretStore
    {
        private int _remaining = failures;

        public Dictionary<Guid, string> Secrets { get; } = new();
        public List<Guid> SeenIds { get; } = new();
        public int Attempts { get; private set; }
        public int Deletes { get; private set; }

        public bool IsSecure => true;
        public bool CanStore => true;

        public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
        {
            Attempts++;
            ct.ThrowIfCancellationRequested();
            if (!SeenIds.Contains(connectionId)) SeenIds.Add(connectionId);
            if (_remaining-- > 0)
                throw new InvalidOperationException(
                    message ?? "secret-tool store failed: Couldn't create item: The secret was transferred or encrypted in an invalid way.");
            Secrets[connectionId] = password;
            return Task.CompletedTask;
        }

        public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
            => Task.FromResult(Secrets.TryGetValue(connectionId, out var v) ? v : null);

        public Task DeleteAsync(Guid connectionId, CancellationToken ct)
        {
            Deletes++;
            Secrets.Remove(connectionId);
            return Task.CompletedTask;
        }
    }
}
