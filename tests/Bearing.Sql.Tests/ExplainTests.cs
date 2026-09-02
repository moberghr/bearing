using System.Linq;
using Bearing.Sql;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Building the EXPLAIN and reading the plan back. Both pure, so the interesting cases — a rollback that
/// would not have rolled back, a plan from a Postgres that reports a field we do not know — are checkable
/// without a server.
/// </summary>
public class ExplainTests
{
    // ---- what gets sent ---------------------------------------------------------------------------

    [Fact]
    public void A_plan_only_explain_runs_nothing_and_needs_no_transaction()
    {
        var request = ExplainSql.Plan("select * from film");

        Assert.Contains("EXPLAIN (FORMAT JSON) select * from film", request.Sql);
        Assert.False(request.Analyzed);
        Assert.False(request.RolledBack);
        Assert.DoesNotContain("BEGIN", request.Sql);
    }

    [Fact]
    public void A_measured_explain_always_rolls_back()
    {
        // ANALYZE runs the statement. Wrapping only the obvious writes would miss a SELECT that calls a
        // volatile function, and that function's INSERT would commit.
        var request = ExplainSql.Measured("select * from film");

        Assert.StartsWith("BEGIN;", request.Sql);
        Assert.EndsWith("ROLLBACK;", request.Sql);
        Assert.Contains("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)", request.Sql);
        Assert.True(request.Analyzed);
        Assert.True(request.RolledBack);
    }

    [Fact]
    public void A_write_is_measured_inside_the_rollback_too()
    {
        var request = ExplainSql.Measured("update film set title = 'x' where id = 1");

        Assert.StartsWith("BEGIN;", request.Sql);
        Assert.EndsWith("ROLLBACK;", request.Sql);
        // The statement sits between them, not after the rollback.
        var explain = request.Sql.IndexOf("EXPLAIN", System.StringComparison.Ordinal);
        var rollback = request.Sql.IndexOf("ROLLBACK", System.StringComparison.Ordinal);
        Assert.True(explain < rollback, "the statement is outside the transaction");
    }

    [Theory]
    [InlineData("select 1;")]
    [InlineData("select 1 ;")]
    [InlineData("select 1;;")]
    [InlineData("select 1;  ")]
    public void A_trailing_semicolon_is_removed(string statement)
    {
        // Left in place it ends the EXPLAIN early, and ROLLBACK becomes a statement of its own — which
        // parses, runs, and yields a plan that was never inside a transaction at all.
        var request = ExplainSql.Measured(statement);

        var explainLine = request.Sql.Split('\n')[1];
        Assert.EndsWith("select 1;", explainLine);
        Assert.DoesNotContain(";;", explainLine);
    }

    // ---- reading the plan back --------------------------------------------------------------------

    /// <summary>A two-node analysed plan in Postgres' own shape, including fields this parser ignores.</summary>
    private const string AnalyzedJson = """
        [
          {
            "Plan": {
              "Node Type": "Aggregate",
              "Strategy": "Plain",
              "Parallel Aware": false,
              "Total Cost": 78.32,
              "Plan Rows": 1,
              "Actual Rows": 1,
              "Actual Total Time": 12.5,
              "Actual Loops": 1,
              "Shared Read Blocks": 4,
              "Plans": [
                {
                  "Node Type": "Seq Scan",
                  "Relation Name": "film",
                  "Alias": "f",
                  "Filter": "(length > 100)",
                  "Total Cost": 65.0,
                  "Plan Rows": 200,
                  "Actual Rows": 1000,
                  "Actual Total Time": 11.0,
                  "Actual Loops": 1,
                  "Shared Read Blocks": 3
                }
              ]
            },
            "Planning Time": 0.31,
            "Execution Time": 12.7,
            "Triggers": []
          }
        ]
        """;

    [Fact]
    public void A_plan_is_read_into_a_tree()
    {
        var plan = ExplainPlanParser.Parse(AnalyzedJson, analyzed: true, rolledBack: true);

        Assert.NotNull(plan);
        Assert.Equal("Aggregate", plan!.Root.NodeType);
        Assert.Equal(0.31, plan.PlanningMs);
        Assert.Equal(12.7, plan.ExecutionMs);
        Assert.True(plan.Analyzed);
        Assert.True(plan.RolledBack);

        var scan = Assert.Single(plan.Root.Children);
        Assert.Equal("Seq Scan", scan.NodeType);
        Assert.Equal("film", scan.Relation);
        Assert.Equal("(length > 100)", scan.Filter);
        Assert.Equal("Seq Scan on film", scan.Title);
    }

    [Fact]
    public void Self_time_excludes_the_children()
    {
        // Postgres reports inclusive time, so the root is always the biggest number and ordering by it says
        // only "the query took as long as the query". Self time is what points at the node to fix.
        var plan = ExplainPlanParser.Parse(AnalyzedJson, analyzed: true)!;

        Assert.Equal(1.5, plan.Root.SelfMs!.Value, 3);   // 12.5 total − 11.0 in the scan
        Assert.Equal(11.0, plan.Root.Children[0].SelfMs!.Value, 3);
    }

    [Fact]
    public void The_worst_node_is_the_one_with_the_most_self_time()
    {
        var plan = ExplainPlanParser.Parse(AnalyzedJson, analyzed: true)!;

        Assert.Equal("Seq Scan", plan.Hotspots()[0].NodeType);
    }

    [Fact]
    public void The_estimate_error_is_a_factor_either_way_round()
    {
        // 200 estimated, 1000 actual → 5x out. The direction does not matter to the reader; the size does.
        var plan = ExplainPlanParser.Parse(AnalyzedJson, analyzed: true)!;

        Assert.Equal(5, plan.Root.Children[0].EstimateErrorFactor!.Value, 3);
    }

    [Fact]
    public void A_plan_only_explain_has_no_timings()
    {
        const string json = """
            [{"Plan": {"Node Type": "Seq Scan", "Relation Name": "film", "Total Cost": 65.0, "Plan Rows": 200}}]
            """;

        var plan = ExplainPlanParser.Parse(json);

        Assert.NotNull(plan);
        Assert.Null(plan!.Root.ActualMs);
        Assert.Null(plan.Root.SelfMs);
        Assert.Null(plan.Root.EstimateErrorFactor);
        Assert.False(plan.Analyzed);
        // Ordering still works without timings, falling back to the planner's cost.
        Assert.Equal("Seq Scan", plan.Hotspots()[0].NodeType);
    }

    [Fact]
    public void Actuals_are_per_loop_so_the_error_factor_multiplies_them()
    {
        // A node inside a nested loop reports rows *per loop*, and the estimate is for the whole node.
        // Comparing them raw makes every inner node look wildly under-estimated.
        const string json = """
            [{"Plan": {"Node Type": "Index Scan", "Plan Rows": 100,
                       "Actual Rows": 10, "Actual Loops": 10, "Actual Total Time": 1.0}}]
            """;

        var plan = ExplainPlanParser.Parse(json, analyzed: true)!;

        // 10 rows x 10 loops = 100 actual against 100 estimated: a perfect estimate, not a 10x miss.
        Assert.Equal(1, plan.Root.EstimateErrorFactor!.Value, 3);
    }

    [Fact]
    public void Parallel_arithmetic_never_yields_a_negative_self_time()
    {
        // Children can sum past the parent's total on a parallel plan. That is noise in the numbers, not a
        // node that took less than no time.
        const string json = """
            [{"Plan": {"Node Type": "Gather", "Actual Total Time": 5.0, "Plans": [
                {"Node Type": "Parallel Seq Scan", "Actual Total Time": 9.0}]}}]
            """;

        var plan = ExplainPlanParser.Parse(json, analyzed: true)!;

        Assert.Equal(0, plan.Root.SelfMs!.Value);
    }

    [Fact]
    public void Anything_that_is_not_a_plan_is_null_rather_than_a_throw()
    {
        // The caller has a string from a database: a failed EXPLAIN, an error rendered as text, an empty
        // result. The UI can show the raw text, which is more use than an exception.
        Assert.Null(ExplainPlanParser.Parse(null));
        Assert.Null(ExplainPlanParser.Parse(""));
        Assert.Null(ExplainPlanParser.Parse("ERROR: permission denied for table film"));
        Assert.Null(ExplainPlanParser.Parse("[]"));
        Assert.Null(ExplainPlanParser.Parse("{}"));
        Assert.Null(ExplainPlanParser.Parse("[{\"NotAPlan\": 1}]"));
    }

    [Fact]
    public void An_unknown_field_does_not_break_the_parse()
    {
        // The field set grows with every Postgres release. A plan carrying something new has to still read.
        const string json = """
            [{"Plan": {"Node Type": "Seq Scan", "Something New In 19": {"nested": true},
                       "Total Cost": 1.0}}]
            """;

        var plan = ExplainPlanParser.Parse(json);

        Assert.Equal("Seq Scan", plan!.Root.NodeType);
    }

    [Fact]
    public void A_deep_plan_flattens_in_document_order()
    {
        const string json = """
            [{"Plan": {"Node Type": "A", "Plans": [
                {"Node Type": "B", "Plans": [{"Node Type": "C"}]},
                {"Node Type": "D"}]}}]
            """;

        var plan = ExplainPlanParser.Parse(json)!;

        Assert.Equal(["A", "B", "C", "D"], plan.Root.Flatten().Select(n => n.NodeType).ToArray());
    }
}
