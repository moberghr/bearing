using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The allow-list a host outside Bearing sends through. Every refusal case here except the last two was a
/// statement that <b>ran</b> against a connection exposed read-only before this existed — see
/// <see cref="ExternalSqlPolicy"/> for the measurements.
/// </summary>
public class ExternalSqlPolicyTests
{
    private static string? Pg(string sql) => ExternalSqlPolicy.Refuse(PostgresDialect.Instance, sql);

    private static string? TSql(string sql) => ExternalSqlPolicy.Refuse(SqlServerDialect.Instance, sql);

    // ---- the measured bypasses -----------------------------------------------------------------

    /// <summary>
    /// The one that mattered. `begin read write` is not a risky verb, so the deny-list passed it, and the
    /// server honoured it — `select nextval('film_film_id_seq')` then returned 1002 on a connection
    /// exposed read-only. Both spellings of it are refused now.
    /// </summary>
    [Theory]
    [InlineData("begin read write; select nextval('s')")]
    [InlineData("set transaction read write; select nextval('s')")]
    [InlineData("begin; select 1; commit")]
    [InlineData("start transaction; select 1")]
    public void A_statement_that_could_make_the_session_writable_is_refused(string sql)
    {
        Assert.NotNull(Pg(sql));
    }

    /// <summary>
    /// `set default_transaction_read_only = off` did not on its own defeat anything — the GUC governs the
    /// *next* transaction, and the batch is already inside one. It is refused regardless: it is not a read,
    /// and "it happens not to work today" is not a reason to carry a statement whose only purpose here
    /// would be to change what the session is allowed to do.
    /// </summary>
    [Fact]
    public void A_session_setting_is_refused_even_where_it_would_not_have_worked()
    {
        Assert.NotNull(Pg("set default_transaction_read_only = off"));
        Assert.NotNull(Pg("set statement_timeout = 0"));
        Assert.NotNull(Pg("reset all"));
    }

    /// <summary>
    /// `set_config` is `SET` wearing a SELECT's clothes, so the verb allow-list cannot see it — and it is
    /// exactly what would shed a role the startup packet applied. It is on the denied-function list for
    /// that reason rather than as one footgun among many.
    /// </summary>
    [Fact]
    public void Set_config_is_refused_although_it_is_shaped_like_a_read()
    {
        var refusal = Pg("select set_config('default_transaction_read_only', 'off', false)");

        Assert.NotNull(refusal);
        Assert.Contains("set_config", refusal);
    }

    /// <summary>
    /// Measured on a superuser connection with read-only fully in force:
    /// <c>select pg_read_file('/etc/passwd')</c> returned the file, and <c>pg_ls_dir('/etc')</c> listed it.
    /// Read-only bounds writes; it never bounded these, because they are reads — just not of the data.
    /// </summary>
    [Theory]
    [InlineData("select pg_read_file('/etc/passwd')")]
    [InlineData("select pg_ls_dir('/etc')")]
    [InlineData("select pg_terminate_backend(pid) from pg_stat_activity")]
    [InlineData("select lo_import('/etc/shadow')")]
    public void A_function_that_reaches_past_the_data_is_refused(string sql)
    {
        Assert.NotNull(Pg(sql));
    }

    /// <summary>
    /// A quoted identifier is the <b>same name</b>, not one of the evasions this list openly does not catch.
    /// <c>select "pg_read_file"('/etc/passwd')</c> is ordinary Postgres and got through, because the closing
    /// quote sat between the name and the <c>(</c> — while the qualified <c>pg_catalog.pg_read_file(…)</c>
    /// was caught. An asymmetry with no reason behind it, in a list whose stated job is the obvious cases.
    /// </summary>
    [Theory]
    [InlineData("select \"pg_read_file\"('/etc/passwd')")]
    [InlineData("select \"pg_ls_dir\" ('/etc')")]
    [InlineData("select pg_catalog.pg_read_file('/etc/passwd')")]
    [InlineData("select PG_READ_FILE('/etc/passwd')")]
    public void A_denied_function_is_refused_however_it_is_spelled(string sql)
    {
        Assert.NotNull(Pg(sql));
    }

    /// <summary>T-SQL spells the same thing with brackets.</summary>
    [Fact]
    public void A_bracketed_name_is_the_same_name_on_sql_server()
    {
        Assert.NotNull(TSql("select [xp_cmdshell]('dir')"));
        Assert.NotNull(TSql("select xp_cmdshell('dir')"));
    }

    // ---- what still has to work ----------------------------------------------------------------

    [Theory]
    [InlineData("select 1")]
    [InlineData("select * from film where title like 'A%'")]
    [InlineData("with recent as (select * from payment limit 10) select count(*) from recent")]
    [InlineData("explain select * from film")]
    [InlineData("explain analyze select * from film")]
    [InlineData("show statement_timeout")]
    [InlineData("table film")]
    [InlineData("values (1), (2)")]
    [InlineData("select 1; select 2")]
    public void A_read_is_allowed(string sql)
    {
        Assert.Null(Pg(sql));
    }

    /// <summary>A column or alias that merely shares a denied function's name is not a call to it.</summary>
    [Fact]
    public void A_name_that_is_not_a_call_is_left_alone()
    {
        Assert.Null(Pg("select pg_read_file from audit_log"));
        Assert.Null(Pg("select id as set_config from t"));
    }

    // ---- writes, and the unknown ---------------------------------------------------------------

    [Theory]
    [InlineData("update film set title = 'x'")]
    [InlineData("delete from film")]
    [InlineData("drop table film")]
    [InlineData("do $$ begin perform 1; end $$")]
    [InlineData("call some_procedure()")]
    [InlineData("explain analyze update film set title = 'x'")]
    [InlineData("with gone as (delete from film returning *) select * from gone")]
    public void A_write_is_refused_however_it_is_dressed(string sql)
    {
        Assert.NotNull(Pg(sql));
    }

    /// <summary>
    /// The property the allow-list exists for: a shape nobody listed is refused rather than allowed. A
    /// deny-list answers the opposite way, which is right for the editor and wrong for a caller who is not
    /// the person at the keyboard.
    /// </summary>
    [Theory]
    [InlineData("vacuum full film")]
    [InlineData("reindex table film")]
    [InlineData("cluster film using film_pkey")]
    [InlineData("listen channel_name")]
    [InlineData("notify channel_name")]
    [InlineData("lock table film in access exclusive mode")]
    [InlineData("copy film to '/tmp/out.csv'")]
    [InlineData("prepare p as select 1")]
    public void A_statement_nobody_listed_is_refused_by_default(string sql)
    {
        Assert.NotNull(Pg(sql));
    }

    [Fact]
    public void Empty_input_is_refused_rather_than_silently_doing_nothing()
    {
        Assert.NotNull(Pg(""));
        Assert.NotNull(Pg("   \n  "));
    }

    /// <summary>
    /// One write anywhere in a batch refuses the whole batch — the same property the write guard has, and
    /// the reason a script of five reads and one update executes nothing.
    /// </summary>
    [Fact]
    public void One_bad_statement_refuses_the_whole_batch()
    {
        Assert.NotNull(Pg("select 1; select 2; set transaction read write; select 3"));
        Assert.NotNull(Pg("select * from film; update film set title = 'x'"));
    }

    // ---- per engine, not translated ------------------------------------------------------------

    [Fact]
    public void The_allow_list_is_this_engines_own()
    {
        // SHOW and TABLE are Postgres reads; in T-SQL they are not, and the list says so rather than
        // carrying Postgres' vocabulary across (§5.4a).
        Assert.Null(Pg("show statement_timeout"));
        Assert.NotNull(TSql("show statement_timeout"));
        Assert.NotNull(TSql("table film"));

        Assert.Null(TSql("select top 10 * from film"));
        // VALUES is Postgres' standalone read; in T-SQL it is a table constructor, not a statement.
        Assert.Null(Pg("values (1), (2)"));
        Assert.NotNull(TSql("values (1), (2)"));
    }

    /// <summary>
    /// T-SQL needs no separator between statements, so the splitter sees one span whose leading word is a
    /// read while the text runs a write. Found by review and measured: before the fix these were classified
    /// <c>risky=false</c> and <c>ExternalSqlPolicy</c> allowed them, and sent to a live SQL Server
    /// `select 1 drop table t` returned its row and dropped the table.
    /// <para>
    /// The same hole made the <b>editor</b> not prompt for them on a guarded connection (§1.2), which is why
    /// the fix is in <c>TSqlWriteGuard</c> rather than in the allow-list.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("select 1 drop table t")]
    [InlineData("select 1 \n update film set title='x'")]
    [InlineData("select 1 begin drop table t end")]
    [InlineData("select 1 truncate table film")]
    [InlineData("with c as (select 1) select * from c delete from film")]
    public void A_t_sql_batch_without_separators_is_read_whole(string sql)
    {
        // The guard names the write, so the editor confirms it...
        var described = WriteGuard.Describe(SqlServerDialect.Instance, sql);
        Assert.True(described.Any(s => s.IsRisky), "the write guard must see the write");

        // ...and the exposed path refuses it.
        Assert.NotNull(TSql(sql));
    }

    [Fact]
    public void A_verb_inside_a_string_or_brackets_is_not_a_statement()
    {
        // Depth is parenthesis depth and only bare words count, so neither of these is a write.
        Assert.Null(TSql("select * from t where action = 'drop table x'"));
        Assert.Null(TSql("select [update] from t"));
    }

    [Fact]
    public void T_SQL_refuses_its_own_footguns()
    {
        Assert.NotNull(TSql("select * from openrowset('SQLNCLI', 'x', 'select 1')"));
        Assert.NotNull(TSql("exec xp_cmdshell('dir')"));
        // DECLARE is deliberately not an allowed read: it opens many ordinary scripts and is also how
        // dynamic SQL is staged, and an ambiguous shape defaults to refusal.
        Assert.NotNull(TSql("declare @id int; select @id"));
    }
}
