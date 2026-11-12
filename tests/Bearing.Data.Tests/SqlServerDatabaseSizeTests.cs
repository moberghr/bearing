using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Database sizes for a least-privilege login (#171). <c>sys.master_files</c> shows a database's rows only
/// to a login holding CREATE DATABASE / ALTER ANY DATABASE / VIEW ANY DEFINITION, so such a login read every
/// database as "size not visible" — including the one it was browsing. The fixture is a login with none of
/// those, a user in the test database, and a second database it has no user in.
/// </summary>
public class SqlServerDatabaseSizeTests
{
    private const string Login = "bearing_size_probe";
    private const string LoginPassword = "Size!Probe1x";
    private const string Fenced = "bearing_size_fenced";

    private static async Task Run(IQueryExecutor exec, string sql)
    {
        var r = Assert.Single(await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None));
        Assert.True(r.Success, r.Error?.Message);
    }

    // §4.7: cluster-wide teardown, one statement per call, each allowed to fail on its own.
    private static async Task DropFixtureAsync(IQueryExecutor exec)
    {
        foreach (var sql in new[]
        {
            $"if user_id('{Login}') is not null drop user [{Login}]",
            $"if db_id('{Fenced}') is not null drop database [{Fenced}]",
            $"if suser_id('{Login}') is not null drop login [{Login}]",
        })
            await exec.ExecuteAsync(sql, new QueryOptions(), CancellationToken.None);
    }

    [SkippableFact]
    public async Task A_login_without_view_any_definition_still_gets_the_size_of_a_database_it_can_enter()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var admin = provider.CreateConnectionFactory(MsSqlTestServer.Info(), MsSqlTestServer.Password);
        await MsSqlTestServer.RequireAsync(admin);
        var exec = provider.CreateQueryExecutor(admin);

        await DropFixtureAsync(exec);
        try
        {
            await Run(exec, $"create login [{Login}] with password = '{LoginPassword}', check_policy = off");
            await Run(exec, $"create user [{Login}] for login [{Login}]");
            await Run(exec, $"create database [{Fenced}]");

            var probeInfo = MsSqlTestServer.Info() with { User = Login };
            await using var probe = provider.CreateConnectionFactory(probeInfo, LoginPassword);

            // The bug on its own: master_files alone answers nothing for the database the login is in.
            await using (var conn = await ((SqlServerConnectionFactory)probe).OpenConnectionAsync(CancellationToken.None))
            {
                var rows = await SqlServerMetadataReader.ReadMasterFileSizesAsync(conn, CancellationToken.None);
                var own = Assert.Single(rows, r => r.Name == MsSqlTestServer.Database);
                Assert.Null(own.Pages);
                Assert.True(own.CanAccess);
            }

            var asProbe = await provider.CreateMetadataReader(probe).GetDatabaseSizesAsync(CancellationToken.None);
            var asAdmin = await provider.CreateMetadataReader(admin).GetDatabaseSizesAsync(CancellationToken.None);

            // The fallback gives the size, and it is the size an admin's master_files read reports.
            var probeOwn = Assert.Single(asProbe, d => d.Database == MsSqlTestServer.Database);
            var adminOwn = Assert.Single(asAdmin, d => d.Database == MsSqlTestServer.Database);
            Assert.NotNull(adminOwn.Bytes);
            Assert.Equal(adminOwn.Bytes, probeOwn.Bytes);
            Assert.Null(probeOwn.NotVisibleReason);

            // A database the login has no user in stays unknown — with the SQL Server reason, not Postgres's.
            var fenced = Assert.Single(asProbe, d => d.Database == Fenced);
            Assert.Null(fenced.Bytes);
            Assert.Equal(SqlServerMetadataReader.NoAccessReason, fenced.NotVisibleReason);
            Assert.DoesNotContain("CONNECT", fenced.NotVisibleReason);
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await DropFixtureAsync(exec);
        }
    }
}
