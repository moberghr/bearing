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
    [InlineData("qualify", "`qualify`")]            // reserved from MySQL 8.4 (measured: 1064 bare)
    [InlineData("tablesample", "`tablesample`")]    // reserved on MySQL 8.4 (information_schema.keywords)
    [InlineData("sql_after_gtids", "`sql_after_gtids`")]   // reserved on MariaDB 11.4 (measured: 1064 bare)
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
                "select * from `t` where a = 'it''s' and b = 42 and c = 0xDEADBEEF and d = X'CAFE' and e = \"q\" # note"));

    // Under NO_BACKSLASH_ESCAPES `'it\'` closes at the backslash and the next quote opens a literal that runs
    // to the end: the log cannot know the server's sql_mode, so every span either reading calls a literal
    // goes, and the shape gives way.
    [Fact]
    public void A_backslash_redacts_what_either_reading_calls_a_literal()
        => Assert.Equal(
            "select * from `t` where a = '?",
            My.RedactLiterals("select * from `t` where a = 'it\\'s' and b = 42 # note"));

    [Theory]
    // Default reading: one SELECT whose literal holds the password. NO_BACKSLASH_ESCAPES: the password is a
    // literal of its own. Either way it must not reach the log.
    [InlineData("select 'a\\', 'hunter2' #'", "hunter2")]
    [InlineData("select 'a\\'; set password = 'hunter2'; -- '", "hunter2")]
    [InlineData("select \"x\\\" , 'hunter2' #\"", "hunter2")]
    public void A_value_one_reading_calls_code_is_still_redacted(string sql, string secret)
        => Assert.DoesNotContain(secret, My.RedactLiterals(sql));

    [Fact]
    public void A_backslash_that_both_readings_agree_on_keeps_the_shape()
        => Assert.Equal("select '?' from t", My.RedactLiterals("select 'C:\\\\' from t"));

    // ---- Splitting ----

    [Fact]
    public void Semicolons_split_but_not_inside_strings_names_or_comments()
    {
        var spans = My.SplitStatements("select 'a;b', `c;d` from t; -- x;y\nselect 2 # z;w\n;");
        Assert.Equal(new[] { "select 'a;b', `c;d` from t", "-- x;y\nselect 2 # z;w" }, spans.Select(s => s.Text));
    }

    [Fact]
    public void End_case_closes_the_case_and_opens_nothing()
    {
        // Counting the CASE of END CASE as a new block left the counter above zero, and every later `;`
        // stopped splitting — the DROP below rode along inside the CREATE.
        const string sql = "create procedure p() begin case x when 1 then select 1; end case; end; drop table important;";
        var spans = My.SplitStatements(sql);
        Assert.Equal(2, spans.Count);
        Assert.StartsWith("create procedure", spans[0].Text);
        Assert.Equal("drop table important", spans[1].Text);
        Assert.Contains("DROP", WriteGuard.FindRiskyStatements(My, sql));
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
    public void Run_all_of_one_delimiter_wrapped_routine_sends_the_routine_without_the_directives()
    {
        // One statement, so EnsureSeparated used to hand back the buffer as typed — DELIMITER lines and `//`
        // included, which the server rejects as a syntax error.
        const string sql = "DELIMITER //\ncreate procedure p() begin select 1; select 2; end //\nDELIMITER ;\n";
        Assert.Equal(
            "create procedure p() begin select 1; select 2; end",
            StatementSplitter.EnsureSeparated(My, sql));
        // Without a directive a lone statement is still returned untouched.
        Assert.Equal("select 1;", StatementSplitter.EnsureSeparated(My, "select 1;"));
    }

    [Fact]
    public void Run_all_puts_the_separator_after_a_hash_comment_on_its_own_line()
    {
        // The `;` appended after `# note` was inside the comment, so the server read `select 1 select 2` (1064).
        Assert.Equal(
            "select 1 # note\n;\nselect 2;",
            StatementSplitter.EnsureSeparated(My, "select 1 # note\n;\nselect 2"));
        Assert.Equal("select 1;\nselect 2;", StatementSplitter.EnsureSeparated(My, "select 1;\nselect 2"));
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

    // A CASE expression's END right before a locking clause is not a FOR loop's END FOR: read as one, it left
    // the CASE open and the DELETE rode along with the SELECT the caret was on.
    [Theory]
    [InlineData("select * from t where id = case when @a then 1 else 2 end for update; delete from t;")]
    [InlineData("select * from t order by case when a then 1 else 2 end for share; delete from t;")]
    public void Case_end_before_a_locking_clause_does_not_unbalance_the_split(string sql)
    {
        var spans = My.SplitStatements(sql);
        Assert.Equal(2, spans.Count);
        Assert.Equal("delete from t", spans[1].Text);
    }

    [Fact]
    public void End_for_still_closes_a_for_loop()
    {
        var spans = My.SplitStatements("for i in 1..3 do update t set a = i; end for; select 2;");
        Assert.Equal(2, spans.Count);
        Assert.Equal("select 2", spans[1].Text);
    }

    // MariaDB runs compound statements at the top level: each is one statement to the server, so the
    // splitter must not cut it at its first semicolon — and must cut right after its END.
    [Theory]
    [InlineData("if @a then delete from t; end if; select 2;")]
    [InlineData("IF @a > 1 THEN select 1; ELSEIF @a < 0 THEN select 2; ELSE select 3; END IF; select 2;")]
    [InlineData("if (select count(*) from t) > 0 then delete from t; end if; select 2;")]
    [InlineData("if (@a) then delete from t; end if; select 2;")]
    [InlineData("if exists (select 1 from t) then delete from t; end if; select 2;")]
    [InlineData("if not exists (select 1 from t) then insert into t values (1); end if; select 2;")]
    [InlineData("while @i < 3 do set @i = @i + 1; end while; select 2;")]
    [InlineData("lbl: loop set @i = @i + 1; if @i > 3 then leave lbl; end if; end loop lbl; select 2;")]
    [InlineData("repeat set @i = @i + 1; until @i > 3 end repeat; select 2;")]
    [InlineData("for i in 1..3 do insert into t values (i); end for; select 2;")]
    [InlineData("if @a then if @b then select 1; end if; select 2; end if; select 2;")]
    [InlineData("if @a then while @i < 3 do set @i = @i + 1; end while; end if; select 2;")]
    [InlineData("if @a then select case when @b then if(@c, 1, 2) else 0 end; end if; select 2;")]
    [InlineData("if @a then select repeat('-', 3); end if; select 2;")]
    [InlineData("begin not atomic if @a then select 1; end if; select 2; end; select 2;")]
    public void A_top_level_compound_statement_is_one_statement(string sql)
    {
        var spans = My.SplitStatements(sql);
        Assert.Equal(2, spans.Count);
        Assert.Equal("select 2", spans[1].Text);
    }

    [Theory]
    [InlineData("select if(a, 1, 2) from t; select 2;")]
    [InlineData("select repeat('x', 2); select 2;")]
    [InlineData("drop table if exists t; select 2;")]
    [InlineData("create table if not exists t (a int); select 2;")]
    [InlineData("do if(@a, 1, 2); select 2;")]
    [InlineData("select case when a then if(b, 1, 2) end from t; select 2;")]
    [InlineData("select * from t for update; select 2;")]
    [InlineData("select a from t where b = 1 limit 1 for update; select 2;")]
    public void If_repeat_and_for_outside_statement_position_open_nothing(string sql)
    {
        var spans = My.SplitStatements(sql);
        Assert.Equal(2, spans.Count);
        Assert.Equal("select 2", spans[1].Text);
    }

    [Fact]
    public void A_procedure_body_with_compounds_still_splits_after_its_end()
    {
        const string sql = """
            create procedure p()
            begin
              declare i int default 0;
              if (i > 0) then select 1; end if;
              if exists (select 1 from t) then delete from t; end if;
              lbl: while i < 3 do set i = i + 1; end while lbl;
              repeat set i = i - 1; until i = 0 end repeat;
              case i when 0 then begin select 0; end; else select 1; end case;
            end;
            drop table important;
            """;
        var spans = My.SplitStatements(sql);
        Assert.Equal(2, spans.Count);
        Assert.EndsWith("end", spans[0].Text);
        Assert.Equal("drop table important", spans[1].Text);
    }

    // Fuzzed: a later write must never ride inside an earlier statement. A word after a `.` is a name even
    // when reserved (`t.case`, `t.end` — MySQL reads it so), and a CASE *expression*'s THEN / ELSE lead to a
    // value, so `then begin end` there is a column called begin, not an empty body.
    [Theory]
    [InlineData("select a.case from t a; delete from t;")]
    [InlineData("select a.case, a.x from t a where a.case = 1; delete from t;")]
    [InlineData("update t set t.case = 1; delete from t;")]
    [InlineData("select t.end, t.case from t; delete from t;")]
    [InlineData("select x from t where t.begin = 1; delete from t;")]
    [InlineData("select case when x then begin end from t; delete from t;")]
    [InlineData("select case when k = 1 then begin else created_at end from shifts; delete from t;")]
    [InlineData("select case when k = 1 then 1 else begin end from shifts; delete from t;")]
    [InlineData("select case k when 1 then begin end from shifts; delete from t;")]
    [InlineData("select x from t where y = case when 1 then begin end; delete from t;")]
    [InlineData("select case when a then case when b then begin end end from t; delete from t;")]
    [InlineData("create view begin as select 1; delete from t;")]
    [InlineData("select case when 1 then 2 end for_x from t; delete from t;")]
    [InlineData("select x from t where id = case when 1 then 2 end for update nowait; delete from t;")]
    [InlineData("select x from t where id = case when 1 then 2 end lock in share mode; delete from t;")]
    [InlineData("select (case when 1 then 2 end) for update; delete from t;")]
    [InlineData("if @a then select 1; end  if; delete from t;")]
    [InlineData("lbl: begin not atomic select 1; end lbl; delete from t;")]
    [InlineData("begin not atomic case @a when 1 then select 1; else select 2; end case; end; delete from t;")]
    [InlineData("case @a when 1 then select 1; else select 2; end case; delete from t;")]
    [InlineData("case when @a then begin select 1; end; end case; delete from t;")]
    [InlineData("if @a then case when @b then begin select 1; end; end case; end if; delete from t;")]
    [InlineData("for r in (select a from t) do select r.a; end for; delete from t;")]
    [InlineData("if (1) then select if(1,2,3); end if; delete from t;")]
    [InlineData("do begin; delete from t;")]
    [InlineData("xa begin 'x'; delete from t;")]
    [InlineData("begin /* c */ ; delete from t;")]
    [InlineData("begin # c\n; delete from t;")]
    [InlineData("BEGIN NOT ATOMIC END; delete from t;")]
    [InlineData("create procedure p() begin end; delete from t;")]
    [InlineData("create trigger tr before insert on t for each row begin set new.a = 1; end; delete from t;")]
    [InlineData("create event e on schedule every 1 day do begin delete from x; end; delete from t;")]
    [InlineData("create function f() returns int deterministic begin return case when 1 then 2 end; end; delete from t;")]
    [InlineData("create procedure p() begin lbl: loop leave lbl; end loop lbl; end; delete from t;")]
    [InlineData("create procedure p() begin declare continue handler for sqlstate '02000' begin end; end; delete from t;")]
    [InlineData("create procedure p() begin select case when 1 then 2 end for update; end; delete from t;")]
    [InlineData("create procedure p() begin set @x = case when 1 then begin end; end; delete from t;")]
    [InlineData("delimiter $$\nselect 1$$\ndelimiter ;\ndelete from t;")]
    [InlineData("select @`a;b`; delete from t;")]
    [InlineData("select a.begin from t a; delete from t;")]
    [InlineData("select x.if from t; delete from t;")]
    [InlineData("do case when a then begin end; delete from t;")]
    [InlineData("case @a when 1 then select case when b then begin end; end case; delete from t;")]
    [InlineData("while @i < (select case when 1 then begin end) do set @i = 1; end while; delete from t;")]
    [InlineData("create function f() returns int return case when 1 then begin end; delete from t;")]
    [InlineData("create procedure p() begin case when 1 then begin select 1; end; end case; end; delete from t;")]
    // A name `begin` outside a stored program's header or statement position is never a body.
    [InlineData("lock tables begin read; delete from t;")]
    [InlineData("lock tables begin write, u read; delete from t;")]
    [InlineData("handler begin open; delete from t;")]
    [InlineData("handler begin read first; delete from t;")]
    [InlineData("call begin(); delete from t;")]
    [InlineData("call begin(1, 2); delete from t;")]
    [InlineData("with begin (n) as (select 1) select * from begin; delete from t;")]
    [InlineData("with recursive begin (n) as (select 1) select * from begin; delete from t;")]
    [InlineData("select * from begin; delete from t;")]
    [InlineData("select * from t join begin on t.a = begin.a; delete from t;")]
    [InlineData("insert into begin values (1); delete from t;")]
    [InlineData("insert begin (a) values (1); delete from t;")]
    [InlineData("insert begin set a = 1; delete from t;")]
    [InlineData("replace begin (a) values (1); delete from t;")]
    [InlineData("update begin set a = 1; delete from t;")]
    [InlineData("update t begin set a = 1; delete from t;")]
    [InlineData("update t as begin set begin.a = 1; delete from t;")]
    [InlineData("table begin; delete from t;")]
    [InlineData("describe begin a; delete from t;")]
    [InlineData("explain begin a; delete from t;")]
    [InlineData("select begin(1); delete from t;")]
    [InlineData("select begin.a from t begin; delete from t;")]
    [InlineData("select x from t where begin = 1; delete from t;")]
    [InlineData("select x from t begin for update; delete from t;")]
    [InlineData("select x from t begin lock in share mode; delete from t;")]
    [InlineData("select x from t begin use index (i); delete from t;")]
    [InlineData("alter table t change a begin int; delete from t;")]
    [InlineData("alter table t alter begin set default 1; delete from t;")]
    [InlineData("alter table t add index begin (a); delete from t;")]
    [InlineData("alter table t add constraint begin unique (a); delete from t;")]
    [InlineData("alter table t rename index begin to i; delete from t;")]
    [InlineData("create database begin character set utf8mb4; delete from t;")]
    [InlineData("create user begin@localhost identified by 'x'; delete from t;")]
    [InlineData("create sequence begin start with 1; delete from t;")]
    [InlineData("flush tables begin with read lock; delete from t;")]
    [InlineData("create procedure begin() begin select 1; end; delete from t;")]
    [InlineData("create function begin() returns int return 1; delete from t;")]
    [InlineData("create trigger begin before insert on t for each row set new.a = 1; delete from t;")]
    [InlineData("create procedure p() begin lock tables begin read; select 1; end; delete from t;")]
    [InlineData("create procedure p() begin call begin(); end; delete from t;")]
    [InlineData("create trigger tr before insert on t for each row call begin(); delete from t;")]
    // `do` and `atomic` are not reserved either: a table called `do` is not a WHILE's DO.
    [InlineData("select * from do for update; delete from t;")]
    [InlineData("select * from t do for update; delete from t;")]
    [InlineData("select * from atomic for update; delete from t;")]
    [InlineData("select * from t as atomic for update; delete from t;")]
    [InlineData("create event e on schedule every 1 day do for i in 1..2 do select 1; end for; delete from t;")]
    [InlineData("while @i < 3 do if @i then select 1; end if; end while; delete from t;")]
    public void A_later_write_is_never_merged_into_an_earlier_statement(string sql)
    {
        var spans = My.SplitStatements(sql);
        Assert.Equal("delete from t", spans[^1].Text);
        Assert.True(spans.Count >= 2);
    }

    [Fact]
    public void A_case_statement_still_reads_then_begin_as_a_body()
    {
        var spans = My.SplitStatements("case when @a then begin select 1; delete from x; end; end case; select 2;");
        Assert.Equal(2, spans.Count);
        Assert.Equal("select 2", spans[1].Text);
    }

    [Theory]
    [InlineData("update t set t.case = 1; delete from x;")]
    [InlineData("select a.case from t a; delete from x;")]
    public void Merged_statements_are_no_longer_merged_through_a_qualified_reserved_word(string sql)
        => Assert.Equal(2, My.SplitStatements(sql).Count);

    [Fact]
    public void A_top_level_compound_is_described_once_with_the_write_in_its_body()
    {
        var statements = WriteGuard.Describe(My, "if @a then delete from t; end if; select 1;");
        Assert.Equal(2, statements.Count);
        Assert.Equal("IF", statements[0].Verb);
        Assert.Contains("DELETE", statements[0].RiskyVerbs);
        Assert.False(statements[1].IsRisky);
    }

    // ---- Write guard ----

    [Theory]
    [InlineData("select * from film")]
    [InlineData("show tables")]
    [InlineData("describe film")]
    [InlineData("select `delete` from t where note = 'drop table x'")]
    [InlineData("with x as (select 1) select * from x for update")]
    [InlineData("with c as (select id from t for update) select * from c for update")]
    [InlineData("explain select * from t for update")]
    [InlineData("select a into @v from t")]
    [InlineData("with c as (select replace(name, 'a', 'b') n from t) select * from c")]
    [InlineData("with c as (select insert(name, 1, 2, 'x') n, truncate(price, 2) p from t) select * from c")]
    [InlineData("explain select replace (name, 'a', 'b') from t")]
    public void Reads_are_not_risky(string sql)
        => Assert.False(WriteGuard.HasRisk(My, sql));

    [Theory]
    [InlineData("replace into t values (1)", "REPLACE")]
    [InlineData("load data infile '/tmp/x' into table t", "LOAD")]
    [InlineData("rename table a to b", "RENAME")]
    [InlineData("select * from t into outfile '/tmp/x'", "SELECT INTO OUTFILE")]
    [InlineData("with x as (select 1) delete from t", "DELETE")]
    [InlineData("explain analyze delete from t", "DELETE")]
    [InlineData("analyze delete from t where id = 1", "DELETE")]          // MariaDB runs it
    [InlineData("analyze format=json update t set a = 1", "UPDATE")]
    [InlineData("/*!50000 drop table t */", "DROP")]
    [InlineData("insert into t values (1) on duplicate key update a = 1", "INSERT")]
    [InlineData("with c as (select id from t for update) update t2 set v = 1 where id in (select id from c)", "UPDATE")]
    [InlineData("with c as (select id from t for update) update t join c using (id) set x = 1", "UPDATE")]  // PR #180 review
    [InlineData("(select 1 into dumpfile '/var/lib/mysql-files/x')", "SELECT INTO DUMPFILE")]   // MySQL 8.4 runs it
    [InlineData("with c as (select replace(name, 'a', 'b') n from t) replace into t2 select * from c", "REPLACE")]
    public void Writes_are_named(string sql, string verb)
        => Assert.Contains(verb, WriteGuard.FindRiskyStatements(My, sql));

    [Theory]
    [InlineData("select begin from shifts; delete from shifts where id = 1;")]
    [InlineData("select id, begin, end from shifts; delete from shifts where id = 1;")]
    [InlineData("select x from t having begin is null; delete from t where id = 1;")]
    [InlineData("select x from t where not begin and x = 1; delete from t where id = 1;")]
    [InlineData("select begin end from shifts; delete from t where id = 1;")]
    [InlineData("select case when begin then 1 end from t; delete from t where id = 1;")]
    [InlineData("select x from t where a in (1) and x like begin; delete from t where id = 1;")]
    [InlineData("select x from t where x between begin and 3; delete from t where id = 1;")]
    [InlineData("select x from t join u on begin; delete from t where id = 1;")]
    [InlineData("select x from t where x is begin; delete from t where id = 1;")]
    [InlineData("select x from t where x in begin; delete from t where id = 1;")]
    [InlineData("select x from t where begin is not null; delete from t where id = 1;")]
    [InlineData("select x from t where begin in (1, 2); delete from t where id = 1;")]
    [InlineData("select x from t where begin between 1 and 2; delete from t where id = 1;")]
    [InlineData("select x from t where begin like 'a%'; delete from t where id = 1;")]
    [InlineData("select x from t order by begin limit 1; delete from t where id = 1;")]
    [InlineData("select begin into @b from t; delete from t where id = 1;")]
    [InlineData("select * from begin where id = 1; delete from t where id = 1;")]
    [InlineData("alter table shifts add column begin datetime; delete from shifts where id = 1;")]
    [InlineData("alter table shifts add begin datetime not null; delete from shifts where id = 1;")]
    [InlineData("alter table shifts modify begin datetime(6); delete from shifts where id = 1;")]
    [InlineData("alter table shifts change begin starts_at datetime; delete from shifts where id = 1;")]
    [InlineData("alter table shifts rename column begin to starts_at; delete from shifts where id = 1;")]
    [InlineData("create procedure p() begin declare begin int; select 1; end; delete from t where id = 1;")]
    public void A_column_called_begin_opens_no_block(string sql)
    {
        var statements = WriteGuard.Describe(My, sql);
        Assert.Equal(2, statements.Count);
        Assert.Contains("DELETE", statements[1].RiskyVerbs);
    }

    [Fact]
    public void A_statement_merged_by_the_splitter_still_names_the_write_inside_it()
    {
        // A stored program's header is where a body may follow, so an alias called `begin` there still reads as
        // a block: the DELETE stays inside the CREATE's statement, and must be named there rather than ride the
        // CREATE's verdict.
        var statement = WriteGuard.Describe(My, "create procedure p() select a from t begin for update; delete from x;").First();
        Assert.True(statement.NamesAWrite);
        Assert.Contains("DELETE", statement.RiskyVerbs);
    }

    [Theory]
    [InlineData("begin not atomic update t set a = 1; delete from x; end", "DELETE")]
    [InlineData("begin not atomic delete from t; drop table x; end", "DROP")]
    [InlineData("insert into t values (1) on duplicate key update a = 1", "INSERT")]
    public void Every_write_a_statement_carries_is_named_not_just_the_first(string sql, string verb)
        => Assert.Contains(verb, WriteGuard.Describe(My, sql).First().RiskyVerbs);

    [Fact]
    public void A_write_lead_with_a_merged_write_after_it_names_both()
    {
        // An alias called `begin` in a stored program's header opens a block the splitter cannot see past; the
        // CREATE leads, and the DELETE after the kept `;` runs with it, so the confirmation must name it too.
        var statement = WriteGuard.Describe(My, "create procedure p() select a from t begin for update; delete from x;").First();
        Assert.Contains("CREATE", statement.RiskyVerbs);
        Assert.Contains("DELETE", statement.RiskyVerbs);
    }

    [Fact]
    public void On_duplicate_key_update_is_not_named_a_separate_update()
        => Assert.DoesNotContain("UPDATE",
            WriteGuard.Describe(My, "with c as (select 1 a) insert into t select a from c on duplicate key update a = 1")
                .Single().RiskyVerbs);

    [Fact]
    public void A_labelled_compound_is_described_by_its_statement_not_its_label()
        => Assert.Equal("BEGIN", WriteGuard.Describe(My, "lbl: begin not atomic select 1; end lbl").Single().Verb);

    [Theory]
    [InlineData("with c as (select id from t for update) update t2 set v = 1 where id in (select id from c)")]
    [InlineData("with c as (select id from t for update) update t join c using (id) set x = 1")]  // PR #180 review
    public void A_real_update_after_a_locking_clause_is_a_named_write(string sql)
    {
        // Each UPDATE is judged by its own position: the first is FOR UPDATE, the second is the write.
        var statement = WriteGuard.Describe(My, sql).Single();
        Assert.True(statement.NamesAWrite);
        Assert.Contains("UPDATE", statement.RiskyVerbs);
    }

    [Theory]
    [InlineData("begin not atomic delete from orders; end", "DELETE")]
    [InlineData("lbl: begin not atomic insert into t values (1); end lbl", "INSERT")]
    [InlineData("begin not atomic while 1 do update t set a = 1; end while; end", "UPDATE")]
    [InlineData("if 1 then delete from t", "DELETE")]
    [InlineData("for i in 1..3 do insert into t values (i); end for", "INSERT")]
    public void A_compound_statement_names_the_write_in_its_body(string sql, string verb)
    {
        var statement = WriteGuard.Describe(My, sql).First();
        Assert.True(statement.NamesAWrite);
        Assert.Contains(verb, statement.RiskyVerbs);
    }

    [Fact]
    public void A_compound_statement_with_no_named_write_is_still_confirmed()
    {
        var statement = WriteGuard.Describe(My, "begin not atomic select * from t for update; end").Single();
        Assert.True(statement.IsRisky);
        Assert.True(statement.FromConservativeDefault);
        Assert.False(statement.NamesAWrite);
    }

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
    [InlineData("set names utf8mb4, autocommit = 1", "SET autocommit")]
    [InlineData("set session sql_mode = '', session autocommit = 1", "SET autocommit")]
    [InlineData("create table t (a int)", "CREATE (commits implicitly)")]
    [InlineData("truncate table t", "TRUNCATE (commits implicitly)")]
    [InlineData("lock tables t read", "LOCK (commits implicitly)")]
    [InlineData("set password = 'x'", "SET PASSWORD (commits implicitly)")]
    [InlineData("set password for 'u'@'%' = 'x'", "SET PASSWORD (commits implicitly)")]
    [InlineData("load index into cache t", "LOAD INDEX (commits implicitly)")]
    [InlineData("start replica", "START REPLICA (commits implicitly)")]
    [InlineData("stop slave", "STOP SLAVE (commits implicitly)")]
    [InlineData("start group_replication", "START GROUP_REPLICATION (commits implicitly)")]
    [InlineData("change master to master_host = 'h'", "CHANGE MASTER (commits implicitly)")]
    [InlineData("change replication source to source_host = 'h'", "CHANGE REPLICATION (commits implicitly)")]
    [InlineData("reset replica", "RESET (commits implicitly)")]
    [InlineData("reset master", "RESET (commits implicitly)")]
    public void Transaction_control_includes_the_implicit_commits(string sql, string expected)
        => Assert.Equal(expected, WriteGuard.Describe(My, sql).Single().TransactionControl);

    [Theory]
    [InlineData("create temporary table t (a int)")]
    [InlineData("drop temporary table t")]
    [InlineData("insert into t values (1)")]
    [InlineData("select 1")]
    [InlineData("set @password = 1")]
    [InlineData("load data infile '/tmp/x' into table t")]
    public void Ordinary_statements_and_temporary_tables_are_not_transaction_control(string sql)
        => Assert.Null(WriteGuard.Describe(My, sql).Single().TransactionControl);

    [Fact]
    public void Begin_not_atomic_is_a_block_not_a_transaction()
        => Assert.Null(WriteGuard.Describe(My, "begin not atomic select 1; end").Single().TransactionControl);

    [Theory]
    // MariaDB runs these compounds at the top level; what ends the transaction is in the body, not the lead.
    [InlineData("if 1 then create table t (a int); end if", "CREATE (commits implicitly)", true)]
    [InlineData("begin not atomic commit; end", "COMMIT", false)]
    [InlineData("lbl: begin not atomic select 1; truncate table t; end", "TRUNCATE (commits implicitly)", true)]
    [InlineData("while @i < 3 do set autocommit = 1; end while", "SET autocommit", false)]
    public void Transaction_control_inside_a_compound_body_is_seen(string sql, string expected, bool implicitCommit)
    {
        var statement = WriteGuard.Describe(My, sql).Single();
        Assert.Equal(expected, statement.TransactionControl);
        Assert.Equal(implicitCommit, statement.CommitsImplicitly);
    }

    [Theory]
    [InlineData("if 1 then begin select 1; end; end if")]          // a nested block, not a transaction
    [InlineData("if 1 then delete from t; end if")]
    public void A_compound_body_without_transaction_control_is_not_control(string sql)
        => Assert.Null(WriteGuard.Describe(My, sql).Single().TransactionControl);

    // ---- Exposed connections ----

    [Fact]
    public void A_double_quoted_credential_catalog_is_refused_for_an_ansi_quotes_server()
    {
        // Under ANSI_QUOTES "mysql"."user" is the table, not two strings — and the mode is the server's.
        var reason = ExternalSqlPolicy.Refuse(My, "select * from \"mysql\".\"user\"");
        Assert.NotNull(reason);
        Assert.Contains("mysql.user", reason);
        // A double-quoted string that names nothing denied is still a read.
        Assert.Null(ExternalSqlPolicy.Refuse(My, "select \"user\" as label"));
    }

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
    [InlineData("select password from mysql.password_history", "'mysql.password_history' is not readable")]
    [InlineData("show create user current_user()", "'SHOW CREATE USER' is not available")]
    [InlineData("SHOW /* x */ CREATE USER 'root'@'%'", "'SHOW CREATE USER' is not available")]
    [InlineData("show /*!create*/ user root", "'SHOW CREATE USER' is not available")]
    [InlineData("show create server s", "'SHOW CREATE SERVER' is not available")]
    [InlineData("show grants", "'SHOW GRANTS' is not available")]
    [InlineData("show /* x */ grants for current_user()", "'SHOW GRANTS' is not available")]
    [InlineData("show binlog events in 'binlog.000001'", "'SHOW BINLOG EVENTS' is not available")]
    [InlineData("SHOW RELAYLOG EVENTS", "'SHOW RELAYLOG EVENTS' is not available")]
    public void An_exposed_connection_refuses_each_shape_in_its_own_words(string sql, string expected)
        => Assert.Contains(expected, ExternalSqlPolicy.Refuse(My, sql));

    // A server in NO_BACKSLASH_ESCAPES or ANSI_QUOTES mode ends these literals at the backslash, and runs
    // what the default reading calls string contents (the DROP below dropped the table on MariaDB 11.4).
    [Theory]
    [InlineData("select 'a\\'; drop table t; -- '", "DROP")]
    [InlineData("select \"a\\\"; drop table t; -- \"", "DROP")]
    [InlineData("select 'a\\'; delete from t; # '", "DELETE")]
    // No delimiter moves here: the statement splits alike under both readings and only its verdict differs.
    [InlineData("select 'a\\' union select 1 into outfile '/tmp/x' #'", "SELECT INTO OUTFILE")]
    public void A_write_hidden_by_a_backslash_escape_is_still_named(string sql, string verb)
        => Assert.Contains(verb, WriteGuard.FindRiskyStatements(My, sql));

    [Theory]
    [InlineData("select 'a\\' , load_file('/etc/passwd') #'", "'load_file' is not available")]
    [InlineData("select 'a\\' , (select authentication_string from mysql.user) #'", "'mysql.user' is not readable")]
    [InlineData("select \"a\\\" , load_file('/etc/passwd') #\"", "'load_file' is not available")]
    // The executable comment is inside a string by default and code under the other reading.
    [InlineData("select 'a\\' /*!, load_file('/etc/passwd') */ #'", "'load_file' is not available")]
    [InlineData("select \"a\\\" /*!, load_file('/etc/passwd') */ #\"", "'load_file' is not available")]
    public void A_name_hidden_by_a_backslash_escape_is_still_refused_on_an_exposed_connection(string sql, string expected)
        => Assert.Contains(expected, ExternalSqlPolicy.Refuse(My, sql));

    [Theory]
    [InlineData("select 'a\\'; set session transaction read write; -- '")]
    [InlineData("select \"a\\\"; select load_file('/etc/passwd'); -- \"")]
    public void An_exposed_connection_refuses_what_another_sql_mode_would_run(string sql)
        => Assert.NotNull(ExternalSqlPolicy.Refuse(My, sql));

    [Theory]
    [InlineData("select 'it\\'s' from t")]
    [InlineData("select 'C:\\\\temp' from t")]
    [InlineData("select * from t where note = 'a\\'b'; select 2")]
    public void A_backslash_in_a_read_adds_no_risk(string sql)
    {
        Assert.False(WriteGuard.HasRisk(My, sql));
        Assert.Null(ExternalSqlPolicy.Refuse(My, sql));
    }

    [Fact]
    public void A_batch_without_a_backslash_is_read_once()
        => Assert.Equal(2, WriteGuard.Describe(My, "select 'a'; select \"b\"").Count);

    [Theory]
    [InlineData("lock tables t read", "LOCK")]
    [InlineData("unlock tables", "UNLOCK")]
    [InlineData("analyze table t", "ANALYZE")]
    [InlineData("check table t", "CHECK")]
    [InlineData("cache index t in c", "CACHE")]
    [InlineData("set password = 'x'", "SET")]
    [InlineData("start replica", "START")]
    [InlineData("stop replica", "STOP")]
    public void An_implicit_commit_is_refused_as_not_a_read_not_as_transaction_control(string sql, string verb)
    {
        // "It is what would let a read-only session become writable" is true of START TRANSACTION READ WRITE
        // and false of LOCK TABLES (§1.1); these end a transaction only as a side effect.
        var reason = ExternalSqlPolicy.Refuse(My, sql);
        Assert.NotNull(reason);
        Assert.Contains($"'{verb}' is not a read", reason);
        Assert.DoesNotContain("writable", reason);
    }

    [Theory]
    [InlineData("lock tables t read", true)]
    [InlineData("analyze table t", true)]
    [InlineData("create table t (a int)", true)]
    [InlineData("start replica", true)]
    [InlineData("set password = 'x'", true)]
    [InlineData("start transaction", false)]
    [InlineData("commit", false)]
    [InlineData("set autocommit = 1", false)]
    [InlineData("select 1", false)]
    // MariaDB's ANALYZE statement runs inside the transaction (measured on 11.4); only ANALYZE TABLE commits.
    [InlineData("analyze select * from t", false)]
    [InlineData("analyze format=json delete from t where id = 1", false)]
    [InlineData("analyze local table t", true)]
    public void The_dialect_types_an_implicit_commit(string sql, bool expected)
        => Assert.Equal(expected, WriteGuard.Describe(My, sql).Single().CommitsImplicitly);

    [Theory]
    [InlineData("begin not atomic insert into t values (1); create table x (a int); end", true)]
    [InlineData("lbl: begin not atomic update t set a = 1; drop table y; end", true)]
    [InlineData("if 1 then insert into t values (1); create table x (a int); end if", true)]
    [InlineData("repeat delete from t; truncate table u; until 1 end repeat", true)]
    // Not run as it is sent: a stored program's body.
    [InlineData("create procedure p() begin insert into t values (1); create table x (a int); end", false)]
    // Only one kind in the block.
    [InlineData("begin not atomic create table x (a int); drop table y; end", false)]
    [InlineData("begin not atomic insert into t values (1); delete from t; end", false)]
    // Explicit control outranks it, and is refused on its own terms.
    [InlineData("begin not atomic insert into t values (1); commit; end", false)]
    [InlineData("insert into t values (1); create table x (a int)", false)]
    public void A_block_that_mixes_a_write_with_an_implicit_commit_is_flagged(string sql, bool expected)
        => Assert.Equal(expected, WriteGuard.Describe(My, sql).Any(s => s.MixesWriteWithImplicitCommit));

    [Fact]
    public void Other_engines_never_flag_a_mixed_block()
    {
        const string sql = "begin; insert into t values (1); create table x (a int); commit;";
        Assert.DoesNotContain(WriteGuard.Describe(PostgresDialect.Instance, sql), s => s.MixesWriteWithImplicitCommit);
        Assert.DoesNotContain(WriteGuard.Describe(SqlServerDialect.Instance, sql), s => s.MixesWriteWithImplicitCommit);
    }

    [Theory]
    [InlineData("analyze select * from t")]
    [InlineData("analyze format=json update t set a = 1 where id = 1")]
    public void The_analyze_statement_is_not_transaction_control(string sql)
        => Assert.Null(WriteGuard.Describe(My, sql).Single().TransactionControl);

    [Theory]
    [InlineData("select * from film")]
    [InlineData("show tables")]
    [InlineData("describe film")]
    [InlineData("select * from users")]   // `user`/`users` are ordinary names; only mysql.user is the catalog
    [InlineData("show create table user")]
    public void An_exposed_connection_runs_reads(string sql)
        => Assert.Null(ExternalSqlPolicy.Refuse(My, sql));
}
