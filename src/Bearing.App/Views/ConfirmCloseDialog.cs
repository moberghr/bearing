using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;

namespace Bearing.App.Views;

/// <summary>
/// Asked before closing a tab that holds unsaved work. Three outcomes rather than two — Save, Don't
/// save, Cancel — returned via <c>ShowDialog&lt;CloseChoice&gt;</c>. Code-built, matching
/// <see cref="ConfirmWriteDialog"/>; a one-off with no bindings.
/// <para>
/// Esc maps to Cancel via <c>IsCancel</c>, and a title-bar dismissal returns <c>default</c>, which is
/// <see cref="CloseChoice.Cancel"/> by design — dismissing this dialog any way at all must never be read
/// as permission to discard.
/// </para>
/// </summary>
public sealed class ConfirmCloseDialog : Window
{
    public ConfirmCloseDialog(string tabName)
    {
        Title = "Unsaved changes";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = $"Save changes to {tabName}?",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = "This tab has unsaved work. If you don't save, it will be lost.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(CloseChoice.Cancel);
        var discard = new Button { Content = "Don't save", Margin = new Thickness(8, 0, 0, 0) };
        discard.Click += (_, _) => Close(CloseChoice.Discard);
        var save = new Button { Content = "Save", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Close(CloseChoice.Save);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(discard);
        buttons.Children.Add(save);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
