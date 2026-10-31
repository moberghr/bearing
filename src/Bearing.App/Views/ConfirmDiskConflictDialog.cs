using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;

namespace Bearing.App.Views;

/// <summary>
/// Asked when a script changed on disk while its tab held unsaved edits — the one case where reloading or
/// saving would each silently lose someone's work. Code-built like <see cref="ConfirmCloseDialog"/>.
/// <para>
/// "Decide later" is both <c>IsCancel</c> and <c>IsDefault</c>, and a title-bar dismissal returns
/// <c>default</c>, which is <see cref="DiskConflictChoice.Decide"/>: Enter, Esc or closing the window keeps
/// both copies and writes nothing.
/// </para>
/// </summary>
public sealed class ConfirmDiskConflictDialog : Window
{
    public ConfirmDiskConflictDialog(string fileName)
    {
        Title = "File changed on disk";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = $"{fileName} changed on disk",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = "Something outside Bearing changed this file, and its tab has edits that aren't saved. "
                   + "Reloading throws away the tab's edits; overwriting throws away the other change. "
                   + "Until you choose, Bearing won't write the file.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var later = new Button { Content = "Decide later", IsCancel = true, IsDefault = true };
        later.Click += (_, _) => Close(DiskConflictChoice.Decide);
        var reload = new Button { Content = "Reload from disk", Margin = new Thickness(8, 0, 0, 0) };
        reload.Click += (_, _) => Close(DiskConflictChoice.Reload);
        var overwrite = new Button { Content = "Overwrite file", Margin = new Thickness(8, 0, 0, 0) };
        overwrite.Click += (_, _) => Close(DiskConflictChoice.Overwrite);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(later);
        buttons.Children.Add(reload);
        buttons.Children.Add(overwrite);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
