using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

public class CredentialResolverTests
{
    private static ConnectionInfo Conn(CredentialKind kind, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "c",
        ProviderId = "postgres",
        Host = "h",
        User = "u",
        CredentialKind = kind,
    };

    [Fact]
    public async Task StoredPassword_reads_the_secret_store_and_has_no_expiry()
    {
        var secrets = new FakeSecretStore();
        var info = Conn(CredentialKind.StoredPassword);
        await secrets.SetPasswordAsync(info.Id, "pw", CancellationToken.None);
        var resolver = new CredentialResolver(() => secrets, null, new ThrowingEntraTokens());

        var cred = await resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None);

        Assert.Equal("pw", cred.Secret);
        Assert.Null(cred.ExpiresAt);
    }

    [Fact]
    public async Task StoredPassword_read_failure_is_reported_not_treated_as_no_password()
    {
        // The bug this pins: a keyring that errors on read used to be indistinguishable from "nothing stored",
        // because the store mapped every non-zero exit to null. The connect then went out passwordless and the
        // user got prompted for a password that was in their keyring all along. A real failure must surface.
        var secrets = new FakeSecretStore
        {
            ReadThrows = new InvalidOperationException(
                "secret-tool lookup failed: The secret was transferred or encrypted in an invalid way."),
        };
        var info = Conn(CredentialKind.StoredPassword);
        var prompt = new FakeCredentialPrompt("should-not-be-asked");
        var resolver = new CredentialResolver(() => secrets, prompt, new ThrowingEntraTokens());

        var ex = await Assert.ThrowsAsync<ConnectionFailedException>(
            () => resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None));

        Assert.Contains("Could not read the stored password", ex.Message);
        Assert.Contains("transferred or encrypted", ex.Message);   // the keyring's own words reach the user
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task StoredPassword_with_nothing_stored_still_connects_passwordless()
    {
        // The other half of the distinction above, and the reason "error" can't simply be folded into "none":
        // no stored secret is the trust-auth / .pgpass path, which must go out with no password and no prompt.
        var info = Conn(CredentialKind.StoredPassword);
        var prompt = new FakeCredentialPrompt("should-not-be-asked");
        var resolver = new CredentialResolver(() => new FakeSecretStore(), prompt, new ThrowingEntraTokens());

        var cred = await resolver.ResolveAsync(info, forceRefresh: false, CancellationToken.None);

        Assert.Null(cred.Secret);
        Assert.Equal(0, prompt.Calls);
    }

    [Fact]
    public async Task Prompt_caches_the_password_and_re_prompts_only_after_invalidate()
    {
        var prompt = new FakeCredentialPrompt("first", "second");
        var info = Conn(CredentialKind.Prompt);
        var resolver = new CredentialResolver(() => null, prompt, new ThrowingEntraTokens());

        var a = await resolver.ResolveAsync(info, false, CancellationToken.None);
        var b = await resolver.ResolveAsync(info, false, CancellationToken.None);
        Assert.Equal("first", a.Secret);
        Assert.Equal("first", b.Secret);           // served from cache
        Assert.Equal(1, prompt.Calls);

        resolver.Invalidate(info.Id);
        var c = await resolver.ResolveAsync(info, false, CancellationToken.None);
        Assert.Equal("second", c.Secret);          // re-prompted after invalidation
        Assert.Equal(2, prompt.Calls);
    }

    [Fact]
    public async Task Prompt_cancelled_throws_ConnectionFailed_and_caches_nothing()
    {
        var prompt = new FakeCredentialPrompt(null, "later"); // first cancel, then a value
        var info = Conn(CredentialKind.Prompt);
        var resolver = new CredentialResolver(() => null, prompt, new ThrowingEntraTokens());

        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => resolver.ResolveAsync(info, false, CancellationToken.None));

        // Nothing was cached, so a subsequent resolve prompts again (and now succeeds).
        var ok = await resolver.ResolveAsync(info, false, CancellationToken.None);
        Assert.Equal("later", ok.Secret);
    }

    [Fact]
    public async Task Prompt_without_a_prompt_impl_throws()
    {
        var resolver = new CredentialResolver(() => null, null, new ThrowingEntraTokens());
        await Assert.ThrowsAsync<ConnectionFailedException>(
            () => resolver.ResolveAsync(Conn(CredentialKind.Prompt), false, CancellationToken.None));
    }

    [Fact]
    public async Task Entra_token_is_cached_until_it_nears_expiry_then_re_minted()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        // Each mint expires 10 minutes after the (current) clock.
        var tokens = new FakeEntraTokens(call => new Credential($"tok{call}", now.AddMinutes(10)));
        var info = Conn(CredentialKind.EntraToken);
        var resolver = new CredentialResolver(() => null, null, tokens, () => now);

        var a = await resolver.ResolveAsync(info, false, CancellationToken.None);
        var b = await resolver.ResolveAsync(info, false, CancellationToken.None);
        Assert.Equal("tok0", a.Secret);
        Assert.Equal("tok0", b.Secret);           // cached — one mint
        Assert.Equal(1, tokens.Calls);

        // Advance to within the refresh skew of expiry (10 min - 2 min skew = refresh at +8 min).
        now = now.AddMinutes(9);
        var c = await resolver.ResolveAsync(info, false, CancellationToken.None);
        Assert.Equal("tok1", c.Secret);           // re-minted
        Assert.Equal(2, tokens.Calls);
    }

    [Fact]
    public async Task Entra_forceRefresh_bypasses_a_still_valid_cached_token()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var tokens = new FakeEntraTokens(call => new Credential($"tok{call}", now.AddHours(1)));
        var info = Conn(CredentialKind.EntraToken);
        var resolver = new CredentialResolver(() => null, null, tokens, () => now);

        await resolver.ResolveAsync(info, false, CancellationToken.None);
        var forced = await resolver.ResolveAsync(info, forceRefresh: true, CancellationToken.None);

        Assert.Equal("tok1", forced.Secret);
        Assert.Equal(2, tokens.Calls);
    }

    [Theory]
    [InlineData(0, false)]   // no expiry → never expiring
    [InlineData(300, false)] // 5 min out, 90 s skew → not yet
    [InlineData(60, true)]   // 60 s out, within the 90 s skew
    [InlineData(-30, true)]  // already past expiry
    public void IsExpiring_respects_the_skew(int secondsUntilExpiry, bool expected)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? expiresAt = secondsUntilExpiry == 0 ? null : now.AddSeconds(secondsUntilExpiry);
        Assert.Equal(expected, CredentialResolver.IsExpiring(expiresAt, now, TimeSpan.FromSeconds(90)));
    }
}
