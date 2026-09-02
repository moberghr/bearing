using Bearing.Core.Data;
using Bearing.Core.Schema;
using Bearing.Data;
using Bearing.Data.SqlServer;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The <c>sys.*</c> catalog reads, against a live server. Every query in
/// <see cref="SqlServerMetadataReader"/> was written from documentation, so this suite is what turns it from
/// plausible into verified — in particular the composite-FK column ordering (a mis-ordered read pairs a
/// column with the wrong partner and FK navigation lands on the wrong row) and the DDL-shaped type strings,
/// where <c>max_length</c> being in bytes is an easy off-by-double.
/// <para>
/// The fixture is created and dropped per test class run rather than assuming a sample database: it is a
/// composite-PK parent, a child whose two-column FK deliberately points at the parent's key columns in the
/// <em>opposite</em> order, a view and a stored procedure.
/// </para>
/// </summary>
public class SqlServerMetadataTests
{
    private static ConnectionInfo Info() => MsSqlTestServer.Info();
    private static string Password => MsSqlTestServer.Password;

    private const string Parent = "bearing_meta_parent";
    private const string Child = "bearing_meta_child";
    private const string View = "bearing_meta_view";
    private const string Proc = "bearing_meta_proc";

    /// <summary>
    /// The fixture. <c>fk (y, x) references parent (a, b)</c> is the point of it: the child's second column
    /// pairs with the parent's first, so a reader that ignored <c>constraint_column_id</c> would produce a
    /// mapping that still looks well-formed and is wrong.
    /// </summary>
    private static async Task CreateFixtureAsync(IQueryExecutor exec)
    {
        await DropFixtureAsync(exec);
        var results = await exec.ExecuteAsync(
            $"""
            create table dbo.{Parent} (
                a int not null,
                b int not null,
                note nvarchar(50) null,
                amount decimal(18,2) null,
                blob varbinary(max) null,
                constraint pk_{Parent} primary key (a, b));
            create table dbo.{Child} (
                x int not null primary key,
                y int not null,
                constraint fk_{Child} foreign key (y, x) references dbo.{Parent} (a, b));
            """, new QueryOptions(), CancellationToken.None);
        Assert.All(results, r => Assert.True(r.Success, r.Error?.Message));

        // CREATE VIEW / CREATE PROCEDURE must each begin a batch, so they cannot ride along above.
        foreach (var ddl in new[]
        {
            $"create view dbo.{View} as select a, b from dbo.{Parent}",
            $"create procedure dbo.{Proc} @id int, @label nvarchar(10) as select @id as id, @label as label",
        })
        {
            var r = Assert.Single(await exec.ExecuteAsync(ddl, new QueryOptions(), CancellationToken.None));
            Assert.True(r.Success, r.Error?.Message);
        }
    }

    private static Task DropFixtureAsync(IQueryExecutor exec) => exec.ExecuteAsync(
        $"""
        drop view if exists dbo.{View};
        drop procedure if exists dbo.{Proc};
        drop table if exists dbo.{Child};
        drop table if exists dbo.{Parent};
        """, new QueryOptions(), CancellationToken.None);

    [SkippableFact]
    public async Task Loads_tables_columns_and_the_primary_key()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);
        await CreateFixtureAsync(exec);
        try
        {
            var snapshot = await reader.LoadSnapshotAsync(MsSqlTestServer.Database, CancellationToken.None);

            var parent = snapshot.ResolveTable("dbo", Parent);
            Assert.NotNull(parent);
            Assert.Equal(RelationKind.Table, parent!.Kind);

            // A view is 'V' and stays a plain View: SQL Server has no materialized view (an indexed view is
            // still 'V'), so inventing one would be a lie the schema tree then draws.
            var view = snapshot.ResolveTable("dbo", View);
            Assert.NotNull(view);
            Assert.Equal(RelationKind.View, view!.Kind);

            // One element by design: T-SQL has a single default schema per user, not a path. The bare-name
            // resolution is only asserted when that schema is the one the fixture lives in — a login whose
            // default schema is not dbo is a legitimate setup, and over-qualifying is the documented
            // consequence of not modelling SQL Server's implicit dbo fallback.
            Assert.Single(snapshot.SearchPath);
            Assert.Contains("dbo", snapshot.Schemas);
            if (string.Equals(snapshot.SearchPath[0], "dbo", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(parent, snapshot.ResolveTable(null, Parent));

            var cols = snapshot.ColumnsOf(parent.Id);
            Assert.Equal(new[] { "a", "b", "note", "amount", "blob" }, cols.Select(c => c.Name));

            // The composite PK is both key columns and nothing else.
            Assert.Equal(new[] { "a", "b" }, cols.Where(c => c.IsPrimaryKey).Select(c => c.Name));
            Assert.Equal(new[] { true, true, false, false, false }, cols.Select(c => c.NotNull));

            // The type as it reads in DDL. nvarchar(50) is the assertion that matters most: max_length is
            // in bytes, so a Unicode column reports 100 and a naive read would say nvarchar(100).
            Assert.Equal("int", cols.Single(c => c.Name == "a").DataType);
            Assert.Equal("nvarchar(50)", cols.Single(c => c.Name == "note").DataType);
            Assert.Equal("decimal(18,2)", cols.Single(c => c.Name == "amount").DataType);
            Assert.Equal("varbinary(max)", cols.Single(c => c.Name == "blob").DataType);
        }
        finally
        {
            await DropFixtureAsync(exec);
        }
    }

    /// <summary>
    /// The composite FK, column for column. <c>order by constraint_column_id</c> is what pairs
    /// <c>ParentOrdinals[i]</c> with <c>ReferencedOrdinals[i]</c>; the fixture's crossed key exists so that
    /// dropping the ordering produces a visibly wrong pairing instead of an accidentally right one.
    /// </summary>
    [SkippableFact]
    public async Task A_composite_foreign_key_pairs_its_columns_in_constraint_order()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);
        await CreateFixtureAsync(exec);
        try
        {
            var snapshot = await reader.LoadSnapshotAsync(MsSqlTestServer.Database, CancellationToken.None);
            var parent = snapshot.ResolveTable("dbo", Parent)!;
            var child = snapshot.ResolveTable("dbo", Child)!;

            var fk = Assert.Single(snapshot.ForeignKeysTouching(child.Id));
            Assert.Equal($"fk_{Child}", fk.Name);
            Assert.Equal(child.Id, fk.ParentTableId);          // "parent" is the referencing side
            Assert.Equal(parent.Id, fk.ReferencedTableId);

            // Read the ordinals back as names, so the assertion says what the mapping means rather than
            // repeating whatever column_id the server happened to assign.
            var childCols = snapshot.ColumnsOf(child.Id);
            var parentCols = snapshot.ColumnsOf(parent.Id);
            var pairs = fk.ParentOrdinals
                .Zip(fk.ReferencedOrdinals, (p, r) => (
                    Child: childCols.Single(c => c.Ordinal == p).Name,
                    Parent: parentCols.Single(c => c.Ordinal == r).Name))
                .ToList();

            Assert.Equal(new[] { ("y", "a"), ("x", "b") }, pairs);

            // The same constraint is reachable from the referenced side — that is what "touching" means, and
            // it is how the referencing rows are found from a parent row.
            Assert.Contains(snapshot.ForeignKeysTouching(parent.Id), f => f.Id == fk.Id);
        }
        finally
        {
            await DropFixtureAsync(exec);
        }
    }

    [SkippableFact]
    public async Task Reads_routines_with_their_arguments_and_kind()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);
        await CreateFixtureAsync(exec);
        try
        {
            var routines = await reader.GetRoutinesAsync(CancellationToken.None);
            var proc = routines.SingleOrDefault(r => r.Name == Proc);
            Assert.NotNull(proc);

            Assert.Equal(RoutineKind.Procedure, proc!.Kind);
            Assert.Equal("dbo", proc.Schema);
            Assert.Equal("@id int, @label nvarchar(10)", proc.Arguments);
            Assert.Equal("", proc.ReturnType);      // a procedure returns nothing to describe

            var definition = await reader.GetRoutineDefinitionAsync(proc.Id, CancellationToken.None);
            Assert.Contains(Proc, definition);
            Assert.Contains("procedure", definition, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropFixtureAsync(exec);
        }
    }

    [SkippableFact]
    public async Task Reads_a_view_definition()
    {
        var provider = new ProviderRegistry().Get(SqlServerProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(Info(), Password);
        await MsSqlTestServer.RequireAsync(factory);

        var exec = provider.CreateQueryExecutor(factory);
        var reader = provider.CreateMetadataReader(factory);
        await CreateFixtureAsync(exec);
        try
        {
            var snapshot = await reader.LoadSnapshotAsync(MsSqlTestServer.Database, CancellationToken.None);
            var view = snapshot.ResolveTable("dbo", View)!;

            var definition = await reader.GetViewDefinitionAsync(view.Id, CancellationToken.None);
            Assert.Contains("select", definition, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Parent, definition);

            // An id nobody can see returns empty rather than throwing — the schema tree asks about whatever
            // node the user clicked, and a permission gap is not an error there.
            Assert.Equal("", await reader.GetViewDefinitionAsync(-1, CancellationToken.None));
        }
        finally
        {
            await DropFixtureAsync(exec);
        }
    }
}
