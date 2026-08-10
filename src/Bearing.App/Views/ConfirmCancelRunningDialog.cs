using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Views;

/// <summary>
/// Asked before an action that would abandon a query still in flight — quitting the app, or closing the
/// tab a query is running on. Code-built, matching <see cref="ConfirmCloseDialog"/>.
/// <para>
/// "Keep running" is <c>IsCancel</c>, so Esc and a title-bar dismissal both return <c>false</c> and the
/// query lives — dismissing this dialog can never be read as permission to kill work in progress. The
/// proceed button is <c>IsDefault</c>: the user asked to quit/close, so Enter confirms it.
/// </para>
/// </summary>
public sealed class ConfirmCancelRunningDialog : Window
{
    /// <param name="runningCount">How many tabs have a query in flight (used by the quit variant).</param>
    /// <param name="tabName">Tab header for the close-one-tab variant; null for the quit variant.</param>
    public ConfirmCancelRunningDialog(int runningCount, string? tabName)
    {
        var quitting = tabName is null;
        Title = quitting ? "Queries still running" : "Query still running";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = quitting
                ? runningCount == 1 ? "A query is still running." : $"{runningCount} queries are still running."
                : $"A query is still running on {tabName}.",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = quitting
                ? "Quitting cancels them. Any results they would have returned are lost."
                : "Closing this tab cancels it. Any results it would have returned are lost.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var keep = new Button { Content = "Keep running", IsCancel = true };
        keep.Click += (_, _) => Close(false);
        var proceed = new Button
        {
            Content = quitting ? "Cancel and quit" : "Cancel and close",
            IsDefault = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        proceed.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(keep);
        buttons.Children.Add(proceed);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
