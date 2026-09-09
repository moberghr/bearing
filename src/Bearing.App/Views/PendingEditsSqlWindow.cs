using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.App.Controls;
using Bearing.App.Services;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>
/// The DML behind a result grid's pending edits, on demand (#114).
/// <para>
/// Until now the generated statements were visible in one place only — the save confirmation — whose two
/// buttons commit or cancel, so there was no way to take the SQL anywhere. This shows the same text at any
/// point while a change is pending, with a route out of the app: the clipboard, or a new editor tab where it
/// can be reviewed, edited and run through the ordinary execution path (write guard included).
/// </para>
/// <para>
/// Its own window rather than another mode of <c>ResultView</c> (§0.4/§9.1), following
/// <see cref="ExplainPlanWindow"/>. The list itself is <see cref="SqlStatementList"/> — the very control the
/// confirmation dialog uses, handed the very same <see cref="WriteConfirmation"/> — so the two can't drift
/// into showing different SQL for the same pending state.
/// </para>
/// </summary>
public sealed class PendingEditsSqlWindow : Window
{
    /// <summary>
    /// Shows the pending DML for <paramref name="request"/>. Closing with <c>true</c> means the user asked
    /// for it in a new tab; the caller owns that, because a window has no business minting editor tabs
    /// (§2.2). The grid's pending state is left alone either way — reading the SQL is not discarding it.
    /// </summary>
    public PendingEditsSqlWindow(WriteConfirmation request)
    {
        Title = "Pending changes — SQL — Bearing";
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res("Bg.Window");

        var root = new DockPanel { LastChildFill = true, Margin = new Avalonia.Thickness(14) };

        var header = Header(request);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = Buttons(request);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // The window's own button row carries Copy, so the list must not draw a second one.
        root.Children.Add(SqlStatementList.Build(request, copyButton: false));

        Content = root;
    }

    /// <summary>Where these statements would land, and the one caveat the displayed text carries.</summary>
    private static Control Header(WriteConfirmation request)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(0, 0, 0, 10) };

        // Not request.Heading: that is the confirmation's *question* ("Save 4 changes to production ·
        // Production?"), and this window asks nothing — its buttons are Copy, Open in a new tab and Close.
        // A question mark over a read-only view invites the reader to look for the yes button.
        panel.Children.Add(new TextBlock
        {
            Text = $"{Plural(request.Statements.Count, "pending change")} to {request.Target}",
            Foreground = Res("Text.Primary"),
            FontSize = Metric("Font.Body"),
            FontWeight = FontWeight.Bold,
        });

        // §5.4: the statements Bearing runs are parameterized, and this rendering inlines the values so the
        // list can be read at all. Said out loud, because the two forms are not character-identical — a value
        // containing a quote is escaped for display and sent as a parameter — and someone about to paste this
        // into a production console needs to know which one they are holding.
        panel.Children.Add(new TextBlock
        {
            Text = "Values are inlined here for reading. Bearing itself sends these as parameterized "
                 + "statements, so a value containing a quote is not sent exactly as it is printed.",
            Foreground = Res("Text.Muted"),
            FontSize = Metric("Font.Small"),
            TextWrapping = TextWrapping.Wrap,
        });

        if (request.Warning is { } warning)
        {
            panel.Children.Add(new TextBlock
            {
                Text = warning,
                Foreground = Res("Warn.Amber"),
                FontSize = Metric("Font.Small"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return panel;
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private Control Buttons(WriteConfirmation request)
    {
        var copy = new Button { Content = "⧉ Copy" };
        // Copy leaves the window open: the list is scrollable and long, and someone who copies usually wants
        // to keep reading it.
        copy.Click += (_, _) => Clipboard?.SetTextAsync(request.Script);

        var openInTab = new Button { Content = "Open in a new tab" };
        openInTab.Click += (_, _) => Close(true);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close(false);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 10, 0, 0),
            Children = { copy, openInTab, close },
        };
    }
}
