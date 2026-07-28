using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Squirrel.App.Formatting;
using Squirrel.App.Input;
using Squirrel.App.Results;
using Squirrel.App.ViewModels;
using Squirrel.Core.Workspace;
using Path = Avalonia.Controls.Shapes.Path;

namespace Squirrel.App.Controls;

public sealed partial class ResultView
{
    // ---- Cell selection + quick-stats (design RESULTS_GRID §7) --------------------------------

    /// <summary>During a drag, hit-test the cell under the pointer and select the rectangle from the
    /// anchor to it (all selectable columns; bool checkbox columns are skipped).</summary>
    private void DragSelectTo(DataGrid grid, ResultSetViewModel result, PointerEventArgs e)
    {
        if (!_sel.Dragging || !ReferenceEquals(_sel.Result, result) || _sel.DragAnchor is not { } anchor) return;
        if (grid.InputHitTest(e.GetPosition(grid)) is not Visual hit) return;
        var cell = hit.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Tag is ValueTuple<object?[], int>);
        if (cell?.Tag is not ValueTuple<object?[], int> target) return;

        _sel.Active = (target.Item1, target.Item2);
        SelectRectangle(result, anchor, (target.Item1, target.Item2));
    }

    /// <summary>Replace the selection with the rectangle spanning cells a..b (inclusive), skipping bool
    /// checkbox columns (they render no selection ring). Shared by drag, Shift-click and Shift+arrows.</summary>
    private void SelectRectangle(ResultSetViewModel result, (object?[] Row, int Col) a, (object?[] Row, int Col) b)
    {
        var rows = result.Rows;
        int r0 = rows.IndexOf(a.Row), r1 = rows.IndexOf(b.Row);
        if (r0 < 0 || r1 < 0) return;
        if (r0 > r1) (r0, r1) = (r1, r0);
        int c0 = Math.Min(a.Col, b.Col), c1 = Math.Max(a.Col, b.Col);

        _sel.Result = result;
        _sel.Cells.Clear();
        for (var r = r0; r <= r1; r++)
        {
            var rr = rows[r];
            for (var c = c0; c <= c1; c++)
                if (c < rr.Length && !IsBoolColumn(result.Columns[c]))
                    _sel.Cells.Add((rr, c));
        }
        SelectionChanged();
    }

    private void ToggleCellSelection(ResultSetViewModel result, object?[] row, int index, bool extend)
    {
        if (!ReferenceEquals(_sel.Result, result)) { _sel.Cells.Clear(); _sel.Result = result; }
        var key = (row, index);
        if (extend) { if (!_sel.Cells.Remove(key)) _sel.Cells.Add(key); }
        else { _sel.Cells.Clear(); _sel.Cells.Add(key); }
        SelectionChanged();
    }

    private void ClearSelection() => _sel.Clear();

    // ---- Keyboard navigation & actions (spreadsheet-style) -----------------------------------

    private static bool IsNavKey(Key k) => k is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    /// <summary>Keyboard-drive a result grid: arrows/Home/End/PageUp/PageDown move the active cell
    /// (Shift extends a rectangular range, Ctrl jumps to the row/column edge); Ctrl+A selects all;
    /// Ctrl+C (or Ctrl+Insert) copies the selection as TSV; Delete marks the selected rows for deletion
    /// on an editable result; Enter/F2 edits the active cell; Escape clears the selection. Runs in the
    /// tunnel phase and marks handled keys so the DataGrid's own navigation/copy don't also fire.</summary>
    private void OnGridKey(DataGrid grid, ResultSetViewModel result, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;                 // a cell editor is focused — let it have the keys
        if (!result.HasGrid || result.Rows.Count == 0) return;

        // Discrete grid commands (copy, select-all, delete, begin-edit, clear) go through the shared
        // dispatcher; the grid+result they act on is published on _keyStrokeTarget for the duration of the
        // dispatch only (grid commands are synchronous, so they read it inside TryHandle). A command whose
        // guard is false (Delete on a read-only set, Escape with no selection) leaves the key unhandled so
        // it falls through to navigation below or bubbles to the window.
        _keyStrokeTarget = (grid, result);
        bool handled;
        try { handled = _dispatcher?.TryHandle(e, KeyScope.Grid) == true; }
        finally { _keyStrokeTarget = null; }
        if (handled) return;

        // Everything below is spatial cell-cursor motion — intrinsic grid navigation, not a rebindable
        // command (mirrors how the editor's caret motion isn't in the keymap).
        if (!IsNavKey(e.Key)) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // First arrow into a grid that isn't the active one: seed the active cell at the top-left.
        if (!ReferenceEquals(_sel.Result, result) || _sel.Active is not { } active)
        {
            MoveActive(grid, result, result.Rows[0], FirstSelectableColumn(result), extend: false);
            e.Handled = true;
            return;
        }

        var rows = result.Rows;
        var r = rows.IndexOf(active.Row);
        if (r < 0) return;
        var c = active.Col;
        var last = rows.Count - 1;
        var page = Math.Max(1, VisiblePageSize(grid) - 1);

        int nr = r, nc = c;
        switch (e.Key)
        {
            case Key.Left:     nc = ctrl ? FirstSelectableColumn(result) : StepColumn(result, c, -1); break;
            case Key.Right:    nc = ctrl ? LastSelectableColumn(result)  : StepColumn(result, c, +1); break;
            case Key.Up:       nr = ctrl ? 0 : Math.Max(0, r - 1); break;
            case Key.Down:     nr = ctrl ? last : Math.Min(last, r + 1); break;
            case Key.Home:     nc = FirstSelectableColumn(result); if (ctrl) nr = 0; break;
            case Key.End:      nc = LastSelectableColumn(result);  if (ctrl) nr = last; break;
            case Key.PageUp:   nr = Math.Max(0, r - page); break;
            case Key.PageDown: nr = Math.Min(last, r + page); break;
        }

        MoveActive(grid, result, rows[nr], nc, extend: shift);
        e.Handled = true;
    }

    /// <summary>Move the active cell to (row, col); Shift extends the rectangle from the anchor, otherwise
    /// the selection collapses to the single cell and re-seeds the anchor. Scrolls the target into view.</summary>
    private void MoveActive(DataGrid grid, ResultSetViewModel result, object?[] row, int col, bool extend)
    {
        _sel.Active = (row, col);
        _sel.Result = result;
        if (extend)
        {
            _sel.Anchor ??= _sel.Active;
            SelectRectangle(result, _sel.Anchor.Value, _sel.Active.Value);
        }
        else
        {
            _sel.Anchor = _sel.Active;
            _sel.Cells.Clear();
            if (col < result.Columns.Count && !IsBoolColumn(result.Columns[col])) _sel.Cells.Add((row, col));
            SelectionChanged();
        }
        if (col < grid.Columns.Count) grid.ScrollIntoView(row, grid.Columns[col]);
    }

    /// <summary>Next non-bool column from <paramref name="from"/> in direction ±1, or stay put at an edge.</summary>
    private static int StepColumn(ResultSetViewModel result, int from, int dir)
    {
        for (var c = from + dir; c >= 0 && c < result.Columns.Count; c += dir)
            if (!IsBoolColumn(result.Columns[c])) return c;
        return from;
    }

    private static int FirstSelectableColumn(ResultSetViewModel result)
    {
        for (var c = 0; c < result.Columns.Count; c++)
            if (!IsBoolColumn(result.Columns[c])) return c;
        return 0;
    }

    private static int LastSelectableColumn(ResultSetViewModel result)
    {
        for (var c = result.Columns.Count - 1; c >= 0; c--)
            if (!IsBoolColumn(result.Columns[c])) return c;
        return Math.Max(0, result.Columns.Count - 1);
    }

    /// <summary>Approximate rows-per-page from the realized DataGridRow visuals (for PageUp/PageDown).</summary>
    private static int VisiblePageSize(DataGrid grid)
    {
        var realized = grid.GetVisualDescendants().OfType<DataGridRow>().Count(dgr => dgr.IsVisible);
        return realized > 0 ? realized : 12;
    }

    /// <summary>Select every (non-bool) cell of the result (Ctrl+A).</summary>
    private void SelectAll(ResultSetViewModel result)
    {
        _sel.Result = result;
        _sel.Cells.Clear();
        foreach (var row in result.Rows)
            for (var c = 0; c < result.Columns.Count; c++)
                if (c < row.Length && !IsBoolColumn(result.Columns[c]))
                    _sel.Cells.Add((row, c));
        _sel.Active ??= (result.Rows[0], FirstSelectableColumn(result));
        _sel.Anchor ??= _sel.Active;
        SelectionChanged();
    }

    /// <summary>Copy the selection to the clipboard as tab-separated rows (condensed to the selected
    /// rows × columns; gaps in a non-rectangular selection come out blank).</summary>
    private void CopySelection(ResultSetViewModel result)
    {
        if (!ReferenceEquals(_sel.Result, result) || _sel.Cells.Count == 0) return;
        var rows = result.Rows;
        var rowIdx = _sel.Cells.Select(s => rows.IndexOf(s.Row)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        var colIdx = _sel.Cells.Select(s => s.Col).Distinct().OrderBy(i => i).ToList();
        if (rowIdx.Count == 0 || colIdx.Count == 0) return;

        var text = string.Join("\n", rowIdx.Select(ri =>
        {
            var row = rows[ri];
            return string.Join("\t", colIdx.Select(c =>
                _sel.Cells.Contains((row, c)) && c < row.Length ? CellText(row, c) : ""));
        }));
        TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>Mark every row that owns a selected cell for deletion (editable results). A pending-new
    /// row is dropped outright, so prune any now-dangling selection entries afterwards.</summary>
    private void DeleteSelectedRows(DataGrid grid, ResultSetViewModel result)
    {
        if (!result.IsEditable || !ReferenceEquals(_sel.Result, result)) return;
        foreach (var row in _sel.Cells.Select(s => s.Row).Distinct().ToList())
            if (!result.IsRowDeleted(row)) result.ToggleDelete(row); // mark (never un-mark) for deletion
        _sel.Cells.RemoveWhere(s => !result.Rows.Contains(s.Row));
        if (_sel.Active is { } a && !result.Rows.Contains(a.Row)) { _sel.Active = null; _sel.Anchor = null; }
        RefreshRowColors(grid, result);
        SelectionChanged();
    }

    /// <summary>Begin editing the active cell via the DataGrid's own edit machinery (Enter/F2).</summary>
    private void BeginEditActive(DataGrid grid, ResultSetViewModel result)
    {
        if (_sel.Active is not { } a || !ReferenceEquals(_sel.Result, result)) return;
        if (result.Rows.IndexOf(a.Row) < 0 || a.Col >= grid.Columns.Count) return;
        grid.ScrollIntoView(a.Row, grid.Columns[a.Col]);
        grid.SelectedItem = a.Row;
        grid.CurrentColumn = grid.Columns[a.Col];
        grid.BeginEdit();
    }

    /// <summary>Recompute the stats bars and re-apply every realized cell's selection ring.</summary>
    private void SelectionChanged()
    {
        foreach (var (result, bar) in _statsBars)
        {
            var show = ReferenceEquals(result, _sel.Result) && _sel.Cells.Count >= 2;
            if (show && CellStats.Aggregate(SelectedValues(result)) is { } stats)
            {
                bar.Child = BuildStatsContent(result, _sel.Cells.Count, stats);
                bar.IsVisible = true;
            }
            else
            {
                bar.IsVisible = false;
            }
        }
        _sel.CellRestyle?.Invoke();
    }

    private IEnumerable<object?> SelectedValues(ResultSetViewModel result)
    {
        if (!ReferenceEquals(result, _sel.Result)) yield break;
        foreach (var (row, col) in _sel.Cells)
        {
            if (col >= row.Length || col >= result.Columns.Count) continue;
            // Only "measure" columns feed the stats — summing/averaging PK/FK identifiers is meaningless.
            var isPk = result.PrimaryKeyColumns.Contains(col);
            var isFk = result.ForeignKeyColumns.Contains(col);
            if (!CellStats.IsMeasureColumn(result.Columns[col].ClrType, isPk, isFk)) continue;
            yield return row[col];
        }
    }

    /// <summary>Wrap content with a bottom quick-stats bar (hidden until ≥2 measure cells are selected).</summary>
    private Control WithStatsBar(Control content, ResultSetViewModel result)
    {
        var bar = new Border
        {
            Background = Res("Bg.Hover"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = Separator,
            Padding = new Thickness(8, 4),
            IsVisible = false,
        };
        _statsBars.Add((result, bar));
        DockPanel.SetDock(bar, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(bar);
        panel.Children.Add(content);
        return panel;
    }

    private Control BuildStatsContent(ResultSetViewModel result, int count, CellStatistics stats)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Stat($"{count} cells", "Text.Dim"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"count {stats.Count}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"sum {CellStats.Format(stats.Sum)}", "Ok.Green")); // sum highlighted green
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"avg {CellStats.Format(stats.Avg)}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"min {CellStats.Format(stats.Min)}", "Text.Primary"));
        stack.Children.Add(Sep());
        stack.Children.Add(Stat($"max {CellStats.Format(stats.Max)}", "Text.Primary"));

        var clear = IconTextButton("Clear", "Clear selection");
        clear.Margin = new Thickness(12, 0, 0, 0);
        clear.Click += (_, _) => { if (ReferenceEquals(_sel.Result, result)) { ClearSelection(); SelectionChanged(); } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(stack, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(stack);
        grid.Children.Add(clear);
        return grid;

        static Control Stat(string text, string colorKey) => new TextBlock
        {
            Text = text, Foreground = Res(colorKey), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        static Control Sep() => new TextBlock
        {
            Text = " · ", Foreground = Res("Text.Faint"), VerticalAlignment = VerticalAlignment.Center,
        };
    }

}
