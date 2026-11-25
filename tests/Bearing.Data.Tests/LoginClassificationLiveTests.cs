using System;
using System.Threading;
using System.Threading.Tasks;
using Bearing.Core.Data;
using Bearing.Data.MySql;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What each engine's real server and driver say when a login has <b>no</b> password, and when it has the
/// wrong one — and that the provider reads both as <see cref="DbErrorKind.Authentication"/>. That verdict is the
/// whole trigger for asking the user for a password (Sessions' AuthRetry); an engine that reads either case
/// as Unknown never asks, and a connection imported with nothing in the keychain just fails.
/// <para>
/// Live on purpose (§4.7): the no-password case was assumed to arrive as the server's 28P01 on PostgreSQL and
/// does not — Npgsql refuses it client-side with no SQLSTATE. Only the real driver shows that.
/// </para>
/// <para>
/// Not covered: SQL Server's domain-account (NTLM) login. The test container is outside any domain, and that
/// kind never sends a passwordless attempt anyway — the resolver asks at once (SqlServerDomainLoginTests).
/// </para>
/// </summary>
public class LoginClassificationLiveTests
{
    public static TheoryData<MySqlEngine> Engines => new() { MySqlEngine.MariaDb, MySqlEngine.MySql };

    private static readonly ProviderRegistry Registry = new();

    /// <summary>The verdict for a login with <paramref name="password"/>, or null when it was accepted.</summary>
    private static async Task<DbErrorKind?> VerdictAsync(ConnectionInfo info, string? password)
    {
        var provider = Registry.Get(info.ProviderId);
        await using var factory = provider.CreateConnectionFactory(info, password);
        try
        {
            return await factory.TestConnectionAsync(CancellationToken.None) ? null : DbErrorKind.Unknown;
        }
        catch (Exception ex)
        {
            return provider.ClassifyException(ex);
        }
    }

    // ---- PostgreSQL ------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Postgres_reads_a_login_with_no_password_as_authentication(string? password)
    {
        await PgTestServer.RequireAsync();

        Assert.Equal(DbErrorKind.Authentication, await VerdictAsync(PgTestServer.Info(), password));
    }

    [SkippableFact]
    public async Task Postgres_reads_a_wrong_password_as_authentication_and_accepts_the_right_one()
    {
        await PgTestServer.RequireAsync();

        Assert.Equal(DbErrorKind.Authentication, await VerdictAsync(PgTestServer.Info(), "not-the-password"));
        Assert.Null(await VerdictAsync(PgTestServer.Info(), PgTestServer.Password));
    }

    // ---- SQL Server ------------------------------------------------------------------------------

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-the-password")]
    public async Task SqlServer_reads_a_login_with_no_or_the_wrong_password_as_authentication(string? password)
    {
        await MsSqlTestServer.RequireAsync();

        Assert.Equal(DbErrorKind.Authentication, await VerdictAsync(MsSqlTestServer.Info(), password));
    }

    [SkippableFact]
    public async Task SqlServer_accepts_the_right_password()
    {
        await MsSqlTestServer.RequireAsync();

        Assert.Null(await VerdictAsync(MsSqlTestServer.Info(), MsSqlTestServer.Password));
    }

    // ---- MySQL and MariaDB -----------------------------------------------------------------------

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task MySql_reads_a_login_with_no_password_as_authentication(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);

        Assert.Equal(DbErrorKind.Authentication, await VerdictAsync(MySqlTestServer.Info(engine), null));
        Assert.Equal(DbErrorKind.Authentication, await VerdictAsync(MySqlTestServer.Info(engine), ""));
    }

    [SkippableTheory]
    [MemberData(nameof(Engines))]
    public async Task MySql_reads_a_wrong_password_as_authentication_and_accepts_the_right_one(MySqlEngine engine)
    {
        await MySqlTestServer.RequireAsync(engine);

        Assert.Equal(DbErrorKind.Authentication, await VerdictAsync(MySqlTestServer.Info(engine), "not-the-password"));
        Assert.Null(await VerdictAsync(MySqlTestServer.Info(engine), MySqlTestServer.Password));
    }
}
