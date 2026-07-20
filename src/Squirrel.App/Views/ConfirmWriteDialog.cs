using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Squirrel.Core.Data;

namespace Squirrel.App.Views;

/// <summary>
/// Confirmation shown before a write / destructive-DDL batch runs against a guarded connection.
/// Names the target connection (in its environment color) and the risky verbs. Returns true (proceed)
/// or false (cancel) via <c>ShowDialog&lt;bool&gt;</c>. Code-built — a one-off with no bindings.
/// </summary>
public sealed class ConfirmWriteDialog : Window
{
    public ConfirmWriteDialog(ConnectionInfo connection, IReadOnlyList<string> verbs)
    {
        Title = "Confirm write";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var accent = new SolidColorBrush(Theming.ConnectionColors.Resolve(connection.EnvironmentColor));
        var env = string.IsNullOrWhiteSpace(connection.Environment) ? "" : $" · {connection.Environment}";

        var heading = new TextBlock
        {
            Text = $"Run on {connection.Name}{env}?",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = accent,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = $"This batch contains {string.Join(", ", verbs)} — it will modify data or schema on this connection.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var run = new Button { Content = "Run anyway", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        run.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(run);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
