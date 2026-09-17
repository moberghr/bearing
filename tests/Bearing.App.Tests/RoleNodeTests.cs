using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Roles and grants in the tree (#120). Two things carry the weight: roles hang off the <b>server</b>,
/// because they are cluster-wide and would be a lie under a database node; and a refused read is reported as
/// refused, never as an empty privilege list.
/// </summary>
public class RoleNodeTests
{
    private static ConnectionInfo Conn() => new()
    {
        Id = Guid.NewGuid(),
        Name = "srv",
        ProviderId = "postgres",
        Host = "h",
        Port = 5432,
        Database = "demo",
        User = "u",
    };

    /// <summary>Expand a server node and wait for the Roles group to append itself.</summary>
    private static async Task<ServerNodeViewModel> Expanded(RoleBrowser browser)
    {
        var server = new ServerNodeViewModel(Conn(), browser);
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RolesLoaded = () => landed.TrySetResult();
        await server.EnsureChildrenAsync();
        await Task.WhenAny(landed.Task, Task.Delay(5000));
        return server;
    }

    private static SchemaGroupNodeViewModel? RolesGroup(ServerNodeViewModel server)
        => server.Children.OfType<SchemaGroupNodeViewModel>().FirstOrDefault(g => g.Title == "Roles");

    private static async Task<RoleNodeViewModel> Role(RoleBrowser browser, string name)
    {
        var server = await Expanded(browser);
        var role = RolesGroup(server)!.Children.Cast<RoleNodeViewModel>().Single(r => r.Title == name);
        await role.EnsureChildrenAsync();
        return role;
    }

    // ---- placement ------------------------------------------------------------------------------------

    [Fact]
    public async Task Roles_hang_off_the_server_after_its_databases()
    {
        // Cluster-wide, so they cannot live under a database — and a database node is exactly where someone
        // would look for them, which is why the group has to be visible at the server level instead.
        var server = await Expanded(new RoleBrowser());

        var group = RolesGroup(server);
        Assert.NotNull(group);
        Assert.False(group.IsExpanded);
        Assert.Equal(["postgres", "shop_app", "shop_readers"], group.Children.Select(c => c.Title));

        // After the databases, which are what a server is expanded for.
        var first = server.Children.ToList().FindIndex(c => c is SchemaGroupNodeViewModel);
        Assert.All(server.Children.Take(first), c => Assert.IsType<DatabaseNodeViewModel>(c));
    }

    [Fact]
    public async Task A_server_that_reports_no_roles_grows_no_group()
    {
        var server = await Expanded(new RoleBrowser { Roles = [] });
        Assert.Null(RolesGroup(server));
    }

    [Fact]
    public async Task A_refused_role_list_leaves_the_databases_alone()
    {
        // Silent on failure, like the size reads: the role list is an addition, and a server that will not
        // show it must not turn the expanded tree into an error message.
        var server = await Expanded(new RoleBrowser { ThrowOnRoles = true });

        Assert.Null(RolesGroup(server));
        Assert.Contains(server.Children, c => c is DatabaseNodeViewModel);
    }

    // ---- the row --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_login_role_and_a_group_role_read_differently()
    {
        var server = await Expanded(new RoleBrowser());
        var rows = RolesGroup(server)!.Children.Cast<RoleNodeViewModel>().ToList();

        Assert.StartsWith("login", rows.Single(r => r.Title == "shop_app").Detail);
        // A role that cannot log in is a bag of privileges other roles are granted — a different thing to
        // look at, and the row says which it is first.
        Assert.StartsWith("group", rows.Single(r => r.Title == "shop_readers").Detail);
    }

    [Fact]
    public async Task A_superuser_is_marked_and_coloured_as_the_exception()
    {
        var server = await Expanded(new RoleBrowser());
        var rows = RolesGroup(server)!.Children.Cast<RoleNodeViewModel>().ToList();

        var superuser = rows.Single(r => r.Title == "postgres");
        Assert.Contains("superuser", superuser.Detail);
        // It makes every other privilege question moot, so it should not read like the rest.
        Assert.NotEqual(rows.Single(r => r.Title == "shop_app").IconColorHex, superuser.IconColorHex);
    }

    [Fact]
    public async Task An_unlimited_connection_limit_is_not_rendered_as_minus_one()
    {
        var server = await Expanded(new RoleBrowser());
        var rows = RolesGroup(server)!.Children.Cast<RoleNodeViewModel>().ToList();

        Assert.DoesNotContain("-1", rows.Single(r => r.Title == "postgres").Detail);
        Assert.Contains("max 20 connections", rows.Single(r => r.Title == "shop_app").Detail);
    }

    [Fact]
    public async Task An_expiry_is_shown_and_its_absence_is_not_mistaken_for_one()
    {
        var server = await Expanded(new RoleBrowser());
        var rows = RolesGroup(server)!.Children.Cast<RoleNodeViewModel>().ToList();

        Assert.Contains("until 2027-01-01", rows.Single(r => r.Title == "shop_app").Detail);
        // No expiry is a fact, not an absence of information — and definitely not "not visible".
        Assert.DoesNotContain("until", rows.Single(r => r.Title == "shop_readers").Detail);
    }

    [Fact]
    public async Task Membership_is_on_the_row_and_under_it()
    {
        // A role's group membership is frequently the whole explanation of its privileges, so it is visible
        // without expanding — and it comes from the role list already in hand, so it survives a failed
        // grant read.
        var role = await Role(new RoleBrowser(), "shop_app");

        Assert.Contains("member of shop_readers", role.Detail);
        var memberOf = role.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title == "Member of");
        Assert.Equal(["shop_readers"], memberOf.Children.Select(c => c.Title));
    }

    // ---- the grants -----------------------------------------------------------------------------------

    [Fact]
    public async Task Grants_are_spelled_out_rather_than_shown_as_acl_shorthand()
    {
        // "SELECT, INSERT" is the answer; "arwd" is a rendering of a bitmask that nobody reads.
        var role = await Role(new RoleBrowser(), "shop_app");

        var grants = role.Children.OfType<SchemaGroupNodeViewModel>().Single(g => g.Title.StartsWith("Grants"));
        var payment = grants.Children.Single(c => c.Title == "shop.payment");
        Assert.Equal("DELETE, INSERT, SELECT, UPDATE", payment.Detail);
    }

    [Fact]
    public async Task The_grants_group_names_the_database_it_is_about()
    {
        // The role is the server's; a grant is on an object, and objects live in one database. The label is
        // the only place that distinction can be made visible.
        var role = await Role(new RoleBrowser(), "shop_app");

        Assert.Contains(
            role.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title),
            t => t == "Grants on demo");
    }

    [Fact]
    public async Task A_refused_grant_read_says_so_rather_than_showing_no_grants()
    {
        // The one mistake a privilege screen must not make: "this role can do nothing here" and "you are not
        // allowed to find out" are different answers.
        var role = await Role(new RoleBrowser(), "postgres");

        Assert.Contains(role.Children, c => c.Title == "Not visible to this role");
        Assert.DoesNotContain(role.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title),
            t => t.StartsWith("Grants"));
    }

    [Fact]
    public async Task A_role_with_genuinely_no_grants_says_that_instead()
    {
        var role = await Role(new RoleBrowser(), "shop_readers");
        // shop_readers does have grants in the fixture, so drive the empty case explicitly.
        var empty = await Role(new RoleBrowser { Empty = true }, "shop_readers");

        Assert.Contains(role.Children.OfType<SchemaGroupNodeViewModel>().Select(g => g.Title),
            t => t.StartsWith("Grants"));
        Assert.Contains(empty.Children, c => c.Title == "No grants on demo");
    }

    [Fact]
    public async Task A_thrown_grant_read_is_reported_with_a_redacted_message()
    {
        // The browser opens its own connections, so a connect-time Npgsql failure can quote a whole
        // connection string — password included (§1.1).
        var role = await Role(new RoleBrowser { ThrowOnGrants = true }, "shop_app");

        var failure = role.Children.Single(c => c.Title.StartsWith("Couldn't read grants"));
        Assert.DoesNotContain("hunter2", failure.Title);
    }

    // ---- the summary ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_role_summary_says_what_it_does_not_show()
    {
        // Someone reading a privileges screen may reasonably wonder whether the password is in here
        // somewhere. It is never read — pg_roles masks it and pg_authid is not touched — and saying so is
        // cheaper than leaving them to wonder.
        var role = await Role(new RoleBrowser(), "shop_app");
        var summary = await role.LoadDefinitionAsync(CancellationToken.None);

        Assert.Contains("no password material is read or shown", summary);
        Assert.Contains("grants shown are for the database demo only", summary);
        Assert.Contains("connection limit: 20", summary);
        Assert.Contains("member of: shop_readers", summary);
    }

    [Fact]
    public async Task The_summary_distinguishes_no_expiry_from_a_date()
    {
        var withExpiry = await Role(new RoleBrowser(), "shop_app");
        var without = await Role(new RoleBrowser(), "shop_readers");

        Assert.Contains("valid until: 2027-01-01", await withExpiry.LoadDefinitionAsync(CancellationToken.None));
        Assert.Contains("valid until: no expiry", await without.LoadDefinitionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_summary_says_unlimited_rather_than_minus_one()
        => Assert.Contains(
            "connection limit: unlimited",
            await (await Role(new RoleBrowser(), "postgres")).LoadDefinitionAsync(CancellationToken.None));

    // ---- the fake --------------------------------------------------------------------------------------

    /// <summary>
    /// The demo catalog's roles and grants, which already cover the three grant states (readable, empty,
    /// refused). Failure modes are switches rather than separate fakes.
    /// </summary>
    private sealed class RoleBrowser : ISchemaBrowser
    {
        public IReadOnlyList<RoleInfo> Roles { get; init; } = DemoCatalog.Roles();
        public bool ThrowOnRoles { get; init; }
        public bool ThrowOnGrants { get; init; }
        public bool Empty { get; init; }

        public Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["demo"]);

        public Task<IReadOnlyList<RoleInfo>> GetRolesAsync(ConnectionInfo connection, CancellationToken ct)
            => ThrowOnRoles
                ? Task.FromException<IReadOnlyList<RoleInfo>>(new InvalidOperationException("permission denied"))
                : Task.FromResult(Roles);

        public Task<RoleGrants> GetRoleGrantsAsync(
            ConnectionInfo connection, string database, string roleName, CancellationToken ct)
        {
            if (ThrowOnGrants)
                return Task.FromException<RoleGrants>(
                    // The shape §1.1 is about: a driver error carrying the credential.
                    new InvalidOperationException("failed: Host=h;Username=u;Password=hunter2"));
            return Task.FromResult(Empty ? RoleGrants.Of([]) : DemoCatalog.GrantsOf(roleName));
        }

        /// <summary>Tablespaces (#119 follow-up). Empty here for the same reason the roles are.</summary>
        public Task<IReadOnlyList<SchemaObjectInfo>> GetTablespacesAsync(
            ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SchemaObjectInfo>>([]);

        public Task<DatabaseObjects> GetObjectsAsync(ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(new DatabaseObjects(DemoCatalog.Snapshot(), DemoCatalog.Routines()));

        public Task<DatabaseObjectKinds> GetDatabaseObjectKindsAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult(DatabaseObjectKinds.Empty);

        public Task<TableDetails> GetTableDetailsAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult(TableDetails.Empty);

        public Task<string> GetViewDefinitionAsync(
            ConnectionInfo connection, string database, long tableId, CancellationToken ct)
            => Task.FromResult("select 1");

        public Task<IReadOnlyList<RelationSize>> GetRelationSizesAsync(
            ConnectionInfo connection, string database, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RelationSize>>([]);

        public Task<IReadOnlyList<DatabaseSize>> GetDatabaseSizesAsync(ConnectionInfo connection, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DatabaseSize>>([]);

        public Task<string> GetRoutineDefinitionAsync(
            ConnectionInfo connection, string database, long routineId, CancellationToken ct)
            => Task.FromResult("CREATE FUNCTION …");

        public Task InvalidateAsync(Guid connectionId) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
