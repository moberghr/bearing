using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bearing.App.Views;

/// <summary>
/// Asked before a run, a zone's ✕ or a tab close would throw away unsaved grid edits (spec
/// docs/specs/2026-11-06-inline-results.md §7). Code-built, matching <see cref="ConfirmCancelRunningDialog"/>.
/// <para>
/// "Cancel" is both <c>IsCancel</c> and <c>IsDefault</c>: Esc, Enter and a title-bar dismissal all keep the
/// edits — the only way to lose them is to press the button that says so.
/// </para>
/// </summary>
public sealed class ConfirmDiscardEditsDialog : Window
{
    /// <param name="what">The loss, e.g. "3 unsaved changes in 2 results".</param>
    /// <param name="action">What the user asked for, e.g. "run", "close these results".</param>
    public ConfirmDiscardEditsDialog(string what, string action)
    {
        Title = "Unsaved edits";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = $"{char.ToUpperInvariant(what[0])}{what[1..]} would be lost.",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBlock
        {
            // One sentence true for every caller (a run, ✕, the placement toggle, closing a tab, quitting): what
            // the button does, not what happens to the results. No gesture: grid.save is rebindable.
            Text = $"\u201CDiscard and {action}\u201D throws them away. Cancel keeps them, so you can save them first.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var keep = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        keep.Click += (_, _) => Close(false);
        var discard = new Button { Content = $"Discard and {action}", Margin = new Thickness(8, 0, 0, 0) };
        discard.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(keep);
        buttons.Children.Add(discard);

        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        layout.Children.Add(buttons);
        Content = layout;
    }
}
