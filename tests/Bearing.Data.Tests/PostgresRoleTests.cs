using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// #120's role and grant reads against a real server.
/// <para>
/// pagila ships one role (<c>postgres</c>) and no grants worth reading, so the interesting cases are created
/// here in their own schema under <see cref="PgTestServer.RequireWritableAsync"/> and dropped again. Live
/// rather than fixtures for §4.6's reason, and with an extra one of its own: the privilege rendering comes
/// out of <c>aclexplode</c>, so a fixture would be asserting our belief about the ACL format rather than the
/// format.
/// </para>
/// </summary>
public class PostgresRoleTests
{
    private static ConnectionInfo Info() => PgTestServer.Info();
    private static string Password => PgTestServer.Password;

    private const string Schema = "bearing_roles_test";
    private const string Login = "bearing_test_app";
    private const string Group = "bearing_test_readers";

    [SkippableFact]
    public async Task The_servers_roles_come_back_without_any_password_material()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var roles = await provider.CreateMetadataReader(factory).GetRolesAsync(CancellationToken.None);

        // Every server has a superuser, and it is the one role guaranteed to be there to assert on.
        var superuser = roles.SingleOrDefault(r => r.Name == PgTestServer.User);
        Assert.NotNull(superuser);
        Assert.True(superuser.CanLogin);

        // pg_roles masks rolpassword and RoleInfo has nowhere to put it — asserted as the absence of any
        // field carrying it, which is the only form this claim can take (§1.1).
        Assert.DoesNotContain(
            typeof(Bearing.Core.Schema.RoleInfo).GetProperties().Select(p => p.Name.ToLowerInvariant()),
            n => n.Contains("password") || n.Contains("hash") || n.Contains("secret"));

        // The built-in predefined roles (pg_read_all_data and friends) are group roles, so a server always
        // has both kinds in the list.
        Assert.Contains(roles, r => !r.CanLogin);
    }

    [SkippableFact]
    public async Task An_unlimited_connection_limit_comes_back_as_minus_one_rather_than_as_a_number()
    {
        // Postgres' own encoding, kept rather than normalised here: the row is what decides how to say it,
        // and a reader that turned -1 into 0 would make "unlimited" indistinguishable from "none".
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var roles = await provider.CreateMetadataReader(factory).GetRolesAsync(CancellationToken.None);
        var superuser = roles.Single(r => r.Name == PgTestServer.User);

        Assert.Equal(-1, superuser.ConnectionLimit);
        Assert.Null(superuser.ValidUntil);   // no expiry, which is a fact and not a refusal
    }

    /// <summary>
    /// A login role, a group it belongs to, and real grants — read back as attributes, membership and
    /// spelled-out privileges.
    /// </summary>
    [SkippableFact]
    public async Task A_created_role_comes_back_with_its_attributes_membership_and_grants()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);

        await Setup(exec);
        try
        {
            var roles = await reader.GetRolesAsync(CancellationToken.None);

            var login = roles.Single(r => r.Name == Login);
            Assert.True(login.CanLogin);
            Assert.False(login.IsSuperuser);
            Assert.False(login.CanCreateDb);
            Assert.Equal(7, login.ConnectionLimit);
            Assert.NotNull(login.ValidUntil);
            Assert.Equal(2030, login.ValidUntil!.Value.Year);
            // pg_auth_members, which is what explains most of a role's privileges.
            Assert.Equal([Group], login.MemberOf);

            var group = roles.Single(r => r.Name == Group);
            Assert.False(group.CanLogin);
            Assert.Empty(group.MemberOf);

            // ---- grants
            var grants = await reader.GetRoleGrantsAsync(Login, CancellationToken.None);
            Assert.True(grants.Visible);

            // The database-level row exists even for a role with no object grants, so a role with nothing
            // else still says something true.
            var database = grants.Grants.SingleOrDefault(g => g.Object.StartsWith("database "));
            Assert.NotNull(database);
            Assert.Contains("CONNECT", database.Privileges);

            // Spelled out by aclexplode, not the acl shorthand — the whole point of the rendering.
            var ticket = grants.Grants.Single(g => g.Object == $"{Schema}.ticket");
            Assert.Equal(["INSERT", "SELECT"], ticket.Privileges);

            var audit = grants.Grants.Single(g => g.Object == $"{Schema}.audit");
            Assert.Equal(["SELECT"], audit.Privileges);
        }
        finally
        {
            await Teardown(exec);
        }
    }

    [SkippableFact]
    public async Task A_role_with_no_grants_here_reports_visible_and_empty_rather_than_not_visible()
    {
        // The distinction the whole record exists for: "nothing granted" is an answer, and it must not look
        // like "you may not find out".
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireWritableAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);

        await Setup(exec);
        try
        {
            var grants = await reader.GetRoleGrantsAsync(Group, CancellationToken.None);

            Assert.True(grants.Visible);
            // The group was granted nothing at all, not even CONNECT — so there is no database row either.
            Assert.DoesNotContain(grants.Grants, g => g.Object == $"{Schema}.ticket");
        }
        finally
        {
            await Teardown(exec);
        }
    }

    [SkippableFact]
    public async Task A_role_that_does_not_exist_is_reported_as_not_visible_rather_than_as_empty()
    {
        // has_database_privilege raises for an unknown role, which the reader turns into "could not see" —
        // the honest answer, and never a privilege list.
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await PgTestServer.RequireAsync(factory);

        var grants = await provider.CreateMetadataReader(factory)
            .GetRoleGrantsAsync("bearing_no_such_role_120", CancellationToken.None);

        Assert.False(grants.Visible);
        Assert.Empty(grants.Grants);
    }

    // ---- fixture ------------------------------------------------------------------------------------

    private static async Task Setup(IQueryExecutor exec)
    {
        await Teardown(exec);
        var results = await exec.ExecuteAsync(
            $"""
             create schema {Schema};
             create table {Schema}.ticket (id serial primary key, note text);
             create table {Schema}.audit (id serial primary key, what text);
             create role {Group} nologin;
             create role {Login} login password 'not-a-real-secret'
                 connection limit 7 valid until '2030-01-01';
             grant {Group} to {Login};
             grant connect on database {PgTestServer.Database} to {Login};
             grant usage on schema {Schema} to {Login};
             grant select, insert on {Schema}.ticket to {Login};
             grant select on {Schema}.audit to {Login};
             """,
            new QueryOptions(), CancellationToken.None);

        var failed = results.FirstOrDefault(r => !r.Success);
        Assert.True(failed is null, failed?.Error?.Message);
    }

    /// <summary>
    /// Drop everything, in dependency order: roles cannot be dropped while they still own or are granted
    /// anything, so the schema goes first and the grants are revoked before the roles.
    /// <para>
    /// One statement per call, deliberately. Npgsql runs a multi-statement batch in one implicit
    /// transaction, so a <c>revoke</c> that fails because the role is already gone would abort the drops
    /// behind it — and a test server would accumulate leftover roles from every interrupted run.
    /// </para>
    /// </summary>
    private static async Task Teardown(IQueryExecutor exec)
    {
        string[] statements =
        [
            $"drop schema if exists {Schema} cascade",
            $"revoke all on database {PgTestServer.Database} from {Login}",
            $"drop role if exists {Login}",
            $"drop role if exists {Group}",
        ];
        // Failures ignored: this runs before setup too, when none of it exists yet.
        foreach (var statement in statements)
            await exec.ExecuteAsync(statement, new QueryOptions(), CancellationToken.None);
    }
}
