using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.Persistence;

namespace Bearing.App.Views;

/// <summary>
/// Dismissable "something went wrong" dialog shown for unexpected errors caught by the global handlers.
/// Shows a friendly line plus expandable technical details, and offers to copy them. The full error is
/// always written to <see cref="CrashLog"/> regardless. One dialog at a time so a repeating fault (e.g.
/// a render-loop exception) can't stack hundreds of windows.
/// </summary>
public sealed class ErrorDialog : Window
{
    private static bool _open;

    private ErrorDialog(string context, Exception ex)
    {
        Title = "Bearing — unexpected error";
        Width = 560;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        var details = $"Context: {context}\n\n{ex}";

        var headline = new TextBlock
        {
            Text = "Something went wrong.",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        var sub = new TextBlock
        {
            Text = $"Details were saved to:\n{CrashLog.Path}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
        };
        var scroller = new ScrollViewer
        {
            Content = detailBox,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        // Own column: this holds a stack trace someone is reading to the end, and an overlay bar covers the
        // right of every line it crosses.
        ScrollViewer.SetAllowAutoHide(scroller, false);

        var copy = new Button { Content = "Copy details" };
        copy.Click += async (_, _) =>
        {
            try { if (Clipboard is { } cb) await cb.SetTextAsync(details); } catch { /* clipboard is best-effort */ }
        };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { copy, close },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(16) };
        Grid.SetRow(headline, 0);
        Grid.SetRow(sub, 1);
        Grid.SetRow(scroller, 2);
        Grid.SetRow(buttons, 3);
        scroller.Margin = new Thickness(0, 12, 0, 0);
        grid.Children.Add(headline);
        grid.Children.Add(sub);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);
        Content = grid;
    }

    /// <summary>Show the dialog (owned by <paramref name="owner"/> when available), unless one is already up.</summary>
    public static void Show(Window? owner, string context, Exception ex)
    {
        if (_open) return;
        _open = true;
        var dlg = new ErrorDialog(context, ex);
        dlg.Closed += (_, _) => _open = false;
        if (owner is not null && owner.IsVisible) dlg.Show(owner);
        else dlg.Show();
    }
}
