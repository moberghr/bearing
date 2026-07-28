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
    /// <summary>A value display cell: text (dimmed italic "(null)", numeric in code color), plus an
    /// inspect (⤢) affordance for jsonb/json and any long/multiline value. Every value cell is
    /// selectable (single/drag/modifier-click); numeric selections drive the quick-stats bar.</summary>
    private IDataTemplate ValueCell(ResultSetViewModel result, int index, DataGrid grid)
    {
        var isJsonCol = IsJsonType(result.Columns[index].DataTypeName);
        var numeric = CellStats.IsNumeric(result.Columns[index].ClrType);
        return new FuncDataTemplate<object?[]>((row, _) =>
        {
            var isNull = row is null || index >= row.Length || row[index] is null;
            var text = new TextBlock
            {
                Text = CellText(row, index),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = isNull ? NullBrush : (numeric ? Res("Text.Code") : Res("Text.Primary")),
                FontStyle = isNull ? FontStyle.Italic : FontStyle.Normal,
            };

            Control inner = text;
            if (!isNull)
            {
                var raw = CellText(row, index);
                if (isJsonCol || raw.Length > 60 || raw.Contains('\n'))
                {
                    var expand = InspectAffordance();
                    // handledEventsToo: the DataGrid marks the press handled in the tunnel phase.
                    expand.AddHandler(PointerPressedEvent, (_, e) => { ShowInspector(result, index, row!); e.Handled = true; },
                        RoutingStrategies.Bubble, handledEventsToo: true);
                    var dock = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; // keep ⤢ clear of the scrollbar
                    DockPanel.SetDock(expand, Dock.Right);
                    dock.Children.Add(expand);
                    dock.Children.Add(text);
                    inner = dock;
                }
            }
            return MakeSelectable(inner, result, row, index, grid);
        });
    }

    /// <summary>Wrap a cell's content in a selectable border: single-click selects (blue ring) and
    /// starts a drag rectangle, Ctrl/Cmd/Shift-click toggles; the whole-row highlight stays invisible.
    /// Numeric selections feed the quick-stats bar; text selections just highlight.</summary>
    private Control MakeSelectable(Control inner, ResultSetViewModel result, object?[]? row, int index, DataGrid grid)
    {
        var border = new Border
        {
            Child = inner,
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

        void Restyle()
        {
            var selected = ReferenceEquals(_sel.Result, result) && _sel.Cells.Contains((row, index));
            if (!selected)
            {
                border.Background = Brushes.Transparent;
                border.BorderThickness = new Thickness(0);
                border.Padding = new Thickness(1); // full reserve → content stays put
                border.CornerRadius = new CornerRadius(2);
                return;
            }

            // Merge adjacent selected cells: fill them all, but only stroke the block's outer edges —
            // an edge shared with another selected cell gets no border, so the selection reads as one
            // contiguous region instead of a grid of individually-ringed cells.
            var rows = result.Rows;
            var r = rows.IndexOf(row);
            var up    = r > 0 && _sel.Cells.Contains((rows[r - 1], index));
            var down   = r >= 0 && r + 1 < rows.Count && _sel.Cells.Contains((rows[r + 1], index));
            var left  = _sel.Cells.Contains((row, index - 1));
            var right = _sel.Cells.Contains((row, index + 1));

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
        Restyle();
        _sel.CellRestyle += Restyle;
        border.DetachedFromVisualTree += (_, _) => _sel.CellRestyle -= Restyle;

        border.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount >= 2) return; // let the grid start editing on double-click
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            grid.Focus(); // route subsequent key presses to this grid's keyboard handler
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (shift && _sel.Anchor is { } anchor && ReferenceEquals(_sel.Result, result))
            {
                // Shift-click: rectangular range from the existing anchor to the clicked cell.
                SelectRectangle(result, anchor, (row, index));
                _sel.Active = (row, index);
            }
            else if (ctrl)
            {
                ToggleCellSelection(result, row, index, extend: true);
                _sel.Active = (row, index);
                _sel.Anchor = (row, index);
            }
            else
            {
                _sel.Result = result;
                _sel.Cells.Clear();
                _sel.Cells.Add((row, index));
                _sel.Active = (row, index);
                _sel.Anchor = (row, index);
                _sel.Dragging = true;
                _sel.DragAnchor = (row, index);
                e.Pointer.Capture(grid);
                SelectionChanged();
            }
            e.Handled = true;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        return border;
    }

    private static bool IsBoolColumn(Squirrel.Core.Data.ColumnDescriptor c)
        => (Nullable.GetUnderlyingType(c.ClrType) ?? c.ClrType) == typeof(bool);

    /// <summary>A boolean cell rendered as a checkbox: read-only display when the grid is locked,
    /// interactive (toggles the row value + marks it edited) when the result is editable.</summary>
    private IDataTemplate BoolCell(ResultSetViewModel result, int index)
        => new FuncDataTemplate<object?[]>((row, _) =>
        {
            var cb = new CheckBox
            {
                IsThreeState = true, // null → indeterminate
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = ToBool(row, index),
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
                    ApplyRowStatus(dgr, result);
            };
            return cb;
        });

    private static bool? ToBool(object?[]? row, int index)
    {
        if (row is null || index >= row.Length) return null;
        return row[index] switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };
    }

    private static bool IsJsonType(string dataTypeName)
        => string.Equals(dataTypeName, "jsonb", StringComparison.OrdinalIgnoreCase)
        || string.Equals(dataTypeName, "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>A small drawn "⤢" inspect icon (vector, not a glyph) that opens the cell inspector.</summary>
    private static Control InspectAffordance()
    {
        var arrow = new Path
        {
            Data = Geometry.Parse("M1,6 V1 H6 M1,1 L6,6 M13,8 V13 H8 M13,13 L8,8"),
            Stroke = Res("Syntax.Table"),
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            Child = arrow,
            Background = Brushes.Transparent,
            Width = 18,
            Height = 16,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(box, "Inspect value");
        return box;
    }

    /// <summary>Column header = the name plus inline type badges: orange PK, purple FK, teal jsonb/json
    /// (design RESULTS_GRID §3). Badges are 9px/700 tinted chips after the name.</summary>
    private static Control BuildColumnHeader(ResultSetViewModel result, int index)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = result.Columns[index].Name, VerticalAlignment = VerticalAlignment.Center });

        if (result.PrimaryKeyColumns.Contains(index)) row.Children.Add(Badge("PK", "Accent.Orange"));
        if (result.ForeignKeyColumns.Contains(index)) row.Children.Add(Badge("FK", "Syntax.Keyword"));

        var type = result.Columns[index].DataTypeName;
        if (string.Equals(type, "jsonb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "json", StringComparison.OrdinalIgnoreCase))
            row.Children.Add(Badge(type.ToLowerInvariant(), "Syntax.Table"));

        return row;
    }

    private static Control Badge(string text, string colorKey)
        => new Border
        {
            Background = Tint(colorKey, 0x33),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = Res(colorKey),
            },
        };

    /// <summary>The in-cell editor (a TextBox seeded with the current value) shared by editable and FK
    /// columns. A template — re-materialized per row as the grid recycles containers on scroll —
    /// deliberately NOT a <see cref="DataGridTextColumn"/> with an indexer binding (<c>[i]</c>): that
    /// binding doesn't re-evaluate when a row container is reused (Avalonia DataGrid recycling, #17534).</summary>
    private static IDataTemplate CellEditor(int index)
        => new FuncDataTemplate<object?[]>((row, _) =>
        {
            var box = new TextBox
            {
                Text = CellText(row, index),
                Padding = new Thickness(3, 1),
                BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            // Entering edit mode preselects the whole value (type-to-replace, like a spreadsheet).
            box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
            return box;
        });

    private static string CellText(object?[]? row, int index)
        => row is not null && index < row.Length ? CellFormat.Display(row[index]) : "";

    /// <summary>Subtle edit controls for the right of a result's meta row: ＋ Add / Delete / ⭳ Export
    /// (borderless), plus a pending commit group (● N pending · Script · Discard · Save) that appears
    /// only when there are unsaved changes.</summary>
    private Control EditControls(ResultSetViewModel result, DataGrid grid)
    {
        var add = SubtleButton("＋ Add", "Add row");
        add.Click += (_, _) => { var row = result.AddRow(); grid.ScrollIntoView(row, null); RefreshRowColors(grid, result); };

        var delete = SubtleButton("Delete", "Delete selected row");
        delete.Click += (_, _) => { if (grid.SelectedItem is object?[] row) { result.ToggleDelete(row); RefreshRowColors(grid, result); } };

        var export = SubtleButton("⭳ Export", "Export — coming soon"); // rendered; wired later (per decision)

        // Pending commit group: ● N pending · ‹ › Script · Discard (red outline) · ✓ Save (green fill).
        var dot = new TextBlock { Text = "●", Foreground = Res("Accent.Orange"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        var pending = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), Foreground = Res("Text.Primary") };
        pending.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.PendingText)));

        var script = SubtleButton("‹ › Script", "Preview the SQL a save would run");
        script.Click += (_, _) => PreviewSql?.Invoke(result);

        var discard = new Button
        {
            Content = "Discard", FontSize = 12, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent, BorderBrush = Res("Error.Red"), BorderThickness = new Thickness(1),
            Foreground = Res("Error.Red"), Cursor = new Cursor(StandardCursorType.Hand),
        };
        discard.Click += async (_, _) => { if (DiscardChanges is { } f) await f(result); };

        var save = new Button
        {
            Content = "✓ Save", FontSize = 12, Padding = new Thickness(8, 2),
            Background = Res("Ok.Green"), Foreground = Res("Bg.Editor"), Cursor = new Cursor(StandardCursorType.Hand),
        };
        save.Click += async (_, _) => { if (SaveChanges is { } f) await f(result); };

        var pendingGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, DataContext = result };
        pendingGroup.Children.Add(dot);
        pendingGroup.Children.Add(pending);
        pendingGroup.Children.Add(script);
        pendingGroup.Children.Add(discard);
        pendingGroup.Children.Add(save);
        pendingGroup.Bind(IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasPendingChanges)));

        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(add);
        bar.Children.Add(delete);
        bar.Children.Add(export);
        bar.Children.Add(pendingGroup);
        return bar;
    }

    /// <summary>A borderless, dim, hand-cursor button for subtle inline actions. Each space-separated
    /// token (icon glyph, word) is its own vertically-centered TextBlock so a tall icon glyph doesn't
    /// enlarge the label's line-box and knock the words out of alignment with icon-less buttons.</summary>
    private static Button SubtleButton(string content, string tip)
    {
        var tokens = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in content.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            tokens.Children.Add(new TextBlock { Text = t, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

        var b = new Button
        {
            Content = tokens,
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2),
            Foreground = Res("Text.Dim"),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    /// <summary>A foreign-key column: the value shows as plain text with a clickable jump-icon on the
    /// right that navigates to the referenced row. In an editable result the value is also editable
    /// (double-click / F2) via the shared editor; the jump icon stays on the display cell.</summary>
    private DataGridColumn ForeignKeyColumn(ResultSetViewModel result, int index, DataGrid grid)
        => new DataGridTemplateColumn
        {
            Tag = index, // enables CellEditEnding capture when the grid is editable
            CellEditingTemplate = result.IsEditable ? CellEditor(index) : null,
            CellTemplate = new FuncDataTemplate<object?[]>((row, _) =>
            {
                if (row is null) return new TextBlock();
                var hasValue = row.Length > index && row[index] is not null;

                var value = new TextBlock
                {
                    Text = CellText(row, index),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 0, 4, 0),
                };

                // A drawn "↗" (vector Path, not a font glyph — symbol glyphs render clipped in the
                // app font). Wrapped in a transparent Border so the whole 16×16 box is the hit target.
                var arrow = new Path
                {
                    Data = Geometry.Parse("M1,9 L9,1 M4,1 L9,1 L9,6"),
                    Stroke = LinkBrush,
                    StrokeThickness = 1.4,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var jump = new Border
                {
                    Child = arrow,
                    Background = Brushes.Transparent,
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(2, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    IsVisible = hasValue,
                };
                ToolTip.SetTip(jump, "Open referenced row");
                // handledEventsToo: the DataGrid marks the press handled in the tunnel phase.
                jump.AddHandler(PointerPressedEvent, async (_, e) =>
                {
                    e.Handled = true;
                    if (hasValue && NavigateForeignKey is { } nav) await nav(result, index, row);
                }, RoutingStrategies.Bubble, handledEventsToo: true);

                var cell = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; // keep ↗ clear of the scrollbar
                DockPanel.SetDock(jump, Dock.Right);
                cell.Children.Add(jump);
                cell.Children.Add(value);
                return MakeSelectable(cell, result, row, index, grid);
            }),
        };

}
