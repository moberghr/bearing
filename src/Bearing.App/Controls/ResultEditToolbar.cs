using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.ViewModels;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Controls;

/// <summary>
/// The right-hand side of an editable result's meta row: the always-visible ＋ Add / Delete / ⭳ Export
/// actions, plus a commit group (● N pending · Script · Discard · Save) that reveals itself only while there
/// are unsaved changes — bound to <see cref="ResultSetViewModel.HasPendingChanges"/>, so it tracks edits made
/// anywhere (a cell commit, a checkbox toggle, a keyboard delete) without this toolbar being told.
/// </summary>
public static class ResultEditToolbar
{
    /// <summary>Build the toolbar for <paramref name="result"/>. The callbacks are already scoped to it.</summary>
    public static Control Build(
        ResultSetViewModel result,
        DataGrid grid,
        Action onPreviewSql,
        Func<Task> onSave,
        Func<Task> onDiscard)
    {
        var add = ResultChrome.SubtleButton("＋ Add", "Add row");
        add.Click += (_, _) =>
        {
            var row = result.AddRow();
            grid.ScrollIntoView(row, null);
            ResultRowPainter.RefreshRowColors(grid, result);
        };

        var delete = ResultChrome.SubtleButton("Delete", "Delete selected row");
        delete.Click += (_, _) =>
        {
            if (grid.SelectedItem is object?[] row)
            {
                result.ToggleDelete(row);
                ResultRowPainter.RefreshRowColors(grid, result);
            }
        };

        var export = ResultChrome.SubtleButton("⭳ Export", "Export — coming soon"); // rendered; wired later (per decision)

        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(add);
        bar.Children.Add(delete);
        bar.Children.Add(export);
        bar.Children.Add(PendingGroup(result, onPreviewSql, onSave, onDiscard));
        return bar;
    }

    /// <summary>● N pending · ‹ › Script · Discard (red outline) · ✓ Save (green fill) — visible only while
    /// the result has pending changes.</summary>
    private static Control PendingGroup(
        ResultSetViewModel result, Action onPreviewSql, Func<Task> onSave, Func<Task> onDiscard)
    {
        var dot = new TextBlock
        {
            Text = "●",
            Foreground = Res("Accent.Brand"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 4, 0),
        };
        var pending = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = Res("Text.Primary"),
        };
        pending.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.PendingText)));

        var script = ResultChrome.SubtleButton("‹ › Script", "Preview the SQL a save would run");
        script.Click += (_, _) => onPreviewSql();

        var discard = new Button
        {
            Content = "Discard",
            FontSize = 12,
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            BorderBrush = Res("Error.Red"),
            BorderThickness = new Thickness(1),
            Foreground = Res("Error.Red"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        discard.Click += async (_, _) => await onDiscard();

        var save = new Button
        {
            Content = "✓ Save",
            FontSize = 12,
            Padding = new Thickness(8, 2),
            Background = Res("Ok.Green"),
            Foreground = Res("Bg.Editor"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        save.Click += async (_, _) => await onSave();

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            DataContext = result,
        };
        group.Children.Add(dot);
        group.Children.Add(pending);
        group.Children.Add(script);
        group.Children.Add(discard);
        group.Children.Add(save);
        group.Bind(Visual.IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasPendingChanges)));
        return group;
    }
}
