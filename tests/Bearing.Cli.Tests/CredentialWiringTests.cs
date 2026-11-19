using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Data;
using Xunit;

namespace Bearing.Cli.Tests;

/// <summary>
/// How the <c>bearing</c> command resolves a secret (<see cref="Program.Credentials"/>): the App's arrangement,
/// with the registry saying which engines take no password. A hand-edited DynamoDB entry still naming
/// <see cref="CredentialKind.StoredPassword"/> must never reach the keychain (§1.1); every other engine reads it
/// as before.
/// </summary>
public class CredentialWiringTests
{
    private static ConnectionInfo Saved(string providerId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "hand-edited",
        ProviderId = providerId,
        Host = "eu-west-1",
        CredentialKind = CredentialKind.StoredPassword,
    };

    [Fact]
    public async Task A_dynamodb_entry_naming_a_stored_password_never_reads_the_keychain()
    {
        var store = new CountingSecretStore();
        var credentials = Program.Credentials(new ProviderRegistry(), () => store);
        var info = Saved("dynamodb");

        var resolved = await credentials.ResolveAsync(info, forceRefresh: false, CancellationToken.None);
        var forced = await credentials.ResolveAsync(info, forceRefresh: true, CancellationToken.None);

        Assert.Null(resolved.Secret);
        Assert.Null(forced.Secret);
        Assert.Equal(0, store.Reads);
        Assert.False(credentials.CanRefresh(info));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("an-engine-nobody-registered")]
    public async Task An_engine_that_takes_a_password_still_reads_it_from_the_keychain(string providerId)
    {
        var store = new CountingSecretStore { Password = "from-the-keychain" };
        var credentials = Program.Credentials(new ProviderRegistry(), () => store);

        var resolved = await credentials.ResolveAsync(Saved(providerId), forceRefresh: false, CancellationToken.None);

        Assert.Equal("from-the-keychain", resolved.Secret);
        Assert.Equal(1, store.Reads);
    }

    /// <summary>Hand-rolled (§4.1): answers one password and counts how often it was asked.</summary>
    private sealed class CountingSecretStore : ISecretStore
    {
        public string? Password { get; init; }
        public int Reads { get; private set; }

        public bool IsSecure => true;
        public bool CanStore => true;

        public Task<string?> GetPasswordAsync(Guid connectionId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Password);
        }

        public Task SetPasswordAsync(Guid connectionId, string password, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(Guid connectionId, CancellationToken ct) => Task.CompletedTask;
    }
}
