using Bearing.Core.Workspace;
using Bearing.Persistence;
using Xunit;

namespace Bearing.Persistence.Tests;

/// <summary>
/// What the startup probe concludes, and what it reports when it rejects a store. This exists because the app
/// spent a debugging session insisting "No system keyring found" on a machine whose keyring worked perfectly:
/// the probe caught every failure into a bare <c>false</c>, so four unrelated causes — no helper on PATH, no
/// session bus, a locked collection, a store that takes a write and returns something else — were
/// indistinguishable both to the user and to us.
/// <para>
/// The real stores can only be exercised on their own platform (<see cref="PlatformKeychainTests"/>), so the
/// reasons and the cleanup postcondition are driven here with fakes instead.
/// </para>
/// </summary>
public class SecretStoreProbeTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task A_working_store_is_accepted_with_no_reason_to_report()
        => Assert.Null(await SecretStoreFactory.ProbeFailureAsync(new FakeStore(), Ct));

    [Fact]
    public async Task A_store_that_throws_reports_its_own_message()
    {
        var store = new FakeStore { SetThrows = new InvalidOperationException("secret-tool store failed: no such collection") };

        var reason = await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.NotNull(reason);
        Assert.Contains("no such collection", reason);   // the helper's own words are the diagnostic
    }

    [Fact]
    public async Task A_probe_reason_is_redacted_before_it_reaches_the_log()
    {
        // The probe value is the literal "probe", so this shouldn't be reachable — but the reason is written
        // to crash.log, so it goes through the same redaction as every other surfaced driver message (§1.1).
        var store = new FakeStore { SetThrows = new InvalidOperationException("failed for password=hunter2") };

        var reason = await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.DoesNotContain("hunter2", reason);
        Assert.Contains("password=***", reason);
    }

    [Fact]
    public async Task A_store_that_silently_drops_the_write_is_rejected_and_says_so()
    {
        var reason = await SecretStoreFactory.ProbeFailureAsync(new FakeStore { Swallow = true }, Ct);

        Assert.NotNull(reason);
        Assert.Contains("missing", reason);
    }

    [Fact]
    public async Task A_store_that_returns_a_different_value_is_rejected_and_says_so()
    {
        var reason = await SecretStoreFactory.ProbeFailureAsync(new FakeStore { Corrupt = "something else" }, Ct);

        Assert.NotNull(reason);
        Assert.Contains("different value", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_probe_secret_is_always_cleaned_up_even_when_the_probe_fails(bool failing)
    {
        // A leftover probe credential is visible in the Windows and macOS credential UIs, where it reads as a
        // bug. The cleanup is in a `finally` precisely so a rejected store is still tidied.
        var store = new FakeStore { Corrupt = failing ? "wrong" : null };

        await SecretStoreFactory.ProbeFailureAsync(store, Ct);

        Assert.True(store.Deleted, "the probe secret was left behind");
        Assert.Empty(store.Secrets);
    }

    [Fact]
    public async Task A_store_that_throws_on_delete_does_not_break_the_probe()
    {
        // Cleanup is best-effort: failing to tidy up must not turn a working keychain into "no keychain".
        var store = new FakeStore { DeleteThrows = new InvalidOperationException("clear failed") };

        Assert.Null(await SecretStoreFactory.ProbeFailureAsync(store, Ct));
    }

    private sealed class FakeStore : ISecretStore
    {
        public Dictionary<Guid, string> Secrets { get; } = new();
        public bool Deleted { get; private set; }

        /// <summary>Thrown from <see cref="SetPasswordAsync"/> — an absent or refusing credential store.</summary>
        public Exception? SetThrows { get; init; }
        public Exception? DeleteThrows { get; init; }
        /// <summary>Accept the write and keep nothing, so the read-back finds nothing.</summary>
        public bool Swallow { get; init; }
        /// <summary>Hand back this instead of what was stored.</summary>
        public string? Corrupt { get; init; }

        public bool IsSecure => true;
        public bool CanStore => true;

        public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct)
        {
            if (SetThrows is not null) throw SetThrows;
            if (!Swallow) Secrets[connectionId] = password;
            return Task.CompletedTask;
        }

        public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
            => Task.FromResult(Corrupt ?? (Secrets.TryGetValue(connectionId, out var v) ? v : null));

        public Task DeleteAsync(Guid connectionId, CancellationToken ct)
        {
            Deleted = true;
            Secrets.Remove(connectionId);
            if (DeleteThrows is not null) throw DeleteThrows;
            return Task.CompletedTask;
        }
    }
}
