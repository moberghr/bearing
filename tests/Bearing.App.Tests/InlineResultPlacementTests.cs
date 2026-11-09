using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Where inline results land (docs/specs/2026-11-06-inline-results.md §2): under the text a Run ran; per
/// statement for Run All only when the provider proved the mapping; otherwise one batch block under the last
/// statement — never a positional guess.
/// </summary>
public class InlineResultPlacementTests
{
    private const string Buffer = "select 1;\n\nselect 2;\n\nselect 3;\n";

    private static ResultSetViewModel Set(int? statementIndex) => new(
        new QueryResult([new ColumnDescriptor("n", "int4", typeof(int))], [[1]], 1, TimeSpan.Zero, null, null, false)
        { StatementIndex = statementIndex },
        sourceSql: null, pageable: false);

    private static RunScope WholeBuffer(string text = Buffer)
        => InlineResultPlacement.Scope(PostgresDialect.Instance, text, 0, isWholeBuffer: true);

    [Fact]
    public void Scope_records_where_each_sent_statement_ends_in_the_buffer()
    {
        var scope = WholeBuffer();

        Assert.Equal([Buffer.IndexOf("select 1;") + 9, Buffer.IndexOf("select 2;") + 9, Buffer.IndexOf("select 3;") + 9],
            scope.StatementEnds);
        Assert.Equal(StatementSplitter.EnsureSeparated(PostgresDialect.Instance, Buffer), scope.Sql);
    }

    [Fact]
    public void A_scope_inside_the_buffer_is_offset_by_where_it_starts()
    {
        var start = Buffer.IndexOf("select 2;");
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", start, isWholeBuffer: false);

        Assert.Equal(start, scope.Start);
        Assert.Equal(start + 9, scope.End);
        Assert.Equal([start + 9], scope.StatementEnds);
    }

    /// <summary>EnsureSeparated drops a span that is only semicolons, so the server never numbers it; the
    /// statement after it must still be index 1, not 2.</summary>
    [Fact]
    public void A_span_that_is_not_sent_takes_no_statement_number()
    {
        const string text = "select 1;\n;\nselect 2;";
        var scope = WholeBuffer(text);

        Assert.Equal(2, scope.StatementEnds.Count);
        Assert.Equal(text.Length, scope.StatementEnds[1]);
    }

    /// <summary>The PostgreSQL split keeps an own-line comment followed by a blank line with the statement
    /// above it; the result still belongs under that statement, not under the comment heading the next one.</summary>
    [Fact]
    public void A_section_comment_after_a_statement_does_not_carry_its_result_down()
    {
        const string text = "select 1;\n\n-- header\n\nselect 2;";
        var all = WholeBuffer(text);
        Assert.Equal([9, text.Length], all.StatementEnds);

        var first = StatementSplitter.StatementAt(PostgresDialect.Instance, text, 3)!;
        var run = InlineResultPlacement.Scope(PostgresDialect.Instance, first.Text, first.Start, isWholeBuffer: false);
        Assert.Equal(9, run.End);
    }

    [Fact]
    public void T_sql_batches_end_on_their_own_lines()
    {
        const string text = "select 1;\nGO\nselect 2; -- trailing\nGO\n";
        var scope = InlineResultPlacement.Scope(SqlServerDialect.Instance, text, 0, isWholeBuffer: true);

        // Each end sits on its statement's line — the zone hangs below that line, above the GO.
        static int LineOf(string t, int offset) => t[..offset].Count(c => c == '\n');
        Assert.Equal(2, scope.StatementEnds.Count);
        Assert.Equal(0, LineOf(text, scope.StatementEnds[0]));
        Assert.Equal(2, LineOf(text, scope.StatementEnds[1]));
    }

    /// <summary>MySQL's <c>#</c> comments after the last statement are not code, so the result hangs under the
    /// statement — read with MySQL's scanner, to which <c>#</c> is a comment, not the PostgreSQL lexer's
    /// operator.</summary>
    [Fact]
    public void My_sql_trailing_hash_comments_do_not_carry_the_result_down()
    {
        const string text = "select 1;\nselect 2; # trailing\n# footer\n";
        var scope = InlineResultPlacement.Scope(MySqlDialect.Instance, text, 0, isWholeBuffer: true);

        // The MySQL split leaves the delimiter out of the span, so code ends just before each ';'.
        var second = text.IndexOf("select 2") + "select 2".Length;
        Assert.Equal(["select 1".Length, second], scope.StatementEnds);
        Assert.Equal(second, scope.End);
    }

    [Fact]
    public void A_run_puts_every_set_in_one_zone_at_the_end_of_what_it_ran()
    {
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1; select 2;  ", 10, isWholeBuffer: false);

        var zone = Assert.Single(InlineResultPlacement.Assign(scope, [Set(0), Set(1)]));
        Assert.Equal(10 + "select 1; select 2;".Length, zone.Offset);
        Assert.Equal(2, zone.Sets.Count);
        Assert.Equal("2 result sets", zone.Caption);
    }

    [Fact]
    public void A_single_set_has_no_caption()
    {
        var scope = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, isWholeBuffer: false);

        Assert.Null(Assert.Single(InlineResultPlacement.Assign(scope, [Set(null)])).Caption);
    }

    [Fact]
    public void Run_all_with_a_proven_mapping_gives_each_statement_its_own_zone()
    {
        var scope = WholeBuffer();

        var zones = InlineResultPlacement.Assign(scope, [Set(0), Set(1), Set(2)]);

        Assert.Equal(scope.StatementEnds, zones.Select(z => z.Offset));
        Assert.All(zones, z => Assert.Null(z.Caption));
    }

    /// <summary>One set without a number is enough: a partly-numbered batch is not a mapping (SQL Server
    /// never numbers; Postgres doesn't when a write sits among the selects).</summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0, null, 2)]
    public void Run_all_without_a_proven_mapping_puts_the_batch_under_the_last_statement(int? a, int? b, int? c)
    {
        var scope = WholeBuffer();

        var zone = Assert.Single(InlineResultPlacement.Assign(scope, [Set(a), Set(b), Set(c)]));

        Assert.Equal(scope.StatementEnds[^1], zone.Offset);
        Assert.Equal("3 result sets from this batch", zone.Caption);
    }

    [Fact]
    public void A_statement_number_beyond_our_split_is_not_trusted()
    {
        var scope = WholeBuffer();

        var zone = Assert.Single(InlineResultPlacement.Assign(scope, [Set(0), Set(7)]));

        Assert.Equal(scope.StatementEnds[^1], zone.Offset);
    }

    /// <summary>The provider numbered every set, but found fewer statements than our split did: index i may
    /// not be our statement i, so nothing is split.</summary>
    [Fact]
    public void A_mapping_whose_count_disagrees_with_our_split_is_not_trusted()
    {
        var scope = WholeBuffer();

        var zone = Assert.Single(InlineResultPlacement.Assign(scope, [Set(0), Set(1)]));

        Assert.Equal(scope.StatementEnds[^1], zone.Offset);
        Assert.Equal("2 result sets from this batch", zone.Caption);
    }

    [Fact]
    public void Several_sets_from_one_statement_share_its_zone()
    {
        var scope = WholeBuffer();

        var zones = InlineResultPlacement.Assign(scope, [Set(0), Set(2), Set(2)]);

        Assert.Equal(2, zones.Count);
        Assert.Equal(2, zones[1].Sets.Count);
        Assert.Equal(scope.StatementEnds[2], zones[1].Offset);
    }

    [Fact]
    public void No_sets_means_no_zones()
        => Assert.Empty(InlineResultPlacement.Assign(WholeBuffer(), Array.Empty<ResultSetViewModel>()));

    // ---- the tab's zone list ----------------------------------------------------------------

    /// <summary>With nothing between them, statement 1's zone sits exactly where statement 2 starts; running
    /// statement 2 must leave it.</summary>
    [Fact]
    public void Running_the_next_statement_keeps_the_zone_where_it_touches()
    {
        const string text = "select 1;select 2;";
        var tab = new EditorTabViewModel("t", text);
        var first = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, false);
        tab.ApplyInlineRun(first, InlineResultPlacement.Assign(first, [Set(null)]));
        var kept = Assert.Single(tab.InlineZones);

        var second = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", "select 1;".Length, false);
        Assert.Equal(kept.Offset, second.Start);   // the case under test
        tab.ApplyInlineRun(second, InlineResultPlacement.Assign(second, [Set(null)]));

        Assert.Equal(2, tab.InlineZones.Count);
        Assert.Same(kept, tab.InlineZones[0]);
    }

    [Fact]
    public void A_run_replaces_only_the_zones_inside_what_it_ran()
    {
        var tab = new EditorTabViewModel("t", Buffer);
        var all = WholeBuffer();
        tab.ApplyInlineRun(all, InlineResultPlacement.Assign(all, [Set(0), Set(1), Set(2)]));
        var before = tab.InlineZones.ToList();

        var second = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 2;", Buffer.IndexOf("select 2;"), false);
        tab.ApplyInlineRun(second, InlineResultPlacement.Assign(second, [Set(0)]));

        Assert.Equal(3, tab.InlineZones.Count);
        Assert.Same(before[0], tab.InlineZones[0]);
        Assert.NotSame(before[1], tab.InlineZones[1]);
        Assert.Same(before[2], tab.InlineZones[2]);
    }

    [Fact]
    public void Run_all_replaces_every_zone()
    {
        var tab = new EditorTabViewModel("t", Buffer);
        var one = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, false);
        tab.ApplyInlineRun(one, InlineResultPlacement.Assign(one, [Set(0)]));

        var all = WholeBuffer();
        tab.ApplyInlineRun(all, InlineResultPlacement.Assign(all, [Set(null)]));

        var zone = Assert.Single(tab.InlineZones);
        Assert.Equal(all.StatementEnds[^1], zone.Offset);
    }

    [Fact]
    public void Removing_a_zone_raises_the_change_once_and_only_if_it_was_there()
    {
        var tab = new EditorTabViewModel("t", Buffer);
        var one = InlineResultPlacement.Scope(PostgresDialect.Instance, "select 1;", 0, false);
        tab.ApplyInlineRun(one, InlineResultPlacement.Assign(one, [Set(0)]));
        var changes = 0;
        tab.InlineZonesChanged += () => changes++;
        var id = tab.InlineZones[0].Id;

        Assert.True(tab.RemoveInlineZone(id));
        Assert.False(tab.RemoveInlineZone(id));
        Assert.Equal(1, changes);
        Assert.Empty(tab.InlineZones);
    }
}
