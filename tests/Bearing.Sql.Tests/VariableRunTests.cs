using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>Planning and binding a run's <c>:name</c> variables (#103) — which <c>@set</c> governs each reference,
/// what is still asked for, and what reaches the server versus what the log keeps.</summary>
public class VariableRunTests
{
    private static readonly ISqlDialect Pg = PostgresDialect.Instance;

    [Fact]
    public void A_set_line_governs_only_what_is_below_it()
    {
        var plan = VariableRun.Plan(Pg, "select :id;\n@set id = 2\nselect :id;");

        Assert.Equal([VariableSource.Prompt, VariableSource.Script], plan.Slots.Select(s => s.Source));
        Assert.Equal(["bearing_id", "bearing_id__2"], plan.Slots.Select(s => s.Placeholder));
        Assert.True(plan.HasMissing);

        var run = VariableRun.Bind(Pg, plan, ["1", "2"]);
        Assert.Equal("select @bearing_id;\n" + new string(' ', 11) + "\nselect @bearing_id__2;", run.Sql);
        Assert.Equal(["1", "2"], run.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void Inside_the_run_the_nearest_line_above_wins_and_above_the_run_the_last_one_does()
    {
        var plan = VariableRun.Plan(Pg,
            "select :id;\n@set id = 7\nselect :id;\n@set id = 8\nselect :id, :id",
            preamble: "@set id = 1\nselect 0;\n@set id = 42\n");

        Assert.Equal([("42", 3), ("7", 5), ("8", 7)], plan.Slots.Select(s => (s.Value, s.Line!.Value)));
        Assert.Equal([VariableSource.Above, VariableSource.Script, VariableSource.Script], plan.Slots.Select(s => s.Source));
        Assert.Equal([0, 1, 2, 2], plan.Uses.Select(u => u.Slot));
        Assert.False(plan.HasMissing);
    }

    [Fact]
    public void Binding_sends_placeholders_with_values_and_logs_the_text_as_written()
    {
        var plan = VariableRun.Plan(Pg, "@set id = 42\nselect * from t where id = :id and name = :name");

        var run = VariableRun.Bind(Pg, plan, ["42", "'O''Brien'"]);

        Assert.Equal("select * from t where id = @bearing_id and name = @bearing_name", run.Sql.Trim());
        Assert.Equal(
            [new QueryParameter("bearing_id", "42", QueryParameterKind.Number),
             new QueryParameter("bearing_name", "O'Brien", QueryParameterKind.Text)],
            run.Parameters);
        // The run's own @set line stays in the logged text; only the value from outside it is logged beside.
        Assert.Equal("@set id = 42\nselect * from t where id = :id and name = :name", run.LogSql);
        Assert.Equal(["name = O'Brien"], run.LogParameters.Select(p => $"{p.Name} = {p.Value}"));
    }

    [Fact]
    public void An_override_is_what_runs_and_what_the_logged_set_line_says()
    {
        var plan = VariableRun.Plan(Pg, "  @set id = 42\nselect :id");

        var run = VariableRun.Bind(Pg, plan, ["7"]);

        Assert.Equal("7", Assert.Single(run.Parameters).Value);
        Assert.Equal("@set id = 7\nselect :id", run.LogSql);
    }

    [Fact]
    public void Presets_bind_without_texts()
    {
        var plan = VariableRun.Plan(Pg, "select :id", preamble: "@set id = 5\n");

        Assert.Equal("5", Assert.Single(VariableRun.Bind(Pg, plan).Parameters).Value);
    }

    [Fact]
    public void A_value_written_as_a_statement_is_still_one_value()
    {
        var run = VariableRun.Bind(Pg, VariableRun.Plan(Pg, "select * from t where id = :id"), ["1; delete from t"]);

        Assert.Equal("select * from t where id = @bearing_id", run.Sql);
        Assert.Equal("1; delete from t", Assert.Single(run.Parameters).Value);
    }

    [Fact]
    public void The_quoted_form_binds_as_text_even_when_it_looks_like_a_number()
    {
        var plan = VariableRun.Plan(Pg, "@set zip = 10115\nselect * from a where zip = :'zip'");

        Assert.Equal(QueryParameterKind.Text, Assert.Single(VariableRun.Bind(Pg, plan).Parameters).Kind);
    }

    [Fact]
    public void A_set_line_nothing_uses_is_dropped_from_what_runs_and_kept_in_the_log()
    {
        var run = VariableRun.Bind(Pg, VariableRun.Plan(Pg, "@set id = 1\nselect 1"));

        Assert.Empty(run.Parameters);
        Assert.DoesNotContain("@set", run.Sql);
        Assert.Equal("@set id = 1\nselect 1", run.LogSql);
    }

    [Fact]
    public void A_set_line_with_no_value_is_removed_but_gives_nothing()
    {
        var plan = VariableRun.Plan(Pg, "@set id =\nselect :id", preamble: "@set id = 5\n");

        // It governs nothing, so the line above the run still does — and nothing of it reaches the server.
        Assert.Equal(("5", VariableSource.Above), (plan.Slots[0].Value, plan.Slots[0].Source));
        Assert.DoesNotContain("@set", VariableRun.Bind(Pg, plan).Sql);
    }

    [Fact]
    public void Blank_answers_are_missing_values_and_binding_them_is_refused()
    {
        var plan = VariableRun.Plan(Pg, "select :a, :b", preamble: "@set b = 2\n");

        Assert.Equal("a", VariableRun.FirstMissing(plan, null));
        Assert.Equal("a", VariableRun.FirstMissing(plan, ["  ", "2"]));
        Assert.Equal("b", VariableRun.FirstMissing(plan, ["''", ";"]));
        Assert.Null(VariableRun.FirstMissing(plan, ["''", "null"]));
        Assert.Throws<System.InvalidOperationException>(() => VariableRun.Bind(Pg, plan, ["", "2"]));
    }
}
