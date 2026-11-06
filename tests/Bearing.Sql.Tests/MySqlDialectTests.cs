using System.Linq;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The generated-text half of MySQL / MariaDB support: quoting, paging, the derived-table wrap, the write
/// guard, the splitter and the literal questions. String assertions only — that a server accepts this text
/// is <c>Bearing.Data.Tests.MySqlExecutorTests</c>' claim, against both engines.
/// </summary>
public class MySqlDialectTests
{
    private static readonly ISqlDialect My = MySqlDialect.Instance;

    [Fact]
    public void Id_matches_the_provider_id() => Assert.Equal("mysql", My.Id);

    // ---- Identifiers ----

    [Theory]
    [InlineData("customers", "customers")]
    [InlineData("Customers", "Customers")]          // case is preserved, never a reason to quote
    [InlineData("order details", "`order details`")]
    [InlineData("order", "`order`")]                // reserved
    [InlineData("returning", "`returning`")]        // reserved on MariaDB only — the union is used
    [InlineData("a`b", "`a``b`")]
    [InlineData("1st", "`1st`")]
    [InlineData("$amount", "$amount")]
    public void Quote_if_needed_backticks_only_what_cannot_stand_bare(string name, string expected)
        => Assert.Equal(expected, My.QuoteIfNeeded(name));

    [Fact]
    public void Unquote_inverts_quote_and_leaves_a_double_quoted_string_alone()
    {
        Assert.Equal("a`b", My.Unquote(My.Quote("a`b")));
        Assert.Equal("\"x\"", My.Unquote("\"x\""));
    }

    // ---- Paging ----

    [Fact]
    public void The_first_page_appends_a_bare_limit_on_its_own_line()
        => Assert.Equal("select * from film\nlimit 100", My.TryAppendPage("select * from film;", 0, 100));

    [Fact]
    public void Later_pages_append_limit_and_offset_without_needing_an_order_by()
        => Assert.Equal("select * from film\nlimit 100 offset 200", My.TryAppendPage("select * from film", 200, 100));

    [Fact]
    public void A_trailing_hash_comment_cannot_swallow_the_page_clause()
    {
        var paged = My.TryAppendPage("select * from film # all of them", 0, 10);
        Assert.Equal("select * from film # all of them\nlimit 10", paged);
    }

    [Theory]
    [InlineData("select * from film limit 5")]
    [InlineData("select * from film for update")]
    [InlineData("select * from film lock in share mode")]
    [InlineData("select count(*) into @n from film")]
    [InlineData("select * from film offset 10 rows fetch next 5 rows only")]
    [InlineData("show tables")]
    [InlineData("select 1; select 2")]
    [InlineData("delete from film")]
    public void Shapes_that_cannot_take_a_page_suffix_are_refused(string sql)
        => Assert.Null(My.TryAppendPage(sql, 0, 10));

    [Fact]
    public void A_backticked_limit_is_a_name_not_the_clause()
        => Assert.NotNull(My.TryAppendPage("select `limit` from t", 0, 10));

    [Fact]
    public void The_wrap_pages_from_outside_a_derived_table()
        => Assert.Equal(
            "select * from (\nselect * from film limit 5\n) as _sq limit 10 offset 20",
            My.Wrap("select * from film limit 5;", 20, 10));

    [Fact]
    public void A_cte_sits_inside_the_derived_table_as_is()
        => Assert.NotNull(My.Wrap("with x as (select 1 a) select * from x", 0, 10));

    [Theory]
    [InlineData("show tables")]
    [InlineData("call p()")]
    [InlineData("select * into @a from t")]
    [InlineData("select 1; select 2")]
    public void Statements_that_cannot_be_a_derived_table_refuse_wrap_and_count(string sql)
    {
        Assert.Null(My.Wrap(sql, 0, 10));
        Assert.Null(My.CountWrap(sql));
    }

    [Fact]
    public void A_parenthesised_union_can_be_counted()
        => Assert.Equal(
            "select count(*) from (\n(select 1) union (select 2)\n) as _sq",
            My.CountWrap("(select 1) union (select 2)"));

    // ---- DML ----

    [Fact]
    public void Insert_offers_returning_and_a_plain_form_for_mysql_to_fall_back_to()
    {
        Assert.Equal("insert into `t` (`a`) values (@p0) returning *", My.InsertStatement("`t`", "`a`", "@p0", true));
        Assert.Equal("insert into `t` (`a`) values (@p0)", My.InsertStatement("`t`", "`a`", "@p0", false));
    }

    [Fact]
    public void Update_never_asks_for_a_read_back_neither_engine_can_give()
        => Assert.Equal(
            "update `t` set `a` = @p0 where `id` = @p1",
            My.UpdateStatement("`t`", "`a` = @p0", "`id` = @p1", new[] { "`a`" }));

    [Theory]
    [InlineData("NOW()", "now()")]
    [InlineData(" uuid ( ) ", "uuid()")]
    [InlineData("default", "default")]
    [InlineData("getdate()", null)]          // T-SQL's, not this engine's
    [InlineData("gen_random_uuid()", null)]  // Postgres'
    public void Edit_expressions_are_this_engines_own_table(string text, string? expected)
        => Assert.Equal(expected, My.TryEditExpression(text));

    [Fact]
    public void Offered_edit_expressions_are_a_subset_of_the_accepted_ones()
        => Assert.All(My.OfferedEditExpressions, e => Assert.Contains(e, My.EditExpressions));

    // ---- Literals ----

    [Fact]
    public void A_backslash_escaped_quote_does_not_end_the_string()
    {
        const string sql = "select 'it\\'s' , x from t";
        Assert.True(My.InStringLiteral(sql, sql.IndexOf("s'", StringComparison.Ordinal) + 1));
        Assert.False(My.InStringLiteral(sql, sql.IndexOf(", x", StringComparison.Ordinal) + 2));
    }

    [Fact]
    public void A_quote_inside_a_backticked_name_opens_no_string()
    {
        const string sql = "select * from `O'Donnell` where ";
        Assert.False(My.InStringLiteral(sql, sql.Length));
    }

    [Fact]
    public void Redaction_strips_values_and_keeps_shape()
        => Assert.Equal(
            "select * from `t` where a = '?' and b = ? and c = ? and d = X'?' and e = \"?\" # note",
            My.RedactLiterals(
                "select * from `t` where a = 'it\\'s' and b = 42 and c = 0xDEADBEEF and d = X'CAFE' and e = \"q\" # note"));

    // ---- Splitting ----

    [Fact]
    public void Semicolons_split_but_not_inside_strings_names_or_comments()
    {
        var spans = My.SplitStatements("select 'a;b', `c;d` from t; -- x;y\nselect 2 # z;w\n;");
        Assert.Equal(new[] { "select 'a;b', `c;d` from t", "-- x;y\nselect 2 # z;w" }, spans.Select(s => s.Text));
    }

    [Fact]
    public void A_procedure_body_without_a_delimiter_is_one_statement()
    {
        const string sql = "create procedure p() begin\n  if 1 then select 1; end if;\n  select 2;\nend;\nselect 3;";
        var spans = My.SplitStatements(sql);
        Assert.Equal(2, spans.Count);
        Assert.StartsWith("create procedure", spans[0].Text);
        Assert.EndsWith("end", spans[0].Text);
        Assert.Equal("select 3", spans[1].Text);
    }

    [Fact]
    public void A_delimiter_directive_changes_the_separator_and_is_not_a_statement()
    {
        const string sql = "DELIMITER //\ncreate procedure p() begin select 1; select 2; end //\nDELIMITER ;\nselect 3;";
        var spans = My.SplitStatements(sql);
        Assert.Equal(
            new[] { "create procedure p() begin select 1; select 2; end", "select 3" },
            spans.Select(s => s.Text));
        // The spans point back into the buffer, so Run sends exactly what the margin shows.
        Assert.Equal(spans[0].Text, sql[spans[0].Start..spans[0].End]);
    }

    [Fact]
    public void A_column_called_delimiter_mid_statement_is_a_column()
    {
        var spans = My.SplitStatements("select\ndelimiter, x from t; select 2;");
        Assert.Equal(2, spans.Count);
    }

    [Fact]
    public void Begin_as_a_transaction_does_not_open_a_block()
    {
        var spans = My.SplitStatements("begin; update t set a = 1; commit;");
        Assert.Equal(3, spans.Count);
    }

    [Fact]
    public void Case_end_inside_a_select_does_not_unbalance_the_split()
    {
        var spans = My.SplitStatements("select case when a then 1 end from t; select 2;");
        Assert.Equal(2, spans.Count);
    }

    // ---- Write guard ----

    [Theory]
    [InlineData("select * from film")]
    [InlineData("show tables")]
    [InlineData("describe film")]
    [InlineData("select `delete` from t where note = 'drop table x'")]
    [InlineData("with x as (select 1) select * from x for update")]
    [InlineData("select a into @v from t")]
    public void Reads_are_not_risky(string sql)
        => Assert.False(WriteGuard.HasRisk(My, sql));

    [Theory]
    [InlineData("replace into t values (1)", "REPLACE")]
    [InlineData("load data infile '/tmp/x' into table t", "LOAD")]
    [InlineData("rename table a to b", "RENAME")]
    [InlineData("select * from t into outfile '/tmp/x'", "SELECT INTO OUTFILE")]
    [InlineData("with x as (select 1) delete from t", "DELETE")]
    [InlineData("explain analyze delete from t", "DELETE")]
    [InlineData("/*!50000 drop table t */", "DROP")]
    [InlineData("insert into t values (1) on duplicate key update a = 1", "INSERT")]
    public void Writes_are_named(string sql, string verb)
        => Assert.Contains(verb, WriteGuard.FindRiskyStatements(My, sql));

    [Theory]
    [InlineData("set @a = 1")]
    [InlineData("prepare s from 'delete from t'")]
    [InlineData("lock tables t write")]
    public void Unrecognised_leads_are_confirmed_by_the_conservative_default(string sql)
    {
        var statement = WriteGuard.Describe(My, sql).Single();
        Assert.True(statement.IsRisky);
        Assert.True(statement.FromConservativeDefault);
        Assert.False(statement.NamesAWrite);
    }

    [Fact]
    public void The_guard_superset_holds_the_postgres_verbs()
        => Assert.Superset(
            PostgresDialect.Instance.RiskyVerbs.ToHashSet(StringComparer.OrdinalIgnoreCase),
            My.RiskyVerbs.ToHashSet(StringComparer.OrdinalIgnoreCase));

    // ---- Transaction control ----

    [Theory]
    [InlineData("start transaction", "START TRANSACTION")]
    [InlineData("begin", "BEGIN")]
    [InlineData("begin work", "BEGIN")]
    [InlineData("commit", "COMMIT")]
    [InlineData("rollback to savepoint a", "ROLLBACK")]
    [InlineData("release savepoint a", "RELEASE SAVEPOINT")]
    [InlineData("set autocommit = 1", "SET autocommit")]
    [InlineData("set @@session.autocommit = 1", "SET autocommit")]
    [InlineData("create table t (a int)", "CREATE (commits implicitly)")]
    [InlineData("truncate table t", "TRUNCATE (commits implicitly)")]
    [InlineData("lock tables t read", "LOCK (commits implicitly)")]
    public void Transaction_control_includes_the_implicit_commits(string sql, string expected)
        => Assert.Equal(expected, WriteGuard.Describe(My, sql).Single().TransactionControl);

    [Theory]
    [InlineData("create temporary table t (a int)")]
    [InlineData("drop temporary table t")]
    [InlineData("insert into t values (1)")]
    [InlineData("select 1")]
    public void Ordinary_statements_and_temporary_tables_are_not_transaction_control(string sql)
        => Assert.Null(WriteGuard.Describe(My, sql).Single().TransactionControl);

    [Fact]
    public void Begin_not_atomic_is_a_block_not_a_transaction()
        => Assert.Null(WriteGuard.Describe(My, "begin not atomic select 1; end").Single().TransactionControl);

    // ---- Exposed connections ----

    [Fact]
    public void Load_file_hidden_in_an_executable_comment_is_still_seen()
    {
        var (called, _) = My.ExternalNameScan("select /*!50000 load_file('/etc/passwd') */");
        Assert.Contains("load_file", called);
    }

    [Fact]
    public void A_backticked_catalog_is_the_catalog()
    {
        var (_, mentioned) = My.ExternalNameScan("select * from `mysql`.`user`");
        Assert.Contains("mysql.user", mentioned);
    }

    [Fact]
    public void A_string_that_names_a_function_calls_nothing()
    {
        var (called, _) = My.ExternalNameScan("select 'load_file(' from t");
        Assert.DoesNotContain("load_file", called);
    }
    [Theory]
    [InlineData("create table t (a int)", "'CREATE' writes")]
    [InlineData("start transaction read write", "'START TRANSACTION' is not allowed")]
    [InlineData("set session transaction read write", "'SET' is not a read")]
    [InlineData("select load_file('/etc/passwd')", "'load_file' is not available")]
    [InlineData("select authentication_string from mysql.user", "'mysql.user' is not readable")]
    public void An_exposed_connection_refuses_each_shape_in_its_own_words(string sql, string expected)
        => Assert.Contains(expected, ExternalSqlPolicy.Refuse(My, sql));

    [Theory]
    [InlineData("select * from film")]
    [InlineData("show tables")]
    [InlineData("describe film")]
    [InlineData("select * from users")]   // `user`/`users` are ordinary names; only mysql.user is the catalog
    public void An_exposed_connection_runs_reads(string sql)
        => Assert.Null(ExternalSqlPolicy.Refuse(My, sql));
}
