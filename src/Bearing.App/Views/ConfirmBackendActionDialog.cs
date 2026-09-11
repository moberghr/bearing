using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Services;

namespace Bearing.App.Views;

/// <summary>
/// Asked before a backend is cancelled or terminated (#101). Two outcomes, returned via
/// <c>ShowDialog&lt;bool&gt;</c>. Code-built, matching <see cref="ConfirmDeleteScriptDialog"/>.
/// <para>
/// Cancel is the default button and <c>IsCancel</c>, so Enter, Esc and a title-bar dismissal all decline. The
/// same rule as the delete dialogs, and for a stronger reason: the session on the other end of this belongs
/// to someone who is not looking at this screen.
/// </para>
/// <para>
/// Every word comes from <see cref="BackendAction"/>, so what the dialog says is testable without a window.
/// </para>
/// </summary>
public sealed class ConfirmBackendActionDialog : Window
{
    public ConfirmBackendActionDialog(BackendAction request)
    {
        Title = request.Title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(18) };

        layout.Children.Add(new TextBlock
        {
            Text = request.Heading,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        // Who it belongs to, before what will happen to it: "am I about to kill the right one" is the
        // question being answered, and a pid alone has nothing in it to recognise.
        layout.Children.Add(new TextBlock
        {
            Text = request.Target,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        if (request.Running is { } running)
            layout.Children.Add(new TextBlock
            {
                Text = running,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 0),
            });

        layout.Children.Add(new TextBlock
        {
            Text = request.Summary,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        });

        if (request.Query is { } query)
            layout.Children.Add(new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Controls.Tokens.SeparatorBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new ScrollViewer
                {
                    MaxHeight = 160,
                    Content = new SelectableTextBlock
                    {
                        Text = query,
                        FontFamily = Controls.Tokens.MonoFont,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            });

        var cancel = new Button { Content = "Leave it", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => Close(false);
        var proceed = new Button { Content = request.ConfirmLabel, Margin = new Thickness(8, 0, 0, 0) };
        proceed.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(proceed);
        layout.Children.Add(buttons);

        Content = layout;
    }
}
