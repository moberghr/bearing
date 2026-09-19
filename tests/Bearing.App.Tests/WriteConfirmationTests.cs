using System;
using System.Linq;
using Bearing.App.Services;
using Bearing.Core.Data;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The write confirmation carries the statements a write is about to run, plus the text the dialog shows.
/// All of it is derived, so it is testable without a window — which is the only way to cover it at all
/// (§4.3: the dialog itself can't be driven headlessly).
/// </summary>
public class WriteConfirmationTests
{
    private static ConnectionInfo Conn(bool guarded = false, string env = "Production") => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod-eu",
        ProviderId = "postgres",
        Environment = env,
        RequireWriteConfirmation = guarded,
    };

    [Fact]
    public void Batch_lists_every_statement_and_marks_only_the_writes()
    {
        var confirmation = WriteConfirmation.ForBatch(Conn(),
            WriteGuard.Describe("select 1 from film; delete from rental where id = 3;"));

        Assert.Equal(2, confirmation.Statements.Count);
        Assert.Equal(new[] { "SELECT", "DELETE" }, confirmation.Statements.Select(s => s.Kind));
        Assert.Equal(new[] { false, true }, confirmation.Statements.Select(s => s.IsRisky));
        Assert.Equal(new[] { "DELETE" }, confirmation.Verbs);
        Assert.Equal(1, confirmation.RiskyCount);
    }

    [Fact]
    public void Batch_summary_says_how_much_of_it_only_reads()
    {
        var mixed = WriteConfirmation.ForBatch(Conn(),
            WriteGuard.Describe("select 1 from film; delete from rental where id = 3;"));
        Assert.Equal("1 of the 2 statements below will modify data or schema (DELETE); the rest only read.",
            mixed.Summary);

        var allWrites = WriteConfirmation.ForBatch(Conn(),
            WriteGuard.Describe("delete from rental; drop table snapshot;"));
        Assert.Equal("2 statements below will modify data or schema (DELETE, DROP).", allWrites.Summary);
    }

    [Fact]
    public void Batch_heading_names_the_connection_and_its_environment()
    {
        Assert.Equal("Run on prod-eu · Production?",
            WriteConfirmation.ForBatch(Conn(), WriteGuard.Describe("delete from rental;")).Heading);
        Assert.Equal("Run on prod-eu?",
            WriteConfirmation.ForBatch(Conn(env: ""), WriteGuard.Describe("delete from rental;")).Heading);
    }

    [Fact]
    public void Guarded_connection_adds_a_warning_line_and_an_unguarded_one_does_not()
    {
        var statements = new[] { new WriteStatement("UPDATE", "update film set x = 1;", IsRisky: true) };

        Assert.Contains("requiring confirmation", WriteConfirmation.ForEdits(Conn(guarded: true), statements).Warning);
        Assert.Null(WriteConfirmation.ForEdits(Conn(), statements).Warning);
    }

    [Fact]
    public void Save_confirmation_promises_one_transaction_and_a_save_button()
    {
        var confirmation = WriteConfirmation.ForEdits(Conn(), new[]
        {
            new WriteStatement("DELETE", "delete from public.orders where id = 9;", IsRisky: true),
            new WriteStatement("UPDATE", "update public.orders set qty = 5 where id = 1;", IsRisky: true),
        });

        Assert.Equal("Save 2 changes to prod-eu · Production?", confirmation.Heading);
        Assert.Equal("2 statements run as one transaction — if any of them fails, none of the changes are committed.",
            confirmation.Summary);
        Assert.Equal(new[] { "DELETE", "UPDATE" }, confirmation.Verbs);
        Assert.Equal("✓ Save", confirmation.ConfirmLabel);
        Assert.Equal("Confirm save", confirmation.Title);
    }

    [Fact]
    public void One_change_reads_in_the_singular()
    {
        var confirmation = WriteConfirmation.ForEdits(Conn(),
            new[] { new WriteStatement("UPDATE", "update public.orders set qty = 5 where id = 1;", IsRisky: true) });

        Assert.Equal("Save 1 change to prod-eu · Production?", confirmation.Heading);
        Assert.StartsWith("1 statement runs as one transaction", confirmation.Summary);
    }

    [Fact]
    public void Copyable_script_terminates_statements_the_user_left_unterminated()
    {
        // Blank-line-separated statements carry no semicolon (StatementSplitter allows the convention), so
        // pasting the copied script elsewhere would otherwise fuse them into one statement.
        var confirmation = WriteConfirmation.ForBatch(Conn(),
            WriteGuard.Describe("delete from rental\n\ndelete from film"));

        Assert.Equal("delete from rental;\ndelete from film;", confirmation.Script);
    }

    // ---- A guard that cannot read the dialect --------------------------------------------------------

    /// <summary>A batch as the guard reports it when it cannot read the engine: every statement risky, no
    /// verb found, and the flag saying why. Built by hand rather than through SqlServerDialect so the
    /// confirmation is tested on its own terms.</summary>
    private static StatementRisk[] Unparsed(params string[] statements)
        => statements
            .Select(s => new StatementRisk(s, s.Split(' ')[0].ToUpperInvariant(), Array.Empty<string>(),
                GuardIsDialectAware: false))
            .ToArray();

    [Fact]
    public void An_unparsed_dialect_confirms_every_statement_but_claims_nothing_about_them()
    {
        var confirmation = WriteConfirmation.ForBatch(Conn(guarded: true),
            Unparsed("select * from Orders", "select 1"));

        // Fail safe: both are confirmed (§1.2).
        Assert.Equal(2, confirmation.RiskyCount);
        Assert.False(confirmation.GuardIsDialectAware);

        // ...but the wording must not say two SELECTs modify data. That sentence is what teaches a user to
        // click through the guard, and the next prompt they click through will be a real DROP.
        Assert.DoesNotContain("modify data", confirmation.Summary);
        Assert.Equal("2 statements below will run on prod-eu · Production. "
                   + "None of them was recognised as a write.", confirmation.Summary);
        Assert.Contains("does not parse this engine's SQL yet", confirmation.GuardNote);
    }

    [Fact]
    public void An_unparsed_dialect_still_names_the_writes_it_did_recognise()
    {
        var statements = new[]
        {
            new StatementRisk("select 1", "SELECT", Array.Empty<string>(), GuardIsDialectAware: false),
            new StatementRisk("delete from Orders", "DELETE", new[] { "DELETE" }, GuardIsDialectAware: false),
        };

        var confirmation = WriteConfirmation.ForBatch(Conn(guarded: true), statements);

        Assert.Contains("Recognised as writes: DELETE.", confirmation.Summary);
    }

    [Fact]
    public void A_parsed_dialect_carries_no_guard_note()
    {
        // The Postgres path is untouched: the note exists only to explain a confirmation nobody could
        // otherwise account for.
        Assert.Null(WriteConfirmation.ForBatch(Conn(), WriteGuard.Describe("delete from rental;")).GuardNote);
        Assert.True(WriteConfirmation.ForBatch(Conn(), WriteGuard.Describe("select 1")).GuardIsDialectAware);
    }

    [Fact]
    public void One_unreadable_statement_makes_the_whole_batch_unreadable()
    {
        // A mixed batch cannot happen through one dialect, but the verdict has to be conservative if it
        // ever does: a batch is only as trustworthy as its least-understood statement.
        var mixed = new[]
        {
            new StatementRisk("select 1", "SELECT", Array.Empty<string>()),
            new StatementRisk("exec sp_x", "EXEC", Array.Empty<string>(), GuardIsDialectAware: false),
        };

        Assert.False(WriteConfirmation.ForBatch(Conn(), mixed).GuardIsDialectAware);
    }

    [Fact]
    public void An_empty_batch_has_nothing_to_be_unsure_about()
        => Assert.True(WriteConfirmation.ForBatch(Conn(), Array.Empty<StatementRisk>()).GuardIsDialectAware);

    // ---- row impact (#112 / #100) ---------------------------------------------------------------------

    private static WriteConfirmation WithImpacts(string sql, params (int Index, RowImpact Impact)[] impacts)
        => WriteConfirmation.ForBatch(Conn(), WriteGuard.Describe(sql),
            impacts.ToDictionary(x => x.Index, x => x.Impact));

    [Fact]
    public void A_counted_write_says_how_many_rows_and_where()
    {
        var target = UpdateDeleteTarget.TryReduce("update payment set amount = 1 where amount > 10")!;
        var confirmation = WithImpacts("update payment set amount = 1 where amount > 10;",
            (0, RowImpact.Counted(target, 3412)));

        var impact = Assert.Single(confirmation.Impacts);
        // Grouped, and grouped invariantly — "3.412 rows" and "3,412 rows" are prompts about different
        // numbers, and the plan window already settled this for the same reason.
        Assert.Equal("This will update 3,412 rows in payment.", impact.Text);
        Assert.False(impact.IsAlarming);
    }

    [Fact]
    public void One_row_is_not_pluralised()
    {
        var target = UpdateDeleteTarget.TryReduce("delete from payment where payment_id = 7")!;
        Assert.Equal("This will delete 1 row in payment.", RowImpact.Counted(target, 1).Text);
    }

    [Fact]
    public void Zero_rows_is_a_real_answer()
    {
        // The wrong-tab mistake has a mirror: a statement you expected to hit something and that matches
        // nothing. Reporting that as no number at all would hide it.
        var target = UpdateDeleteTarget.TryReduce("delete from payment where payment_id = 7")!;
        var impact = RowImpact.Counted(target, 0);
        Assert.Equal("This will delete 0 rows in payment.", impact.Text);
        Assert.False(impact.IsAlarming);
    }

    [Fact]
    public void A_statement_with_no_where_clause_says_every_row_and_says_it_loudly()
    {
        // #100. Not "0 rows", not silence — the one case where the absence of a predicate *is* the finding.
        var target = UpdateDeleteTarget.TryReduce("delete from payment")!;
        var impact = RowImpact.Every(target);

        Assert.Equal("This will delete every row in payment.", impact.Text);
        Assert.True(impact.IsAlarming);
    }

    [Fact]
    public void A_count_that_could_not_be_taken_says_so_rather_than_showing_nothing()
    {
        // "No number" and "zero" must not look the same, and a slow count must not be indistinguishable
        // from a statement the reducer declined.
        var target = UpdateDeleteTarget.TryReduce("delete from payment where amount > 1")!;
        var impact = RowImpact.Uncounted(target);

        Assert.Equal("Could not count the rows this will delete in payment in time.", impact.Text);
        Assert.True(impact.IsAlarming);
    }

    [Fact]
    public void A_batch_with_no_counts_reads_exactly_as_it_did_before()
    {
        var confirmation = WriteConfirmation.ForBatch(Conn(), WriteGuard.Describe("drop table snapshot;"));

        Assert.Empty(confirmation.Impacts);
        Assert.Null(confirmation.ImpactCaveat);
        Assert.All(confirmation.Statements, s => Assert.Null(s.Impact));
    }

    [Fact]
    public void Several_writes_carry_the_caveat_that_the_counts_predate_the_batch()
    {
        // Counted before anything ran, so a statement that a previous one feeds is counted against rows that
        // will no longer be there. Saying so is cheaper than pretending the numbers are independent.
        var first = UpdateDeleteTarget.TryReduce("delete from payment where amount < 1")!;
        var second = UpdateDeleteTarget.TryReduce("delete from rental where return_date is null")!;
        var confirmation = WithImpacts(
            "delete from payment where amount < 1; delete from rental where return_date is null;",
            (0, RowImpact.Counted(first, 12)), (1, RowImpact.Counted(second, 40)));

        Assert.Equal(2, confirmation.Impacts.Count);
        Assert.Equal("Counted before the batch runs — an earlier statement can change what a later one matches.",
            confirmation.ImpactCaveat);
    }

    [Fact]
    public void A_single_write_needs_no_such_caveat()
    {
        var target = UpdateDeleteTarget.TryReduce("delete from payment where amount < 1")!;
        var confirmation = WithImpacts("select 1; delete from payment where amount < 1;",
            (1, RowImpact.Counted(target, 12)));

        Assert.Equal(1, confirmation.RiskyCount);
        Assert.Null(confirmation.ImpactCaveat);
    }

    [Fact]
    public void An_impact_is_attached_to_the_statement_it_describes()
    {
        // Keyed by index, so a batch that mixes reads and writes cannot hang a count on the wrong line.
        var target = UpdateDeleteTarget.TryReduce("delete from payment where amount < 1")!;
        var confirmation = WithImpacts("select 1 from film; delete from payment where amount < 1;",
            (1, RowImpact.Counted(target, 12)));

        Assert.Null(confirmation.Statements[0].Impact);
        Assert.NotNull(confirmation.Statements[1].Impact);
    }
}
