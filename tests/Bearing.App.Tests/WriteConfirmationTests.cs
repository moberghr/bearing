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
}
