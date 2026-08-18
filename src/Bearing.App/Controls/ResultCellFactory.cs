using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    // Long-text/array/json columns start capped so they show partially, but stay freely resizable
    // (no MaxWidth) and can be double-clicked (on the header) to auto-fit.
    private const double WideColumnInitial = 280;

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

    /// <summary>The column for <paramref name="index"/>, picked by column kind, with its header and (for a
    /// wide type) a capped initial width.</summary>
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
        if (ColumnKinds.IsWide(result.Columns[index])) col.Width = new DataGridLength(WideColumnInitial);
        return col;
    }

    /// <summary>Column header = the name plus inline type badges: teal PK, violet FK, mint jsonb/json
    /// (design RESULTS_GRID §3).</summary>
    private static Control ColumnHeader(ResultSetViewModel result, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = result.Columns[index].Name, VerticalAlignment = VerticalAlignment.Center });

        if (result.PrimaryKeyColumns.Contains(index)) row.Children.Add(ResultChrome.Badge("PK", "Accent.Brand"));
        if (result.ForeignKeyColumns.Contains(index)) row.Children.Add(ResultChrome.Badge("FK", "Syntax.Keyword"));

        var type = result.Columns[index].DataTypeName;
        if (ColumnKinds.IsJson(type)) row.Children.Add(ResultChrome.Badge(type.ToLowerInvariant(), "Syntax.Table"));

        return row;
    }

    /// <summary>A value display cell: text (dimmed italic "(null)", numeric in code color), plus an
    /// inspect (⤢) affordance for jsonb/json and any long/multiline value. Every value cell is
    /// selectable (single/drag/modifier-click); numeric selections drive the quick-stats bar.</summary>
    private IDataTemplate ValueCell(ResultSetViewModel result, int index, DataGrid grid)
    {
        var isJsonCol = ColumnKinds.IsJson(result.Columns[index].DataTypeName);
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
            Margin = new Thickness(4, 0),
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
            Margin = new Thickness(4, 0, 4, 0),
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

    /// <summary>A boolean cell rendered as a checkbox: read-only display when the grid is locked,
    /// interactive (toggles the row value + marks it edited) when the result is editable. Selectable like
    /// every other cell (#9) — clicking the box both toggles it and moves the selection here, so a following
    /// Ctrl+C / Delete acts on the row the user just pointed at instead of wherever the cursor used to be.
    /// <para>
    /// The one difference from a value cell: a press here does not arm a drag-rectangle, because taking the
    /// pointer capture for the grid would rob the CheckBox of the release that toggles it. Drag from a
    /// neighbouring cell to sweep across checkbox columns.
    /// </para></summary>
    private IDataTemplate BoolCell(ResultSetViewModel result, int index, DataGrid grid)
        => new FuncDataTemplate<object?[]>((row, _) =>
            MakeSelectable(() => BoolContent(result, index, row), result, row, index, grid, armDrag: false));

    /// <summary>The checkbox itself. Value-dependent like the other cell contents, which is how a paste or the
    /// keyboard toggle shows up — <c>IsChecked</c> is seeded here and nothing rebinds it.</summary>
    private static Control BoolContent(ResultSetViewModel result, int index, object?[]? row)
    {
        var cb = new CheckBox
        {
            // Three-state only where NULL is a legal value; a NOT NULL column clicks false ⇄ true. It can
            // still *show* an indeterminate box: IsThreeState governs the click cycle, not the display, and a
            // pending new row's cells start out null.
            IsThreeState = result.AllowsNull(index),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = BoolCellValue.Read(row, index),
        };
        if (!result.IsEditable)
        {
            cb.IsHitTestVisible = false; // display only (not greyed like IsEnabled=false)
            cb.Focusable = false;
            return cb;
        }
        cb.IsCheckedChanged += (_, _) =>
        {
            if (row is null) return;
            if (!result.SetCell(row, index, cb.IsChecked)) return; // unchanged / out of range (e.g. initial bind)
            if (cb.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault() is { } dgr)
                ResultRowPainter.ApplyRowStatus(dgr, result);
        };
        return cb;
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
    /// <param name="armDrag">False for a cell whose content needs the pointer capture itself (a checkbox):
    /// the click still selects, it just doesn't start a drag-rectangle.</param>
    private Control MakeSelectable(
        Func<Control> content, ResultSetViewModel result, object?[]? row, int index, DataGrid grid,
        bool armDrag = true)
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
                grid.Focus();
                if (!_selection.IsSelected(result, row, index)) _selection.SelectSingle(result, row, index);
                return;
            }
            if (!point.IsLeftButtonPressed) return;
            grid.Focus(); // route subsequent key presses to this grid's keyboard handler

            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (shift && _selection.CanExtendFrom(result)) _selection.ExtendTo(result, row, index);
            else if (ctrl) _selection.ToggleCell(result, row, index);
            else if (armDrag) _selection.SelectSingleAndBeginDrag(result, row, index, e.Pointer, grid);
            else _selection.SelectSingle(result, row, index);
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
