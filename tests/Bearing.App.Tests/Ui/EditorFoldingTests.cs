using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using Bearing.App.Editing;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Folding invariants that keep AvaloniaEdit's height tree consistent (#82). The crash they guard is an
/// <c>InvalidOperationException</c> ("Trying to build visual line from collapsed line") thrown out of the
/// editor's layout pass, so it needs a realized editor — a plain unit test measures nothing and would pass
/// on any of these.
/// <para>
/// The reported crash itself is <b>not</b> reproduced here. Fourteen candidate sequences were driven against
/// the app's full editor configuration — tab switch with matching and with differing text, undo while folded,
/// a whole-document replace (Ctrl+/), deleting a folded region's text, folding twice across an edit, a caret
/// restored inside a fold, scrolled buffers, and synthetic clicks after each — and none threw. So these
/// assert the invariants the fix establishes rather than the failure it prevents; the fix is defensive.
/// </para>
/// <para>
/// The reporter's own trigger — the fold keyboard shortcut — could not be driven either: synthetic key input
/// never reaches the editor's tunnel-phase handler in a headless window (§4.5), so the shortcut path was
/// exercised by calling the commands directly, which is what the shortcut does anyway. What the shortcut
/// uniquely used to leave behind was a caret inside the region it had just collapsed; that state is now
/// unreachable, and <see cref="Fold_current_keeps_the_caret_visible"/> is the test for it.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class EditorFoldingTests
{
    private readonly UiTestSession _ui;

    public EditorFoldingTests(UiTestSession ui) => _ui = ui;

    private const string TwoStatements =
        "select 1,\n       2,\n       3\nfrom a;\n\nselect 4,\n       5,\n       6\nfrom b;\n";

    /// <summary>Offset of a character in the middle of the second statement's foldable body.</summary>
    private static int InsideSecondFold => TwoStatements.IndexOf("5", StringComparison.Ordinal);

    /// <summary>The wiring from <c>MainWindow</c>: one editor for every tab, chrome applied, foldings
    /// recomputed on every text change, the tab bridge owning the buffer swap.</summary>
    private static Fixture Editor()
    {
        var editor = new TextEditor { ShowLineNumbers = true };
        EditorChrome.Apply(editor);
        var folding = new SqlFoldingController(editor);
        var text = new EditorTextBehavior(editor);
        editor.TextChanged += (_, _) => folding.Refresh();
        var window = new Window { Width = 900, Height = 600, Content = editor };
        window.Show();
        window.UpdateLayout();
        return new Fixture(window, editor, folding, text);
    }

    private sealed record Fixture(
        Window Window, TextEditor Editor, SqlFoldingController Folding, EditorTextBehavior Text)
    {
        /// <summary>Load a buffer the way the host does: drop the folds first, then swap the text
        /// (see <c>MainWindow.LoadEditorFromSelectedTab</c>).</summary>
        public void Load(string sql, int caret = 0)
        {
            Folding.Reset();
            Text.Bind(new EditorTabViewModel("t.sql", sql) { CaretOffset = caret });
            Window.UpdateLayout();
        }

        public int FoldedCount => Folding.Sections.Count(s => s.IsFolded);

        /// <summary>The host's rule, in one place so the test asserts the same condition MainWindow uses:
        /// drop the folds when the buffer about to be loaded differs from what the editor holds.</summary>
        public void ResetIfBufferChanged(EditorTabViewModel? tab)
        {
            if (!ReferenceEquals(tab, Text.Tab)
                || !string.Equals(tab?.Text ?? "", Editor.Text, System.StringComparison.Ordinal))
                Folding.Reset();
        }

        public bool CaretHasVisualLine
            => Editor.TextArea.TextView.GetOrConstructVisualLine(
                   Editor.Document.GetLineByOffset(Editor.CaretOffset)) is not null;
    }

    /// <summary>The fold margin is installed, reserves width, and the buffer really does produce foldable
    /// regions. #74 reports the margin never appearing; in this configuration it is there, which narrows that
    /// issue to something the harness does not reproduce.</summary>
    [Fact]
    public Task The_fold_margin_is_installed_and_the_buffer_has_regions() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements);

        var margin = f.Editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        Assert.NotNull(margin.FoldingManager);
        Assert.True(margin.Bounds.Width > 0, $"the fold margin reserves no width: {margin.Bounds}");
        Assert.Equal(2, f.Folding.Sections.Count());
        f.Window.Close();
    });

    /// <summary>…and it puts marks on the surface, which is the actual claim in #74 and the one thing no
    /// property could answer. Asserted on rendered pixels: the margin is not a flat block of background, and
    /// folding changes what it draws (the marker flips, the region bracket goes). Deliberately not asserting
    /// colours — see <see cref="FrameCapture"/>.</summary>
    [Fact]
    public Task The_fold_margin_draws_markers_and_redraws_them_when_folded() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements);
        var margin = f.Editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();

        var unfolded = FrameCapture.Of(f.Window).Within(margin, f.Window);
        Assert.True(unfolded.Distinct().Count() > 1,
            "the fold margin rendered as one flat colour — nothing is drawn in it");

        f.Folding.FoldAll();
        f.Window.UpdateLayout();
        var folded = FrameCapture.Of(f.Window).Within(margin, f.Window);

        Assert.NotEqual(unfolded, folded);
        f.Window.Close();
    });

    /// <summary>Fold in one tab, switch to another: the new buffer must not open wearing the old one's folds,
    /// and measuring it must not throw. Measured honestly, this passes without <c>Reset</c> too — a
    /// whole-buffer replace already drops the sections — so it is a guard on the invariant, not a
    /// demonstration of the fix. What <c>Reset</c> adds is ordering: the folds come off while the old
    /// document's offsets still match the height tree, rather than as a side effect of the swap.</summary>
    [Fact]
    public Task A_buffer_swap_drops_the_previous_tabs_folds() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements);

        f.Folding.FoldAll();
        f.Window.UpdateLayout();
        Assert.Equal(2, f.FoldedCount);

        // The same text, so every region of the new buffer coincides with a folded region of the old — the
        // case where UpdateFoldings would otherwise carry the folded state across.
        f.Load(TwoStatements, caret: InsideSecondFold);

        Assert.Equal(0, f.FoldedCount);
        Assert.True(f.CaretHasVisualLine);
        f.Window.Close();
    });

    /// <summary>Loading a different script into the tab you are already on is a buffer swap too. Guarding
    /// the fold reset on the tab's identity missed it — Open replaces the text of the same tab object, so
    /// the document changed under live collapsed sections, which is exactly the state #82 needs (found in
    /// review of the guard).</summary>
    [Fact]
    public Task Loading_another_script_into_the_same_tab_drops_its_folds() => _ui.Run(() =>
    {
        var f = Editor();
        var tab = new EditorTabViewModel("open.sql", TwoStatements);
        f.Folding.Reset();
        f.Text.Bind(tab);
        f.Window.UpdateLayout();

        f.Folding.FoldAll();
        f.Window.UpdateLayout();
        Assert.Equal(2, f.FoldedCount);

        // What Open does: the same tab, a different buffer.
        tab.Text = TwoStatements.Replace("select", "SELECT", System.StringComparison.Ordinal);
        f.ResetIfBufferChanged(tab);
        f.Text.Bind(tab);
        f.Window.UpdateLayout();

        Assert.Equal(0, f.FoldedCount);
        Assert.True(f.CaretHasVisualLine);
        f.Window.Close();
    });

    /// <summary>…and a re-bind that does not change the buffer keeps them. That is the sidebar's editor-sync
    /// callback, which fires after deleting any script — including one that isn't open.</summary>
    [Fact]
    public Task Re_binding_the_same_buffer_keeps_the_folds() => _ui.Run(() =>
    {
        var f = Editor();
        var tab = new EditorTabViewModel("open.sql", TwoStatements);
        f.Text.Bind(tab);
        f.Window.UpdateLayout();
        f.Folding.FoldAll();
        f.Window.UpdateLayout();
        Assert.Equal(2, f.FoldedCount);

        f.ResetIfBufferChanged(tab);   // no text change: nothing to drop
        f.Window.UpdateLayout();

        Assert.Equal(2, f.FoldedCount);
        f.Window.Close();
    });

    /// <summary>Folding everything while the caret sits inside a region moves the caret onto that region's
    /// header line, instead of leaving it collapsed out of sight.</summary>
    [Fact]
    public Task Fold_all_moves_a_swallowed_caret_onto_the_header_line() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements, caret: InsideSecondFold);

        var section = f.Folding.Sections.Single(
            s => s.StartOffset < InsideSecondFold && s.EndOffset > InsideSecondFold);
        var header = section.StartOffset;

        f.Folding.FoldAll();
        f.Window.UpdateLayout();

        Assert.Equal(header, f.Editor.CaretOffset);
        Assert.True(f.CaretHasVisualLine);
        f.Window.Close();
    });

    /// <summary>Same for the single-statement command, which is the more common gesture: the caret is
    /// normally inside the statement being folded.</summary>
    [Fact]
    public Task Fold_current_keeps_the_caret_visible() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements, caret: InsideSecondFold);

        f.Folding.FoldCurrent();
        f.Window.UpdateLayout();

        Assert.Equal(1, f.FoldedCount);
        Assert.Equal(f.Folding.Sections.Single(s => s.IsFolded).StartOffset, f.Editor.CaretOffset);
        Assert.True(f.CaretHasVisualLine);
        f.Window.Close();
    });

    /// <summary>Unfolding still works after the caret has been moved out — the fold commands stay a pair.</summary>
    [Fact]
    public Task Unfold_all_restores_every_region() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements, caret: InsideSecondFold);

        f.Folding.FoldAll();
        f.Window.UpdateLayout();
        Assert.Equal(2, f.FoldedCount);

        f.Folding.UnfoldAll();
        f.Window.UpdateLayout();

        Assert.Equal(0, f.FoldedCount);
        f.Window.Close();
    });

    /// <summary>The caret invariant holds on every edit, not only on the fold commands. UpdateFoldings keeps
    /// an unchanged region's folded state, so an edit that did not come from typing inside the fold can slide
    /// a still-folded section over the caret — an undo, a whole-document replace, a paste. Found in review:
    /// only the fold commands were correcting it.</summary>
    [Fact]
    public Task An_edit_never_leaves_the_caret_inside_a_folded_region() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements);
        f.Folding.FoldAll();
        f.Window.UpdateLayout();

        // Put the caret back inside a folded region the way a programmatic edit would, then make any edit at
        // all: the refresh that follows has to take the caret back out.
        f.Editor.CaretOffset = InsideSecondFold;
        f.Editor.Document.Insert(0, "-- header");
        f.Window.UpdateLayout();

        Assert.DoesNotContain(f.Folding.Sections, s =>
            s.IsFolded && f.Editor.CaretOffset > s.StartOffset && f.Editor.CaretOffset < s.EndOffset);
        Assert.True(f.CaretHasVisualLine);
        f.Window.Close();
    });

    /// <summary>An edit that guts a folded region leaves nothing folded over dead lines: <c>Refresh</c>
    /// unfolds the invalidated section before rebuilding rather than carrying its collapsed state onto a
    /// region that no longer matches it.</summary>
    [Fact]
    public Task An_edit_that_guts_a_folded_region_leaves_it_unfolded() => _ui.Run(() =>
    {
        var f = Editor();
        f.Load(TwoStatements);

        f.Folding.FoldAll();
        f.Window.UpdateLayout();
        Assert.Equal(2, f.FoldedCount);

        // Collapse the whole second statement onto one line, so its region can no longer span text.
        var second = TwoStatements.IndexOf("select 4", StringComparison.Ordinal);
        f.Editor.Document.Replace(second, f.Editor.Document.TextLength - second, "select 4 from b;");
        f.Window.UpdateLayout();

        Assert.All(f.Folding.Sections, s => Assert.True(
            !s.IsFolded || (s.StartOffset < s.EndOffset && s.EndOffset <= f.Editor.Document.TextLength),
            $"a folded section no longer spans text: {s.StartOffset}..{s.EndOffset} "
            + $"of {f.Editor.Document.TextLength}"));
        Assert.True(f.CaretHasVisualLine);
        f.Window.Close();
    });
}
