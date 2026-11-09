using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The reduction behind #112's row count. Two halves, and the second matters more: what it reduces, and
/// what it refuses to. A statement it gets wrong does not merely fail to show a number — it shows a
/// confident count of rows the statement will not touch, which is worse than the prompt had before.
/// </summary>
public class UpdateDeleteTargetTests
{
    // ---- what reduces ---------------------------------------------------------------------------------

    [Fact]
    public void An_update_with_a_where_clause_reduces_to_its_relation_and_predicate()
    {
        var target = UpdateDeleteTarget.TryReduce("update public.payment set amount = 4.99 where amount > 10");

        Assert.NotNull(target);
        Assert.Equal("UPDATE", target.Verb);
        Assert.Equal("public.payment", target.Relation);
        Assert.Equal("amount > 10", target.Where);
        Assert.False(target.EveryRow);
        Assert.Equal("select * from public.payment where amount > 10", target.RowsSql);
    }

    [Fact]
    public void A_delete_reduces_the_same_way()
    {
        var target = UpdateDeleteTarget.TryReduce("delete from rental where return_date is null");

        Assert.NotNull(target);
        Assert.Equal("DELETE", target.Verb);
        Assert.Equal("rental", target.Relation);
        Assert.Equal("select * from rental where return_date is null", target.RowsSql);
    }

    /// <summary>
    /// The alias has to travel into the count, or a predicate that qualifies its columns cannot run at all.
    /// This is the case that turns a working count into a syntax error if the target is reduced to a bare
    /// relation name.
    /// </summary>
    [Theory]
    [InlineData("update payment p set amount = 1 where p.amount > 10", "select * from payment p where p.amount > 10")]
    [InlineData("update payment as p set amount = 1 where p.amount > 10", "select * from payment as p where p.amount > 10")]
    [InlineData("delete from payment p where p.amount > 10", "select * from payment p where p.amount > 10")]
    public void An_alias_survives_into_the_count(string statement, string expected)
    {
        var target = UpdateDeleteTarget.TryReduce(statement);

        Assert.NotNull(target);
        Assert.Equal(expected, target.RowsSql);
        Assert.Equal("payment", target.Relation);   // the alias is not part of what the prompt names
    }

    [Fact]
    public void Only_and_a_quoted_identifier_are_carried_through()
    {
        var target = UpdateDeleteTarget.TryReduce("""delete from only "odd Name" where id = 1""");

        Assert.NotNull(target);
        Assert.Equal("\"odd Name\"", target.Relation);
        Assert.Equal("""select * from only "odd Name" where id = 1""", target.RowsSql);
    }

    [Fact]
    public void An_unterminated_predicate_is_not_confused_with_having_none()
    {
        // The two look alike to a token scan and mean opposite things: `delete from t` deletes everything,
        // and `delete from t where` deletes nothing because it does not parse.
        Assert.True(UpdateDeleteTarget.TryReduce("delete from payment")!.EveryRow);
        Assert.Null(UpdateDeleteTarget.TryReduce("delete from payment where"));
    }

    [Fact]
    public void No_where_clause_means_every_row()
    {
        // #100's case: the confirmation says "every row", not a number, and not nothing.
        var target = UpdateDeleteTarget.TryReduce("delete from payment");

        Assert.NotNull(target);
        Assert.True(target.EveryRow);
        Assert.Null(target.Where);
        Assert.Equal("select * from payment", target.RowsSql);

        var update = UpdateDeleteTarget.TryReduce("update payment set amount = 0");
        Assert.NotNull(update);
        Assert.True(update.EveryRow);
    }

    [Fact]
    public void A_returning_clause_is_not_part_of_the_predicate()
    {
        // RETURNING changes what comes back, not which rows match — so it must be cut, and its presence
        // must not make the statement unreducible.
        var target = UpdateDeleteTarget.TryReduce("delete from payment where amount < 1 returning payment_id");

        Assert.NotNull(target);
        Assert.Equal("amount < 1", target.Where);
    }

    [Fact]
    public void A_trailing_semicolon_is_not_part_of_the_predicate()
    {
        var target = UpdateDeleteTarget.TryReduce("delete from payment where amount < 1;");

        Assert.NotNull(target);
        Assert.Equal("amount < 1", target.Where);
    }

    [Fact]
    public void A_subquery_in_the_predicate_is_kept_whole()
    {
        // Counting this is exactly as correct as running it, and the parenthesised FROM inside must not be
        // mistaken for a top-level one.
        var target = UpdateDeleteTarget.TryReduce(
            "delete from payment where customer_id in (select customer_id from customer where active = 0)");

        Assert.NotNull(target);
        Assert.Equal("customer_id in (select customer_id from customer where active = 0)", target.Where);
    }

    [Fact]
    public void A_set_list_containing_a_subquery_is_skipped_over()
    {
        var target = UpdateDeleteTarget.TryReduce(
            "update payment set amount = (select max(amount) from payment) where payment_id = 5");

        Assert.NotNull(target);
        Assert.Equal("payment_id = 5", target.Where);
    }

    [Fact]
    public void A_case_expression_in_the_set_list_does_not_confuse_the_scan()
    {
        var target = UpdateDeleteTarget.TryReduce(
            "update payment set amount = case when amount > 5 then 5 else amount end where staff_id = 2");

        Assert.NotNull(target);
        Assert.Equal("staff_id = 2", target.Where);
    }

    [Fact]
    public void Comments_and_line_breaks_are_preserved_in_the_predicate()
    {
        var target = UpdateDeleteTarget.TryReduce("""
            update payment
               set amount = 1
             where amount > 10   -- only the expensive ones
            """);

        Assert.NotNull(target);
        Assert.Contains("amount > 10", target.Where);
    }

    // ---- what refuses ---------------------------------------------------------------------------------

    [Theory]
    // Multi-table: which rows match is no longer a property of the target alone.
    [InlineData("update payment p set amount = c.balance from customer c where p.customer_id = c.customer_id")]
    [InlineData("delete from payment p using customer c where p.customer_id = c.customer_id")]
    // Not an UPDATE/DELETE at all, or one hidden behind a preamble the count would have to execute.
    [InlineData("select * from payment where amount > 10")]
    [InlineData("with gone as (delete from payment where amount < 1 returning *) select count(*) from gone")]
    [InlineData("explain analyze delete from payment where amount < 1")]
    [InlineData("truncate payment")]
    [InlineData("insert into payment (amount) values (1)")]
    // A placeholder the count cannot bind. Counting with it dropped or defaulted would report a different
    // predicate's rows than the statement runs.
    [InlineData("delete from payment where payment_id = $1")]
    [InlineData("update payment set amount = $1 where payment_id = $2")]
    // Several relations in the target.
    [InlineData("delete from payment, rental where false")]
    // A WHERE keyword with nothing after it. Not "every row" — a statement that will not run at all, and
    // saying "this will delete every row in t" about it states a fact it does not have.
    [InlineData("delete from payment where")]
    [InlineData("update payment set amount = 1 where")]
    [InlineData("delete from payment where returning payment_id")]
    // Nothing to reduce.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delete")]
    [InlineData("delete payment where id = 1")]
    // MySQL: a join in the target is the multi-table form; a LIMIT without a WHERE is not "every row"; and a
    // user variable is unset (NULL, not an error) on the connection the count runs on.
    [InlineData("update customer c join payment p on p.customer_id = c.customer_id set c.active = 1")]
    [InlineData("update customer c inner join payment p on p.customer_id = c.customer_id set c.active = 1 where p.amount > 5")]
    [InlineData("delete from payment limit 10")]
    [InlineData("delete from payment order by payment_date limit 10")]
    [InlineData("update payment set amount = 1 order by payment_id limit 1")]
    [InlineData("delete from payment where payment_id = @id")]
    [InlineData("update payment set amount = 1 where customer_id = @@session.last_insert_id")]
    [InlineData("delete from payment where payment_id = @'id'")]
    [InlineData("update low_priority payment set amount = 1")]
    [InlineData("update ignore payment set amount = 1 where payment_id = 1")]
    // A partition selection without a WHERE empties that partition, not the table.
    [InlineData("delete from payment partition (p0)")]
    [InlineData("update payment partition (p0) set amount = 1")]
    public void Anything_it_cannot_flatten_with_certainty_declines(string statement)
        => Assert.Null(UpdateDeleteTarget.TryReduce(statement));

    /// <summary>
    /// A statement whose target is a relation *named* like a keyword still reduces — the lexer distinguishes
    /// a quoted identifier from the keyword, which a regex could not.
    /// </summary>
    [Fact]
    public void A_relation_named_like_a_keyword_is_not_mistaken_for_one()
    {
        var target = UpdateDeleteTarget.TryReduce("""update "set" set x = 1 where "where" = 2""");

        Assert.NotNull(target);
        Assert.Equal("\"set\"", target.Relation);
        Assert.Equal("\"where\" = 2", target.Where);
    }

    /// <summary>
    /// A dollar-quoted body can contain the word <c>where</c>; only the lexer knows it is a string. This is
    /// the shape §1.3 cites for the redactor, and it applies here for the same reason.
    /// </summary>
    [Fact]
    public void A_dollar_quoted_literal_is_not_read_as_a_clause()
    {
        var target = UpdateDeleteTarget.TryReduce(
            "update note set body = $tag$ where from using $tag$ where id = 3");

        Assert.NotNull(target);
        Assert.Equal("id = 3", target.Where);
    }

    /// <summary>
    /// T-SQL's row-limited write. The reduction is dialect-blind (it lexes with <c>PgParsing</c>), and
    /// <c>update top (10) Orders set …</c> reduced with <c>top</c> as the relation: the counted form built a
    /// query the server rejected, and the no-WHERE form reported "every row in top" without asking anyone
    /// — a wrong relation and a wrong row set, in the confirmation §1.5 exists to make trustworthy.
    /// A TOP has no honest count in any case: its predicate does not decide the row set on its own.
    /// </summary>
    [Theory]
    [InlineData("update top (10) Orders set Freight = 1 where OrderId = 5")]
    [InlineData("update top (10) Orders set Freight = 1")]
    [InlineData("update TOP (5) PERCENT Orders set Freight = 1 where OrderId = 5")]
    [InlineData("delete top (5) from Orders where OrderId = 5")]
    public void A_tsql_top_clause_declines(string statement)
        => Assert.Null(UpdateDeleteTarget.TryReduce(statement));

    // A partition selection declines (above) only where it selects: PARTITION is not reserved in Postgres, and a
    // column of that name in the SET list must keep #100's "every row".
    [Theory]
    [InlineData("update payment set partition = 1")]
    [InlineData("update payment set amount = 1, partition = 'p0'")]
    public void A_column_called_partition_is_still_every_row(string statement)
        => Assert.True(UpdateDeleteTarget.TryReduce(statement) is { EveryRow: true, Relation: "payment" });

    // A variable declines (above); the operators Postgres spells with an @ are not variables.
    [Theory]
    [InlineData("delete from doc where body @> '{\"a\": 1}'")]
    [InlineData("update doc set seen = true where tsv @@ to_tsquery('x')")]
    public void Postgres_operators_that_start_with_an_at_sign_still_reduce(string statement)
        => Assert.NotNull(UpdateDeleteTarget.TryReduce(statement));

    /// <summary>
    /// And the decline is the <c>TOP (</c> shape, not the word: a relation actually named <c>top</c> still
    /// reduces, because it is followed by its <c>SET</c> rather than by a row count.
    /// </summary>
    [Fact]
    public void A_relation_named_top_still_reduces()
    {
        var target = UpdateDeleteTarget.TryReduce("update top set x = 1 where id = 2");

        Assert.NotNull(target);
        Assert.Equal("top", target.Relation);
        Assert.Equal("id = 2", target.Where);
    }

    // MySQL reads these differently from the Postgres lexer the reduction uses: `#` starts a comment, so the
    // WHERE below is not there and the statement deletes every row — counting `id = 1` would be a reassuring
    // number about it. An executable comment runs; `--` without a space is not a comment; a backslash moves
    // where a literal ends.
    [Theory]
    [InlineData("delete from payment # where payment_id = 1")]
    [InlineData("update payment set amount = 0 # where payment_id = 1")]
    [InlineData("delete from payment where 1 = 1 /*!50000 and payment_id = 1*/")]
    [InlineData("delete from payment where payment_id = 1 --1")]
    [InlineData("delete from payment where note = 'it\\'s'")]
    // A span the splitter kept together (it read a block): a count of the first statement would be about part
    // of what runs — "every row in payment" for a span that also deletes from another table.
    [InlineData("update payment set amount = 1; delete from rental;")]
    [InlineData("delete from payment where payment_id = 1; drop table rental")]
    // `$x$` is a name to MySQL and the start of one string to Postgres, which would hide the comma (and the
    // WHERE) of a multi-table UPDATE and report it as "every row in payment".
    [InlineData("update payment $x$, rental $x$y set $x$.amount = 0 where $x$.rental_id = $x$y.rental_id")]
    [InlineData("update payment $x$ join rental $x$y on $x$.rental_id = $x$y.rental_id set $x$.amount = 0")]
    public void A_mysql_statement_the_postgres_lexer_misreads_declines(string statement)
        => Assert.Null(UpdateDeleteTarget.TryReduce(MySqlDialect.Instance, statement));

    [Theory]
    [InlineData("delete from payment where payment_id = 1 -- tidy", "payment", "payment_id = 1")]
    [InlineData("update payment p set p.amount = 0 where p.payment_id = 'a#b'", "payment", "p.payment_id = 'a#b'")]
    public void A_mysql_statement_both_lexers_read_alike_still_reduces(string statement, string relation, string where)
    {
        var target = UpdateDeleteTarget.TryReduce(MySqlDialect.Instance, statement);
        Assert.NotNull(target);
        Assert.Equal(relation, target.Relation);
        Assert.Equal(where, target.Where);
    }
}
