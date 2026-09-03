using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Entra against Azure SQL: the token has to reach <see cref="SqlConnection.AccessToken"/> and it has to
/// stay out of the connection string (§1.1).
/// <para>
/// <b>None of this needs a server, and the assertions are stronger than they look.</b> SqlClient's
/// <c>AccessToken</c> setter validates the string it is being attached to and throws
/// <see cref="InvalidOperationException"/> if that string carries a user id, a password or integrated
/// security — so <c>CreateConnection</c> returning a connection with the token on it is simultaneously
/// proof that the factory wrote none of those three. The control below is what gives that its teeth: the
/// same token attached to a stored-password factory's connection throws, so "it didn't throw" is not
/// vacuous.
/// </para>
/// <para>
/// What is <b>not</b> covered here is a real handshake. That needs an Azure SQL endpoint and an
/// <c>az login</c>, neither of which a <c>SkippableFact</c> against <see cref="MsSqlTestServer"/> can
/// stand in for — the container authenticates with SQL logins and would reject any token, so a test
/// pointed at it would assert the failure rather than the feature.
/// </para>
/// </summary>
public class SqlServerEntraTests
{
    /// <summary>A token-shaped string. Not a real JWT: nothing here parses it, and SqlClient does not
    /// either until the server rejects it.</summary>
    private const string Token = "header.payload.signature";

    private static ConnectionInfo Entra(string user = "")
        => MsSqlTestServer.Info() with { CredentialKind = CredentialKind.EntraToken, User = user };

    [Fact]
    public void An_entra_connection_carries_the_token_on_the_connection_object()
    {
        var factory = new SqlServerConnectionFactory(Entra(), Token);

        using var conn = factory.CreateConnection();

        Assert.Equal(Token, conn.AccessToken);
    }

    [Fact]
    public void The_token_is_not_in_the_connection_string()
    {
        var factory = new SqlServerConnectionFactory(Entra(), Token);

        using var conn = factory.CreateConnection();

        // The value itself, and the keyword someone might reach for while "fixing" this.
        Assert.DoesNotContain(Token, conn.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessToken", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The user name is deliberately dropped for this kind. It is not tidiness: SqlClient refuses an access
    /// token beside a <c>User ID</c>, so a factory that kept the dialog's User box would fail at
    /// <c>CreateConnection</c> rather than at login — and an Entra login's identity is the token's, not the
    /// box's.
    /// </summary>
    [Fact]
    public void A_user_name_on_an_entra_connection_does_not_reach_the_string()
    {
        var factory = new SqlServerConnectionFactory(Entra("someone@example.com"), Token);

        using var conn = factory.CreateConnection();

        Assert.Equal(Token, conn.AccessToken);
        Assert.DoesNotContain("someone@example.com", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The control for the three above: SqlClient's own refusal is what they rest on, so it has to
    /// be shown to happen. A stored-password connection's string carries a password, and attaching a token
    /// to it throws.</summary>
    [Fact]
    public void The_control_a_password_bearing_connection_refuses_an_access_token()
    {
        var factory = new SqlServerConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);

        using var conn = factory.CreateConnection();

        Assert.Null(conn.AccessToken);
        Assert.Throws<InvalidOperationException>(() => conn.AccessToken = Token);
    }

    /// <summary>Integrated authentication is the other kind SqlClient will not mix a token with, and it is
    /// the one an Entra connection could plausibly acquire by accident — both are "no password in the
    /// string", so a factory that set <c>Integrated Security</c> for anything that is not
    /// <see cref="CredentialKind.StoredPassword"/> would look correct until a token arrived.</summary>
    [Fact]
    public void The_control_an_integrated_connection_refuses_an_access_token()
    {
        var factory = new SqlServerConnectionFactory(
            MsSqlTestServer.Info() with { CredentialKind = CredentialKind.Integrated }, null);

        using var conn = factory.CreateConnection();

        Assert.Throws<InvalidOperationException>(() => conn.AccessToken = Token);
    }

    /// <summary>An Entra connection whose credential has not been resolved yet must still build: the
    /// resolver can hand over a null secret (the prompt was cancelled, az is not signed in and the caller
    /// is retrying), and the failure the user needs is a login failure, not a throw out of a factory
    /// constructor.</summary>
    [Fact]
    public void An_unresolved_token_leaves_the_connection_credential_less_rather_than_throwing()
    {
        var factory = new SqlServerConnectionFactory(Entra("someone@example.com"), null);

        using var conn = factory.CreateConnection();

        Assert.Null(conn.AccessToken);
        // Still no user or password smuggled in — the string is credential-less, which is why a token can
        // be attached to it after the fact.
        conn.AccessToken = Token;
        Assert.Equal(Token, conn.AccessToken);
    }

    /// <summary>The dropdown gates on the provider flag, so flipping the flag without wiring the path (or
    /// wiring it and forgetting the flag) is the one mistake that shows up as a credential kind that fails
    /// at login. Both engines now support it, by two different mechanisms.</summary>
    [Fact]
    public void Both_engines_can_authenticate_with_an_entra_token()
    {
        Assert.True(new PostgresProvider().SupportsEntraToken);
        Assert.True(new SqlServerProvider().SupportsEntraToken);
    }
}
