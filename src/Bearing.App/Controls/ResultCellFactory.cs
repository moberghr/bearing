using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// Builds a results grid's columns: the header (name + PK/FK/type badges), the display cell for each column
/// kind (foreign key with a jump icon, bool as a checkbox, everything else a selectable value cell), and the
/// shared in-cell editor.
/// <para>
/// Every value cell is wrapped in a selection border that participates in
/// <see cref="GridSelectionController"/>'s spreadsheet selection — which is why this needs the controller
/// rather than being a bag of statics. The two things it can't decide for itself, opening the inspector and
/// following a foreign key, arrive as callbacks.
/// </para>
/// </summary>
public sealed class ResultCellFactory
{
    // Non-text pixels inside a value cell: the text's margin plus the selection border's 1px reserve, both
    // sides (see MakeSelectable).
    private const double CellChrome = ResultGridChrome.CellTextInset * 2;

    // On top of that, the width a cell reserves for an inline affordance: the 16–18px glyph with its
    // margins, plus the DockPanel's 18px right margin keeping it clear of the scrollbar.
    private const double AffordanceWidth = 40;

    private readonly GridSelectionController _selection;
    private readonly Action<ResultSetViewModel, int, object?[]> _inspect;
    private readonly Action<ResultSetViewModel, int, object?[]> _followForeignKey;

    public ResultCellFactory(
        GridSelectionController selection,
        Action<ResultSetViewModel, int, object?[]> inspect,
        Action<ResultSetViewModel, int, object?[]> followForeignKey)
    {
        _selection = selection;
        _inspect = inspect;
        _followForeignKey = followForeignKey;
    }

    /// <summary>The column for <paramref name="index"/>, picked by column kind, with its header and a
    /// content-derived initial width (#30).</summary>
    public DataGridColumn BuildColumn(ResultSetViewModel result, int index, DataGrid grid)
    {
        // FK columns keep their jump-icon template; bool columns render a checkbox; everything else is a
        // value cell (measure / inspectable / plain text), editable via a template editor
        // (indexer-bound DataGridTextColumns don't edit reliably on recycle — see CellEditor).
        DataGridColumn col;
        if (result.ForeignKeyColumns.Contains(index))
            col = ForeignKeyColumn(result, index, grid);
        else if (ColumnKinds.IsBool(result.Columns[index]))
            col = new DataGridTemplateColumn { CellTemplate = BoolCell(result, index, grid) }; // toggles inline
        else if (result.IsEditable)
            col = new DataGridTemplateColumn
            {
                Tag = index, // column index, read back in CellEditEnding
                CellTemplate = ValueCell(result, index, grid),
                CellEditingTemplate = CellEditor(index),
            };
        else
            col = new DataGridTemplateColumn { CellTemplate = ValueCell(result, index, grid) };

        col.Header = ColumnHeader(result, index);
        col.Width = new DataGridLength(InitialWidth(result, index));
        return col;
    }

    /// <summary>Column header = the name plus inline type badges: teal PK, violet FK, mint jsonb/json
    /// (design RESULTS_GRID §3).</summary>
    private static Control ColumnHeader(ResultSetViewModel result, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = result.Columns[index].Name, VerticalAlignment = VerticalAlignment.Center });
        foreach (var (text, color) in Badges(result, index)) row.Children.Add(ResultChrome.Badge(text, color));
        return row;
    }

    /// <summary>The header's inline type badges. Shared with the width arithmetic below, which has to know
    /// how many there are before the header is measured.</summary>
    private static List<(string Text, string Color)> Badges(ResultSetViewModel result, int index)
    {
        var badges = new List<(string, string)>(2);
        if (result.PrimaryKeyColumns.Contains(index)) badges.Add(("PK", "Accent.Brand"));
        if (result.ForeignKeyColumns.Contains(index)) badges.Add(("FK", "Syntax.Keyword"));
        var type = result.Columns[index].DataTypeName;
        if (ColumnKinds.IsDocument(type)) badges.Add((type.ToLowerInvariant(), "Syntax.Table"));
        return badges;
    }

    /// <summary>The width the column opens at: whatever its header and its widest loaded value need, capped
    /// (<see cref="ColumnWidths"/>). Nothing is left on the DataGrid's <c>Auto</c> sizing, which grew a
    /// column to its longest realized value and pushed the rest off screen (#30). A foreign-key or json
    /// column also reserves room for its always-present ↗ / ⤢ glyph, so the value isn't sized into it.
    /// ("json column" is any document column now — xml gets the same affordance, see
    /// <see cref="ColumnKinds.IsDocument"/>.)</summary>
    private static double InitialWidth(ResultSetViewModel result, int index)
    {
        var column = result.Columns[index];
        var hasGlyph = result.ForeignKeyColumns.Contains(index) || ColumnKinds.IsDocument(column.DataTypeName);
        return ColumnWidths.Initial(
            headerChars: column.Name.Length,
            headerExtra: ResultGridChrome.HeaderChromeFor(Badges(result, index).Select(b => b.Text)),
            valueChars: ColumnWidths.ValueChars(result.Rows, index),
            cellExtra: CellChrome + (hasGlyph ? AffordanceWidth : 0),
            charWidth: ResultGridChrome.CharAdvance);
    }

    /// <summary>A value display cell: text (dimmed italic "(null)", numeric in code color), plus an
    /// inspect (⤢) affordance for document columns (json/jsonb/xml) and any long/multiline value. Every value cell is
    /// selectable (single/drag/modifier-click); numeric selections drive the quick-stats bar.</summary>
    private IDataTemplate ValueCell(ResultSetViewModel result, int index, DataGrid grid)
    {
        var isJsonCol = ColumnKinds.IsDocument(result.Columns[index].DataTypeName);
        var numeric = CellStats.IsNumeric(result.Columns[index].ClrType);
        return new FuncDataTemplate<object?[]>((row, _) =>
            MakeSelectable(() => ValueContent(result, index, row, isJsonCol, numeric), result, row, index, grid));
    }

    /// <summary>The inside of a value cell, built from the row's current value — a function rather than a
    /// one-off because a write that doesn't come from the in-cell editor (a paste, the keyboard bool toggle)
    /// has to be able to re-render the cell it changed. Everything here depends on the value: the text, the
    /// dimmed-italic NULL styling, and whether the ⤢ inspect affordance is there at all.</summary>
    private Control ValueContent(ResultSetViewModel result, int index, object?[]? row, bool isJsonCol, bool numeric)
    {
        var isNull = row is null || index >= row.Length || row[index] is null;
        var text = new TextBlock
        {
            Text = GridSelectionOps.CellText(row, index),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(ResultGridChrome.CellTextMargin, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = isNull ? NullBrush : (numeric ? Res("Text.Code") : Res("Text.Primary")),
            FontStyle = isNull ? FontStyle.Italic : FontStyle.Normal,
        };
        if (isNull) return text;

        var raw = GridSelectionOps.CellText(row, index);
        if (!isJsonCol && raw.Length <= 60 && !raw.Contains('\n')) return text;

        var expand = ResultChrome.InspectAffordance();
        // handledEventsToo: the DataGrid marks the press handled in the tunnel phase.
        expand.AddHandler(InputElement.PointerPressedEvent, (_, e) => { _inspect(result, index, row!); e.Handled = true; },
            RoutingStrategies.Bubble, handledEventsToo: true);
        var dock = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; // keep ⤢ clear of the scrollbar
        DockPanel.SetDock(expand, Dock.Right);
        dock.Children.Add(expand);
        dock.Children.Add(text);
        return dock;
    }

    /// <summary>A foreign-key column: the value shows as plain text with a clickable jump-icon on the
    /// right that navigates to the referenced row. In an editable result the value is also editable
    /// (double-click / F2) via the shared editor; the jump icon stays on the display cell.</summary>
    private DataGridColumn ForeignKeyColumn(ResultSetViewModel result, int index, DataGrid grid)
        => new DataGridTemplateColumn
        {
            Tag = index, // enables CellEditEnding capture when the grid is editable
            CellEditingTemplate = result.IsEditable ? CellEditor(index) : null,
            CellTemplate = new FuncDataTemplate<object?[]>((row, _) => row is null
                ? new TextBlock()
                : MakeSelectable(() => ForeignKeyContent(result, index, row), result, row, index, grid)),
        };

    /// <summary>The inside of a foreign-key cell: the value plus the ↗ jump icon, which is hidden on a NULL —
    /// so this is value-dependent and gets rebuilt when the row changes (see <see cref="ValueContent"/>).</summary>
    private Control ForeignKeyContent(ResultSetViewModel result, int index, object?[] row)
    {
        var hasValue = row.Length > index && row[index] is not null;

        var value = new TextBlock
        {
            Text = GridSelectionOps.CellText(row, index),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(ResultGridChrome.CellTextMargin, 0),
        };

        var jump = ResultChrome.JumpAffordance();
        jump.IsVisible = hasValue;
        // handledEventsToo: the DataGrid marks the press handled in the tunnel phase.
        jump.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            e.Handled = true;
            if (hasValue) _followForeignKey(result, index, row);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        var cell = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; // keep ↗ clear of the scrollbar
        DockPanel.SetDock(jump, Dock.Right);
        cell.Children.Add(jump);
        cell.Children.Add(value);
        return cell;
    }

    /// <summary>A boolean cell: a checkbox showing the value. It selects, drags and copies exactly like every
    /// other cell (#9). A plain click <i>on the box</i> also cycles the value; a click anywhere else in the
    /// cell — like a click in any other column — only selects.
    /// <para>
    /// The CheckBox stays an inert indicator even so: the click is handled by the cell, which tests the press
    /// against the indicator's bounds (see <c>GridSelectionController.TryToggleBoolAtPointer</c>). That keeps
    /// one write path for the mouse, the double-tap and the keyboard, and it is why a value change can safely
    /// re-render the cell — a live CheckBox holds the pointer capture between press and release, and replacing
    /// it in between silently ate the click.
    /// </para></summary>
    private IDataTemplate BoolCell(ResultSetViewModel result, int index, DataGrid grid)
        => new FuncDataTemplate<object?[]>((row, _) =>
            MakeSelectable(() => BoolContent(row, index), result, row, index, grid));

    /// <summary>The indicator, and — because the cell hit-tests against it — the exact area where a click
    /// cycles the value. It is drawn (<see cref="ResultChrome.BoolIndicator"/>) rather than a Fluent
    /// <c>CheckBox</c>: that control sizes itself as a labelled one — a 20px box plus an 8px content pad,
    /// inside a 32px minimum — so it both overhung the visible box on three sides and, a cell being as tall
    /// as its content, raised every row in the grid off the 26px floor.
    /// <para>
    /// Never hit-testable: every write goes through <c>GridSelectionController.ToggleBool</c>, so the mouse,
    /// the double-tap and the keyboard are one code path. That is also why the cell can safely re-render on a
    /// value change — a live CheckBox held the pointer capture between press and release, and replacing it in
    /// between silently ate the click.
    /// </para></summary>
    private static Control BoolContent(object?[]? row, int index)
    {
        var indicator = ResultChrome.BoolIndicator(BoolCellValue.Read(row, index));
        indicator.IsHitTestVisible = false; // display only (not greyed out like IsEnabled=false would be)
        return indicator;
    }

    /// <summary>The in-cell editor (a TextBox seeded with the current value) shared by editable and FK
    /// columns. A template — re-materialized per row as the grid recycles containers on scroll —
    /// deliberately NOT a <see cref="DataGridTextColumn"/> with an indexer binding (<c>[i]</c>): that
    /// binding doesn't re-evaluate when a row container is reused (Avalonia DataGrid recycling, #17534).</summary>
    public static IDataTemplate CellEditor(int index)
        => new FuncDataTemplate<object?[]>((row, _) =>
        {
            var box = new TextBox
            {
                Text = GridSelectionOps.CellText(row, index),
                Padding = new Thickness(3, 1),
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            // Entering edit mode preselects the whole value (type-to-replace, like a spreadsheet).
            box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
            return box;
        });

    /// <summary>Wrap a cell's content in a selectable border: single-click selects (blue ring) and
    /// starts a drag rectangle, Ctrl/Cmd-click toggles, Shift-click extends a rectangle; the whole-row
    /// highlight stays invisible. Numeric selections feed the quick-stats bar.</summary>
    /// <param name="content">Builds the cell's inside from the row's current value. Called now, and again
    /// whenever that value changed under us — the display templates are materialized once per realized row,
    /// so a write that doesn't go through the in-cell editor (a paste, the keyboard bool toggle) would
    /// otherwise leave the old text on screen while the pending UPDATE carried the new one.</param>
    /// <remarks>
    /// Rebuilding the content is safe for every cell kind because no cell's content holds the pointer
    /// capture: a press hit-tests to this Border (never replaced — only its child is), and a drag captures the
    /// grid. That was not true while the checkbox was a live control, and replacing it mid-click ate the
    /// release that toggled it.
    /// </remarks>
    private Control MakeSelectable(
        Func<Control> content, ResultSetViewModel result, object?[]? row, int index, DataGrid grid)
    {
        var border = new Border
        {
            Child = content(),
            Background = Brushes.Transparent,       // hit-testable across the whole cell
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(2),
            // Reserve 1px on every side always (as padding) so drawing the selection border later
            // doesn't shift the content — border thickness and padding trade off to keep inset constant.
            Padding = new Thickness(1),
        };
        if (row is null) return border; // nothing to key a selection on

        border.Tag = (row, index); // read back when a drag hit-tests the cell under the pointer

        // The value this cell is currently showing. Compared on every restyle, so re-rendering costs a
        // reference check per realized cell rather than a rebuilt visual per arrow keypress.
        var rendered = ValueAt(row, index);
        void Restyle()
        {
            if (!Equals(ValueAt(row, index), rendered))
            {
                rendered = ValueAt(row, index);
                border.Child = content();
            }
            ApplySelectionRing(border, result, row, index);
        }
        Restyle();
        _selection.AddRestyleListener(Restyle);
        border.DetachedFromVisualTree += (_, _) => _selection.RemoveRestyleListener(Restyle);

        // Our press handler marks the event handled, so the DataGrid never gets to set its own current cell
        // from the click. Focusing a grid that has *no* current cell makes Avalonia adopt the first column
        // and scroll it into view — which is why clicking a cell while scrolled right sometimes threw the
        // view back to the leftmost column. Handing it the clicked cell first makes that a no-op.
        // `CurrentItem` is internal in Avalonia 12, so the current *row* has to come from the selection; the
        // row highlight that would otherwise paint is already suppressed (ResultGridChrome).
        void FocusClickedCell()
        {
            if (index < grid.Columns.Count)
            {
                grid.SelectedItem = row;
                grid.CurrentColumn = grid.Columns[index];
            }
            grid.Focus();
        }

        // Corrective, on top of the above: whatever moved the viewport during the click — the DataGrid
        // adopting a current cell, or the quick-stats bar appearing and re-measuring the grid — the cell you
        // clicked ends up visible again. A no-op when nothing moved. Done twice because the two candidate
        // causes land in different frames: the re-measure happens in the layout pass after this returns.
        void KeepClickedCellInView()
        {
            if (index >= grid.Columns.Count) return;
            var column = grid.Columns[index];
            grid.ScrollIntoView(row, column);
            Dispatcher.UIThread.Post(() => grid.ScrollIntoView(row, column), DispatcherPriority.Loaded);
        }

        border.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount >= 2) return; // let the grid start editing on double-click
            var point = e.GetCurrentPoint(border).Properties;
            if (point.IsRightButtonPressed)
            {
                // Right-click arms the context menu. Outside the current selection it collapses onto this
                // cell, so Copy/Copy as can't act on cells the user isn't pointing at; *inside* it leaves the
                // selection alone (right-clicking a block to copy it must not shrink it to one cell). Never
                // marked handled — the flyout still has to open.
                FocusClickedCell();
                if (!_selection.IsSelected(result, row, index)) _selection.SelectSingle(result, row, index);
                KeepClickedCellInView();
                return;
            }
            if (!point.IsLeftButtonPressed) return;
            FocusClickedCell(); // route subsequent key presses here, without a jump to column 0

            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (shift && _selection.CanExtendFrom(result)) _selection.ExtendTo(result, row, index);
            else if (ctrl) _selection.ToggleCell(result, row, index);
            else _selection.SelectSingleAndBeginDrag(result, row, index, e.Pointer, grid);
            KeepClickedCellInView();
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return border;
    }

    private static object? ValueAt(object?[]? row, int index)
        => row is not null && index < row.Length ? row[index] : null;

    /// <summary>Draw (or clear) a cell's selection ring.
    /// <para>
    /// Adjacent selected cells are merged: they all fill, but only the block's outer edges are stroked — an
    /// edge shared with another selected cell gets no border, so the selection reads as one contiguous
    /// region instead of a grid of individually-ringed cells.
    /// </para>
    /// </summary>
    private void ApplySelectionRing(Border border, ResultSetViewModel result, object?[] row, int index)
    {
        if (!_selection.IsSelected(result, row, index))
        {
            border.Background = Brushes.Transparent;
            border.BorderThickness = new Thickness(0);
            border.Padding = new Thickness(1); // full reserve → content stays put
            border.CornerRadius = new CornerRadius(2);
            return;
        }

        var rows = result.Rows;
        var r = rows.IndexOf(row);
        var up = r > 0 && _selection.IsSelected(result, rows[r - 1], index);
        var down = r >= 0 && r + 1 < rows.Count && _selection.IsSelected(result, rows[r + 1], index);
        var left = _selection.IsSelected(result, row, index - 1);
        var right = _selection.IsSelected(result, row, index + 1);

        // Border on outer edges only. Each side's border + padding sums to 1px so content never shifts:
        // a drawn edge is 1px border / 0 padding, a shared (undrawn) edge is 0 border / 1px padding.
        double bl = left ? 0 : 1, bt = up ? 0 : 1, br = right ? 0 : 1, bb = down ? 0 : 1;
        border.Background = Tint("Syntax.Func", 0x2A);
        border.BorderBrush = Res("Syntax.Func");
        border.BorderThickness = new Thickness(bl, bt, br, bb);
        border.Padding = new Thickness(1 - bl, 1 - bt, 1 - br, 1 - bb);
        // Round only a lone cell's corners; a cell inside a block stays square so the edges abut cleanly.
        border.CornerRadius = new CornerRadius(up || down || left || right ? 0 : 2);
    }
}
