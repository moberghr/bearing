using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The SQL Server factory's domain-account (NTLM) branch, as far as it can be observed without a domain:
/// what the connection is handed, and what is refused before the driver is touched.
/// <para>
/// <b>Not covered here, and not coverable by this repo's containers:</b> the handshake itself. The test SQL
/// Server is a Linux container outside any domain, so no login over NTLM can succeed against it; that half
/// was checked by hand against a real domain server.
/// </para>
/// </summary>
public class SqlServerDomainLoginTests
{
    private const string Secret = "s3cret-pw";

    private static ConnectionInfo Info(string user) => MsSqlTestServer.Info() with
    {
        CredentialKind = CredentialKind.DomainPassword,
        User = user,
    };

    [Fact]
    public void The_connection_carries_the_ntlm_answerer_and_the_string_carries_no_credential()
    {
        var factory = new SqlServerConnectionFactory(Info(@"corp\ana"), Secret);

        using var conn = factory.CreateConnection();

        Assert.IsType<NtlmSspiContextProvider>(conn.SspiContextProvider);
        Assert.DoesNotContain(Secret, conn.ConnectionString);
        Assert.DoesNotContain("ana", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Integrated Security=True", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The driver compares the answerer by reference as part of the pool's identity, so one per
    /// factory is what makes the pool reusable — and what keeps two accounts out of each other's pool.</summary>
    [Fact]
    public void One_factory_shares_one_answerer_and_two_factories_do_not()
    {
        var first = new SqlServerConnectionFactory(Info(@"corp\ana"), Secret);
        var second = new SqlServerConnectionFactory(Info(@"corp\ben"), Secret);

        using var a1 = first.CreateConnection();
        using var a2 = first.CreateConnection();
        using var b = second.CreateConnection();

        Assert.Same(a1.SspiContextProvider, a2.SspiContextProvider);
        Assert.NotSame(a1.SspiContextProvider, b.SspiContextProvider);
    }

    /// <summary>The string says Integrated Security, so opening without the answerer would log in as the
    /// OS identity instead of the account asked for. Both refusals must come before any network use.</summary>
    [Theory]
    [InlineData("ana", Secret, "DOMAIN\\user")]     // no domain in the user
    [InlineData(@"corp\ana", "", "password")]       // nothing to present
    [InlineData(@"corp\ana", null, "password")]
    public async Task A_login_that_cannot_be_presented_is_refused_before_connecting(
        string user, string? password, string expected)
    {
        var info = Info(user) with { Host = "host.invalid" };
        var factory = new SqlServerConnectionFactory(info, password);

        using var conn = factory.CreateConnection();
        Assert.Null(conn.SspiContextProvider);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.TestConnectionAsync(CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Other_credential_kinds_get_no_answerer()
    {
        var factory = new SqlServerConnectionFactory(
            MsSqlTestServer.Info() with { CredentialKind = CredentialKind.Integrated }, null);

        using var conn = factory.CreateConnection();

        Assert.Null(conn.SspiContextProvider);
    }
}
