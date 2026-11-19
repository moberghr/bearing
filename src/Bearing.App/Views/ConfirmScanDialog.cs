using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// Confirmation shown before a read that may cover a whole DynamoDB table (<i>Behaviour 8</i>), and again before
/// Fetch all / Export on a result confirmed as one. Names the connection (in its environment colour), then the
/// <see cref="ScanQuestion.Text"/>: each statement's table (and index), the approximate item count when known, that
/// rows load a page at a time, and that this check stops mistakes while the boundary is an IAM policy.
/// <para>
/// <b>Cancel is the default and Esc</b> (<c>IsCancel</c> and <c>IsDefault</c>), and runs nothing — Enter must not
/// be the way into a scan. Dismissing by the title bar returns <c>default(bool)</c> = cancel. Code-built — no
/// bindings, modelled on <see cref="ConfirmWriteDialog"/>.
/// </para>
/// </summary>
public sealed class ConfirmScanDialog : Window
{
    public ConfirmScanDialog(ScanQuestion request)
    {
        Title = request.Title;
        Width = 620;
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
            Text = request.Text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Text.Primary"),
        });

        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close(false);
        var proceed = new Button { Content = request.ConfirmLabel, Margin = new Thickness(8, 0, 0, 0) };
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
