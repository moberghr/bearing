using System;
using System.Linq;
using System.Text;
using Bearing.App.Editing;
using Bearing.App.Results;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The smallest edits for a rewrite (<see cref="TextHunks"/>): applied to the old text they give the new one
/// exactly, and they leave unchanged text alone — so an anchor in it survives the edit, its undo and its redo.
/// </summary>
public class TextHunksTests
{
    private static string Apply(string before, string after)
    {
        var text = new StringBuilder(before);
        foreach (var hunk in TextHunks.Diff(before, after).Reverse())
        {
            text.Remove(hunk.Offset, hunk.Length).Insert(hunk.Offset, hunk.Text);
        }
        return text.ToString();
    }

    [Fact]
    public void Commenting_lines_only_inserts_the_markers()
    {
        const string before = "select 1;\nselect 2;\nselect 3;\n";
        const string after = "select 1;\n-- select 2;\n-- select 3;\n";

        var hunks = TextHunks.Diff(before, after);

        Assert.Equal([new TextHunks.Hunk(10, 0, "-- "), new TextHunks.Hunk(20, 0, "-- ")], hunks);
    }

    [Fact]
    public void Uncommenting_only_removes_the_markers()
    {
        var hunks = TextHunks.Diff("-- select 1;\n-- select 2;\n", "select 1;\nselect 2;\n");

        Assert.Equal([new TextHunks.Hunk(0, 3, ""), new TextHunks.Hunk(13, 3, "")], hunks);
    }

    /// <summary>A reformat keeps each statement's closing <c>;</c> and the position after it — where a zone
    /// hangs — out of every hunk.</summary>
    [Fact]
    public void A_reformat_leaves_each_statement_end_untouched()
    {
        const string before = "select     1;\nselect     2;\n";
        const string after = "SELECT\n  1;\n\nSELECT\n  2;\n";

        var hunks = TextHunks.Diff(before, after);

        Assert.Equal(after, Apply(before, after));
        foreach (var end in new[] { before.IndexOf("1;", StringComparison.Ordinal) + 2, before.IndexOf("2;", StringComparison.Ordinal) + 2 })
        {
            Assert.DoesNotContain(hunks, h => h.Offset < end && end < h.Offset + h.Length);   // not strictly inside
        }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "select 1;")]
    [InlineData("select 1;", "")]
    [InlineData("abc", "abc")]
    [InlineData("a\nb\nc\n", "a\nx\nc\n")]
    [InlineData("a\r\nb\r\n", "a\r\n-- b\r\n")]
    [InlineData("select a, b from t where x = 1;", "SELECT a,\n       b\nFROM t\nWHERE x = 1;")]
    public void Applying_the_hunks_gives_the_new_text(string before, string after)
        => Assert.Equal(after, Apply(before, after));

    [Fact]
    public void Random_rewrites_round_trip()
    {
        var random = new Random(1234);
        const string alphabet = "ab;\n -";
        for (var run = 0; run < 300; run++)
        {
            string Make() => new(Enumerable.Range(0, random.Next(0, 60)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var before = Make();
            var after = Make();
            Assert.Equal(after, Apply(before, after));
            var hunks = TextHunks.Diff(before, after);
            for (var i = 1; i < hunks.Count; i++)
            {
                Assert.True(hunks[i - 1].Offset + hunks[i - 1].Length <= hunks[i].Offset, "hunks in order, not overlapping");
            }
        }
    }

    /// <summary>Past the edit-distance cap a run is one hunk (trimmed of its common ends), not an unbounded diff.</summary>
    [Fact]
    public void Past_the_cap_a_run_is_one_hunk()
    {
        var before = string.Concat(Enumerable.Range(0, 3000).Select(i => $"a{i}\n"));
        var after = string.Concat(Enumerable.Range(0, 3000).Select(i => $"b{i}\n"));

        Assert.Null(TextHunks.Myers(3000, 3000, 10, (i, j) => false));
        Assert.Equal(after, Apply(before, after));
    }

    /// <summary>Statement ends passed as sync points are hunk boundaries even where the diff would otherwise give
    /// up and replace the whole run as one hunk — so an anchor on one survives the reformat, its undo and redo.</summary>
    [Fact]
    public void Sync_points_are_never_strictly_inside_a_hunk()
    {
        var before = string.Concat(Enumerable.Range(0, 300).Select(i => $"select     {i};\n"));
        var after = string.Concat(Enumerable.Range(0, 300).Select(i => $"SELECT\n  {i};\n\n"));
        var sync = TextHunks.StatementSync(Bearing.Sql.PostgresDialect.Instance, before, after);
        Assert.Equal(300, sync.Count);

        var hunks = TextHunks.Diff(before, after, sync);

        Assert.Equal(after, ApplyHunks(before, hunks));
        foreach (var (end, _) in sync)
        {
            Assert.DoesNotContain(hunks, h => h.Offset < end && end < h.Offset + h.Length);
        }
        Assert.Contains(TextHunks.Diff(before, after), h => sync.Any(p => h.Offset < p.Before && p.Before < h.Offset + h.Length));
    }

    [Fact]
    public void Line_by_line_gives_one_hunk_per_changed_line()
    {
        const string before = "select 1;\nselect 2;\nselect 3;\n";
        const string after = "select 1;\n-- select 2;\n-- select 3;\n";

        Assert.Equal([new TextHunks.Hunk(10, 0, "-- "), new TextHunks.Hunk(20, 0, "-- ")], TextHunks.LineByLine(before, after));
        // Line count changed: one hunk without its common ends, no diff.
        Assert.Equal([new TextHunks.Hunk(2, 0, "b")], TextHunks.LineByLine("a\n", "a\nb"));
    }

    private static string ApplyHunks(string before, System.Collections.Generic.IReadOnlyList<TextHunks.Hunk> hunks)
    {
        var text = new StringBuilder(before);
        foreach (var hunk in hunks.Reverse())
        {
            text.Remove(hunk.Offset, hunk.Length).Insert(hunk.Offset, hunk.Text);
        }
        return text.ToString();
    }

    /// <summary>An anchor right after a hunk — a statement end, when the last word of a statement without a ';' is
    /// upper-cased — stays at the end of the new text, not at the start of the hunk; and undo and redo put it back
    /// at the end each time.</summary>
    [Fact]
    public void An_anchor_at_a_hunks_end_stays_at_the_end()
    {
        const string before = "select a from t order by a desc";
        const string after = "SELECT a FROM t ORDER BY a DESC";
        var document = new AvaloniaEdit.Document.TextDocument(before);
        var anchor = document.CreateAnchor(before.Length);
        anchor.MovementType = AvaloniaEdit.Document.AnchorMovementType.BeforeInsertion;

        TextHunkApply.Apply(document, 0, TextHunks.Diff(before, after, [(before.Length, after.Length)]));

        Assert.Equal(after, document.Text);
        Assert.False(anchor.IsDeleted);
        Assert.Equal(after.Length, anchor.Offset);

        document.UndoStack.Undo();
        Assert.Equal(before, document.Text);
        Assert.Equal(before.Length, anchor.Offset);
        document.UndoStack.Redo();
        Assert.Equal(after.Length, anchor.Offset);
    }

    /// <summary>Past the total work budget the rewrite is one hunk per stretch between sync points: still exact,
    /// still bounded, and the sync points still boundaries.</summary>
    [Fact]
    public void Past_the_work_budget_each_stretch_is_one_hunk()
    {
        var before = string.Concat(Enumerable.Range(0, 50).Select(i => $"select     {i};\n"));
        var after = string.Concat(Enumerable.Range(0, 50).Select(i => $"SELECT\n  {i};\n\n"));
        var sync = TextHunks.StatementSync(Bearing.Sql.PostgresDialect.Instance, before, after);

        var hunks = TextHunks.Diff(before, after, sync, maxWork: 10);

        Assert.Equal(after, ApplyHunks(before, hunks));
        Assert.True(hunks.Count <= sync.Count + 1, $"{hunks.Count} hunks for {sync.Count + 1} stretches");
        foreach (var (end, _) in sync)
        {
            Assert.DoesNotContain(hunks, h => h.Offset < end && end < h.Offset + h.Length);
        }
    }

    /// <summary>Text inserted right before a statement end (a pure insertion ending on the sync point) still
    /// carries an anchor there to the new end — and back on undo: the insertion is widened into a replace of the
    /// character before it.</summary>
    [Fact]
    public void An_insertion_ending_on_a_statement_end_carries_its_anchor()
    {
        const string before = "select 1";
        const string after = "select 1 ;";
        var hunks = TextHunks.Diff(before, after, [(before.Length, after.Length)]);
        Assert.DoesNotContain(hunks, h => h.Length == 0 && h.Offset == before.Length);
        Assert.Equal(after, ApplyHunks(before, hunks));

        var document = new AvaloniaEdit.Document.TextDocument(before);
        var anchor = document.CreateAnchor(before.Length);
        anchor.MovementType = AvaloniaEdit.Document.AnchorMovementType.BeforeInsertion;
        TextHunkApply.Apply(document, 0, hunks);
        Assert.Equal(after.Length, anchor.Offset);
        document.UndoStack.Undo();
        Assert.Equal(before.Length, anchor.Offset);
        document.UndoStack.Redo();
        Assert.Equal(after.Length, anchor.Offset);
    }

    /// <summary>The cheap fallback for a rewrite with too many edits still keeps statement ends as boundaries.</summary>
    [Fact]
    public void Per_stretch_keeps_statement_ends_as_boundaries()
    {
        var before = string.Concat(Enumerable.Range(0, 50).Select(i => $"select     {i};\n"));
        var after = string.Concat(Enumerable.Range(0, 50).Select(i => $"SELECT\n  {i};\n\n"));
        var sync = TextHunks.StatementSync(Bearing.Sql.PostgresDialect.Instance, before, after);

        var hunks = TextHunks.PerStretch(before, after, sync);

        Assert.Equal(after, ApplyHunks(before, hunks));
        foreach (var (end, _) in sync)
        {
            Assert.DoesNotContain(hunks, h => h.Offset < end && end < h.Offset + h.Length);
        }
    }
}
