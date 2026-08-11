using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Controls;
using Bearing.App.Services;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// Confirmation shown before a write runs: a risky batch on a guarded connection, or any inline-edit save.
/// Names the target connection (in its environment color), says what the write does, and — the point of it —
/// <b>lists the statements about to run</b> (<see cref="SqlStatementList"/>), so "am I about to nuke prod" is
/// answerable without leaving the dialog. This replaced the separate manual [Preview SQL] step.
/// Returns true (proceed) or false (cancel) via <c>ShowDialog&lt;bool&gt;</c>. Code-built — no bindings.
/// </summary>
public sealed class ConfirmWriteDialog : Window
{
    public ConfirmWriteDialog(WriteConfirmation request)
    {
        Title = request.Title;
        Width = 700;
        SizeToContent = SizeToContent.Height;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var accent = new SolidColorBrush(Theming.ConnectionColors.Resolve(request.Connection.EnvironmentColor));

        var layout = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        layout.Children.Add(new TextBlock
        {
            Text = request.Heading,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = accent,
            TextWrapping = TextWrapping.Wrap,
        });
        layout.Children.Add(new TextBlock
        {
            Text = request.Summary,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });
        if (request.Warning is { } warning)
            layout.Children.Add(new TextBlock
            {
                Text = warning,
                Foreground = Res("Warn.Amber"),
                TextWrapping = TextWrapping.Wrap,
            });

        // The statement list is the only part that can grow: cap it so a migration-sized batch still leaves
        // the heading and the buttons on screen (SizeToContent.Height sizes the window to whatever it needs).
        var statements = SqlStatementList.Build(request);
        statements.MaxHeight = 340;
        statements.Margin = new Thickness(0, 4, 0, 0);
        layout.Children.Add(statements);

        // Enter commits an ordinary save (the dialog is on the path to the button the user just clicked), but
        // NOT on a connection whose whole point is that writes must be deliberate — there, Enter cancels and
        // proceeding takes a click. Dismissing by the title bar returns default(bool) = cancel either way.
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = request.IsGuarded };
        cancel.Click += (_, _) => Close(false);
        var proceed = new Button
        {
            Content = request.ConfirmLabel,
            IsDefault = !request.IsGuarded,
            Margin = new Thickness(8, 0, 0, 0),
        };
        if (request.Action == WriteAction.SaveEdits)
        {
            // Reads as a continuation of the ✓ Save button that opened it (Controls/ResultEditToolbar).
            proceed.Background = Res("Ok.Green");
            proceed.Foreground = Res("Bg.Editor");
        }
        proceed.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(proceed);
        layout.Children.Add(buttons);

        Content = layout;
    }
}
