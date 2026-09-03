using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The guard across two dialects: that each reads its own engine, that T-SQL's own lexical shapes cannot
/// trip it, and that the §1.2 safety net still catches a dialect whose scanner cannot be trusted — never
/// narrower for any dialect, so "I could not parse this" must mean "confirm it".
/// </summary>
public class DialectWriteGuardTests
{
    private static readonly ISqlDialect Ss = SqlServerDialect.Instance;

    // ---- The T-SQL guard reads T-SQL ----

    [Fact]
    public void A_plain_select_is_not_risky_now_that_the_guard_reads_t_sql()
    {
        // This is what the fail-safe used to cost: a guarded SQL Server connection confirmed on every
        // single run, SELECTs included, because the guard could not tell them apart.
        Assert.True(Ss.HasDialectAwareGuard);

        var only = Assert.Single(WriteGuard.Describe(Ss, "select * from Orders"));
        Assert.False(only.IsRisky);
        Assert.Empty(only.RiskyVerbs);
        Assert.Equal("SELECT", only.Label);
        Assert.False(WriteGuard.HasRisk(Ss, "select * from Orders"));
    }

    [Fact]
    public void A_batch_is_classified_statement_by_statement()
    {
        var described = WriteGuard.Describe(Ss, "select 1;\ndelete from Orders;\nselect 2;");

        Assert.Equal(new[] { false, true, false }, described.Select(s => s.IsRisky));
        Assert.Equal("DELETE", described[1].Label);
        Assert.True(WriteGuard.HasRisk(Ss, "select 1;\ndelete from Orders;\nselect 2;"));
    }

    [Theory]
    // A delimited name is a name, whatever word is inside it. The PG lexer could not see this at all.
    [InlineData("select * from dbo.[delete]")]
    [InlineData("select [drop], [truncate] from dbo.[Order Details]")]
    [InlineData("select * from \"update\"")]
    // A keyword inside a string literal is data, not a statement.
    [InlineData("select 'delete from Orders' as note")]
    [InlineData("select N'truncate table x' as note")]
    // A variable is not a verb.
    [InlineData("select @drop as x")]
    // ...and a comment is not code, including a nested block comment, which T-SQL allows.
    [InlineData("select 1 -- delete from Orders")]
    [InlineData("/* delete from Orders /* nested */ still a comment */ select 1")]
    public void T_sql_shapes_that_only_look_like_writes_are_not_risky(string sql)
    {
        Assert.False(WriteGuard.HasRisk(Ss, sql));
        Assert.Empty(WriteGuard.FindRiskyStatements(Ss, sql));
    }

    [Theory]
    [InlineData("delete from Orders", "DELETE")]
    [InlineData("update Orders set Total = 1", "UPDATE")]
    [InlineData("exec dbo.Rebuild", "EXEC")]
    [InlineData("execute dbo.Rebuild", "EXECUTE")]
    [InlineData("bulk insert Orders from 'c:\\o.csv'", "BULK")]
    [InlineData("backup database Sales to disk = 'b.bak'", "BACKUP")]
    [InlineData("restore database Sales from disk = 'b.bak'", "RESTORE")]
    [InlineData("deny select on Orders to bob", "DENY")]
    [InlineData("truncate table Orders", "TRUNCATE")]
    public void A_real_write_is_still_caught(string sql, string verb)
        => Assert.Equal(new[] { verb }, WriteGuard.FindRiskyStatements(Ss, sql));

    [Fact]
    public void Select_into_is_a_write_no_leading_verb_reveals()
    {
        Assert.Equal(new[] { "SELECT INTO" },
            WriteGuard.FindRiskyStatements(Ss, "select * into Snapshot from Orders"));
        // ...but an INTO inside a subquery is not the top-level target.
        Assert.Empty(WriteGuard.FindRiskyStatements(
            Ss, "select * from (select top 1 x into_col from t) y"));
    }

    [Fact]
    public void A_data_modifying_cte_is_found_by_scanning_the_preamble()
        => Assert.Equal(new[] { "DELETE" },
            WriteGuard.FindRiskyStatements(Ss, "with old as (select * from Orders) delete from old"));

    [Theory]
    // The conservative half: anything not recognised as a read is confirmed rather than waved through.
    [InlineData("set nocount on")]
    [InlineData("declare @x int")]
    [InlineData("if 1 = 1 delete from Orders")]
    [InlineData("begin transaction")]
    public void A_statement_the_guard_cannot_read_as_a_read_is_treated_as_a_write(string sql)
        => Assert.True(WriteGuard.HasRisk(Ss, sql));

    [Fact]
    public void The_go_batch_separator_splits_statements()
    {
        // A semicolon-only splitter merges these into one statement and mislabels it by its first verb.
        var described = WriteGuard.Describe(Ss, "select * from Orders\nGO\ndelete from Orders\nGO");

        Assert.Equal(2, described.Count);
        Assert.False(described[0].IsRisky);
        Assert.True(described[1].IsRisky);
    }

    // ---- The §1.2 safety net, for a dialect whose scanner cannot be trusted ----

    /// <summary>A dialect that admits its scanner cannot read the engine. Both shipped dialects read their
    /// own, so this is the only way left to exercise the fail-safe — and it must stay exercised: it is what
    /// a third engine gets before its scanner exists.</summary>
    private sealed class UnreadableDialect : ISqlDialect
    {
        public string Id => "unreadable";
        public bool HasDialectAwareGuard => false;
        public IReadOnlySet<string> RiskyVerbs => Ss.RiskyVerbs;
        public IReadOnlyList<StatementRisk> DescribeStatements(string sql) => Ss.DescribeStatements(sql);
        public IReadOnlyList<StatementSpan> SplitStatements(string sql) => Ss.SplitStatements(sql);
        public ISqlParseRules ParseRules => Ss.ParseRules;
        public bool InStringLiteral(string sql, int offset) => Ss.InStringLiteral(sql, offset);
        public string RedactLiterals(string? sql) => Ss.RedactLiterals(sql);

        public string Quote(string i) => Ss.Quote(i);
        public string QuoteIfNeeded(string i) => Ss.QuoteIfNeeded(i);
        public bool NeedsQuoting(string i) => Ss.NeedsQuoting(i);
        public string Unquote(string i) => Ss.Unquote(i);
        public string? TryAppendPage(string s, int o, int l) => Ss.TryAppendPage(s, o, l);
        public string? Wrap(string s, int o, int l) => Ss.Wrap(s, o, l);
        public string? CountWrap(string s) => Ss.CountWrap(s);
        public string InsertStatement(string t, string c, string v, bool r) => Ss.InsertStatement(t, c, v, r);
    }

    private static readonly ISqlDialect Unreadable = new UnreadableDialect();

    [Fact]
    public void Without_a_dialect_aware_guard_even_a_plain_select_is_risky()
    {
        var only = Assert.Single(WriteGuard.Describe(Unreadable, "select * from Orders"));

        Assert.True(only.IsRisky);
        Assert.Empty(only.RiskyVerbs);            // nothing was *found* — that is the point
        Assert.True(WriteGuard.HasRisk(Unreadable, "select * from Orders"));
    }

    [Fact]
    public void The_label_says_why_a_read_is_being_confirmed()
    {
        var only = Assert.Single(WriteGuard.Describe(Unreadable, "select * from Orders"));

        Assert.Contains(StatementRisk.UnparsedDialectNote, only.Label);
        Assert.StartsWith("SELECT", only.Label);
    }

    [Fact]
    public void Every_statement_of_an_unreadable_batch_is_reported_risky_reads_included()
    {
        var described = WriteGuard.Describe(Unreadable, "select 1;\ndelete from Orders;\nselect 2;");

        Assert.Equal(new[] { true, true, true }, described.Select(s => s.IsRisky));
        // The one it did recognise is still labelled by what it does, not by the guard's uncertainty.
        Assert.Equal("DELETE", described[1].Label);
        Assert.DoesNotContain(StatementRisk.UnparsedDialectNote, described[1].Label);
    }

    // ---- Postgres is unchanged by any of this ----

    [Fact]
    public void The_postgres_set_is_a_subset_of_the_sql_server_one()
        // Superset, never subset: the guard may not be narrower for the newer engine.
        => Assert.True(PostgresDialect.Instance.RiskyVerbs.IsSubsetOf(Ss.RiskyVerbs));

    [Fact]
    public void Postgres_keeps_todays_verdicts_including_the_ones_it_misses()
    {
        // EXECUTE of a prepared write is a real Postgres gap, deliberately left alone: widening the
        // Postgres set would change what an existing connection confirms on, which is not this work.
        Assert.True(PostgresDialect.Instance.HasDialectAwareGuard);
        Assert.Empty(WriteGuard.FindRiskyStatements(PostgresDialect.Instance, "execute my_plan(1)"));
        Assert.False(WriteGuard.HasRisk(PostgresDialect.Instance, "select * from film"));
    }

    [Theory]
    [InlineData("select * from film")]
    [InlineData("select 1;\ndelete from rental;\nselect 2;")]
    [InlineData("with removed as (delete from rental returning *) select * into snapshot from removed;")]
    public void The_dialect_less_entry_points_are_the_postgres_ones(string sql)
    {
        var viaDialect = WriteGuard.Describe(PostgresDialect.Instance, sql);
        var viaStatic = WriteGuard.Describe(sql);

        // Element-wise: StatementRisk is a record, but RiskyVerbs is a list, so record equality would
        // compare it by reference and pass for any two descriptions.
        Assert.Equal(viaDialect.Count, viaStatic.Count);
        for (var i = 0; i < viaDialect.Count; i++)
        {
            Assert.Equal(viaDialect[i].Text, viaStatic[i].Text);
            Assert.Equal(viaDialect[i].Verb, viaStatic[i].Verb);
            Assert.Equal(viaDialect[i].RiskyVerbs, viaStatic[i].RiskyVerbs);
            Assert.Equal(viaDialect[i].IsRisky, viaStatic[i].IsRisky);
            Assert.Equal(viaDialect[i].Label, viaStatic[i].Label);
        }

        Assert.Equal(WriteGuard.FindRiskyStatements(PostgresDialect.Instance, sql), WriteGuard.FindRiskyStatements(sql));
        Assert.Equal(WriteGuard.HasRisk(PostgresDialect.Instance, sql), WriteGuard.HasRisk(sql));
    }
}
