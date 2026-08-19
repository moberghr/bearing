using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;

namespace Bearing.App.Views;

/// <summary>
/// Asked before a project is removed. Three outcomes — forget it, delete its folder, cancel — returned via
/// <c>ShowDialog&lt;ProjectRemoval&gt;</c>. Code-built, matching <see cref="ConfirmCloseDialog"/>.
/// <para>
/// The two removals are one dialog rather than two menu items because the difference is the whole question,
/// and it is only answerable next to the actual path. "Delete folder" is deliberately <em>not</em> the
/// default button: Enter and a title-bar dismissal both land on
/// <see cref="ProjectRemoval.Cancel"/> (the zero value), so no keystroke deletes anything by itself.
/// </para>
/// </summary>
public sealed class ConfirmRemoveProjectDialog : Window
{
    public ConfirmRemoveProjectDialog(string name, string directory)
    {
        Title = "Remove project";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = $"Remove '{name}'?",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = "Removing it from the list leaves every file where it is — the project is only forgotten, "
                 + "and you can open it again from its folder. Deleting the folder takes its scripts, scratch "
                 + "buffers and session with it and cannot be undone.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // The path is what a delete actually removes, so it is shown verbatim rather than summarised.
        var path = new TextBlock
        {
            Text = directory,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close(ProjectRemoval.Cancel);
        var fromList = new Button { Content = "Remove from list", Margin = new Thickness(8, 0, 0, 0) };
        fromList.Click += (_, _) => Close(ProjectRemoval.FromList);
        var fromDisk = new Button { Content = "Delete folder", Margin = new Thickness(8, 0, 0, 0) };
        fromDisk.Click += (_, _) => Close(ProjectRemoval.FromDisk);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(fromList);
        buttons.Children.Add(fromDisk);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(path);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
