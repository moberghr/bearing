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
/// The right-hand side of an editable result's meta row: the always-visible ＋ Add / Delete rows actions, plus
/// a commit group (● N pending · Discard · Save) that reveals itself only while there are unsaved changes.
/// Every one of the four is also a keyboard command in the grid scope (grid.addRow / grid.delete / grid.save /
/// grid.discard, #11) — which is what the tooltips advertise, and which is why each is a callback the host
/// hands in rather than something this control works out from grid state (#32). The commit group is bound to
/// <see cref="ResultSetViewModel.HasPendingChanges"/>, so it tracks edits made anywhere (a cell commit, a
/// checkbox toggle, a keyboard delete) without this toolbar being told.
/// <para>There is no Script/preview button: Save now shows the generated DML in its confirmation, so the
/// preview is on the path to committing rather than a step the user had to remember to take. Export moved
/// out to <see cref="ResultExportButton"/> — it applies to read-only results too, which never render this.</para>
/// </summary>
public static class ResultEditToolbar
{
    /// <summary>Build the toolbar for <paramref name="result"/>. The callbacks are already scoped to it.</summary>
    /// <param name="onAddRow">Adding a row also moves the cell cursor onto it, which is the host's business
    /// (it owns the selection) — and it is the same action grid.addRow runs, so it lives in one place.</param>
    /// <param name="onDelete">Same reason, and the same action grid.delete runs: the rows to mark are the ones
    /// owning a selected cell, which only the host's selection controller knows. This button used to read
    /// <c>grid.SelectedItem</c> itself — a property cell selection deliberately never sets (#32), so it did
    /// nothing until an edit had happened to set it as a side effect.</param>
    public static Control Build(
        ResultSetViewModel result,
        Action onAddRow,
        Action onDelete,
        Func<Task> onSave,
        Func<Task> onDiscard)
    {
        var add = ResultChrome.SubtleButton("＋ Add", "Add row (Alt+Insert)");
        add.Click += (_, _) => onAddRow();

        // "rows", plural: the action marks every row the selection touches, not the one row the old label implied.
        var delete = ResultChrome.SubtleButton("Delete rows", "Mark the selected rows for deletion (Delete)");
        delete.Click += (_, _) => onDelete();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(add);
        bar.Children.Add(delete);
        bar.Children.Add(PendingGroup(result, onSave, onDiscard));
        return bar;
    }

    /// <summary>● N pending · Discard (red outline) · ✓ Save (green fill) — visible only while the result has
    /// pending changes.</summary>
    private static Control PendingGroup(ResultSetViewModel result, Func<Task> onSave, Func<Task> onDiscard)
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
            FontSize = Metric("Font.Body"),
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = Res("Text.Primary"),
        };
        pending.Bind(TextBlock.TextProperty, new Binding(nameof(ResultSetViewModel.PendingText)));

        var discard = new Button
        {
            Content = "Discard",
            FontSize = Metric("Font.Body"),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            BorderBrush = Res("Error.Red"),
            BorderThickness = new Thickness(1),
            Foreground = Res("Error.Red"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(discard, "Discard pending changes (Ctrl+Alt+Z)");
        discard.Click += async (_, _) => await onDiscard();

        var save = new Button
        {
            Content = "✓ Save",
            FontSize = Metric("Font.Body"),
            Padding = new Thickness(8, 2),
            Background = Res("Ok.Green"),
            Foreground = Res("Bg.Editor"),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(save, "Save pending changes (Ctrl+S)");
        save.Click += async (_, _) => await onSave();

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            DataContext = result,
        };
        group.Children.Add(dot);
        group.Children.Add(pending);
        group.Children.Add(discard);
        group.Children.Add(save);
        group.Bind(Visual.IsVisibleProperty, new Binding(nameof(ResultSetViewModel.HasPendingChanges)));
        return group;
    }
}
