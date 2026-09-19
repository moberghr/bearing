using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Views;

/// <summary>
/// Asked before an action that would end an uncommitted transaction — quitting, disconnecting, closing the
/// tab holding one, or anything else that tears the session down (#131). Code-built, matching
/// <see cref="ConfirmCancelRunningDialog"/>, whose shape this is: the same question about a different thing
/// in flight.
/// <para>
/// "Keep it open" is <c>IsCancel</c> <b>and</b> <c>IsDefault</c> — the treatment §9.13 gives
/// <see cref="ConfirmBackendActionDialog"/>, so that no single keystroke throws away uncommitted work. Esc,
/// Enter and a title-bar dismissal all return <c>false</c> and the transaction lives. That is a keystroke
/// more than <see cref="ConfirmCancelRunningDialog"/> asks for, and deliberately: a cancelled query can be
/// re-run, and this cannot be undone at all.
/// </para>
/// <para>
/// The proceed button says <b>roll back</b>, never "discard": that is the statement the server will actually
/// run, and a button that named the outcome loosely would be the one place in this feature where the word on
/// screen and the SQL disagree.
/// </para>
/// </summary>
public sealed class ConfirmDiscardTransactionDialog : Window
{
    /// <param name="count">How many uncommitted transactions the action would end.</param>
    /// <param name="action">What the user asked for, lower-case and in their words — "quit",
    /// "disconnect from prod", "close Report.sql". It completes the heading.</param>
    public ConfirmDiscardTransactionDialog(int count, string action)
    {
        Title = count == 1 ? "Uncommitted transaction" : "Uncommitted transactions";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = count == 1
                ? "A transaction has not been committed."
                : $"{count} transactions have not been committed.",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = count == 1
                ? $"To {action}, it has to be rolled back. Everything it has written is lost."
                : $"To {action}, they have to be rolled back. Everything they have written is lost.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var keep = new Button { Content = "Keep it open", IsCancel = true, IsDefault = true };
        keep.Click += (_, _) => Close(false);
        var proceed = new Button
        {
            Content = count == 1 ? "Roll back and continue" : "Roll back all and continue",
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
