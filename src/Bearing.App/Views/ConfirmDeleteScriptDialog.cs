using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Views;

/// <summary>
/// Asked before a script file is deleted. Two outcomes, returned via <c>ShowDialog&lt;bool&gt;</c>.
/// Code-built, matching <see cref="ConfirmCloseDialog"/>.
/// <para>
/// Cancel is the default button and <c>IsCancel</c>, so Enter, Esc and a title-bar dismissal all decline —
/// the same rule as <see cref="ConfirmRemoveProjectDialog"/>: no single keystroke deletes anything.
/// </para>
/// </summary>
public sealed class ConfirmDeleteScriptDialog : Window
{
    public ConfirmDeleteScriptDialog(string fileName)
    {
        Title = "Delete script";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = $"Delete {fileName}?",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = "The file is removed from disk and its tab closes. This cannot be undone — unless the "
                 + "project folder is under version control, where the file is still in your history.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close(false);
        var delete = new Button { Content = "Delete", Margin = new Thickness(8, 0, 0, 0) };
        delete.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(delete);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
