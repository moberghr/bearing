using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Bearing.App.Services;
using Bearing.App.Views;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The pending-edits SQL window (#114). What only a realized visual can hold: the statements are actually
/// rendered, the inlining caveat is on screen, and the two ways out of the window report different answers —
/// which is what decides whether the caller mints a tab. The confirmation text itself is tested without a UI
/// in <c>WriteConfirmationTests</c>.
/// </summary>
[Collection(UiTestCollection.Name)]
public class PendingEditsSqlWindowTests
{
    private readonly UiTestSession _ui;

    public PendingEditsSqlWindowTests(UiTestSession ui) => _ui = ui;

    private static WriteConfirmation Request(bool guarded = false) => WriteConfirmation.ForEdits(
        new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod-eu",
            Environment = "Production",
            ProviderId = "postgres",
            RequireWriteConfirmation = guarded,
        },
        [
            new WriteStatement("UPDATE", "update public.payment set amount = 4.99 where payment_id = 17;", true),
            new WriteStatement("DELETE", "delete from public.payment where payment_id = 18;", true),
        ]);

    private static PendingEditsSqlWindow Show(WriteConfirmation request)
    {
        var window = new PendingEditsSqlWindow(request);
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        return window;
    }

    private static IReadOnlyList<string> Labels(Window window)
        => window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? "")
            .Where(t => t.Length > 0)
            .ToList();

    [Fact]
    public Task Every_pending_statement_is_rendered() => _ui.Run(() =>
    {
        var window = Show(Request());
        var labels = Labels(window);

        Assert.Contains("update public.payment set amount = 4.99 where payment_id = 17;", labels);
        Assert.Contains("delete from public.payment where payment_id = 18;", labels);
        Assert.Contains("2 statements", labels);
        // Named target, so it is answerable without leaving the window — same as the save confirmation.
        Assert.Contains(labels, l => l.Contains("prod-eu") && l.Contains("Production"));
        window.Close();
    });

    [Fact]
    public Task It_says_the_displayed_values_are_inlined_for_reading() => _ui.Run(() =>
    {
        // §5.4: what Bearing sends is parameterized. A window that showed inlined SQL without saying so
        // would invite someone to paste a subtly different statement into a production console.
        var window = Show(Request());
        var text = string.Join(" ", Labels(window));

        Assert.Contains("inlined here for reading", text);
        Assert.Contains("parameterized", text);
        window.Close();
    });

    [Fact]
    public Task A_guarded_connection_carries_its_warning_here_too() => _ui.Run(() =>
    {
        var window = Show(Request(guarded: true));
        Assert.Contains(Labels(window), l => l.Contains("requiring confirmation for every write"));
        window.Close();

        var ordinary = Show(Request(guarded: false));
        Assert.DoesNotContain(Labels(ordinary), l => l.Contains("requiring confirmation for every write"));
        ordinary.Close();
    });

    /// <summary>
    /// Close reports false and "Open in a new tab" reports true — the whole contract with the caller, since
    /// the window itself never mints a tab (§2.2). Asserted through the buttons rather than the dialog
    /// result, which needs an owner window to await.
    /// </summary>
    [Fact]
    public Task The_two_exits_report_different_answers() => _ui.Run(() =>
    {
        var window = Show(Request());
        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Content is string)
            .ToList();
        var labels = buttons.Select(b => (string)b.Content!).ToList();

        Assert.Contains("Open in a new tab", labels);
        Assert.Contains("Close", labels);
        // Two copy buttons is expected: the footer's, and the statement list's own header (which every write
        // confirmation already shows).
        Assert.Contains("⧉ Copy", labels);

        // Copy must not be an exit: the list scrolls, and someone who copies usually keeps reading.
        Assert.All(buttons.Where(b => (string)b.Content! == "⧉ Copy"), b => Assert.False(b.IsCancel));
        Assert.True(buttons.Single(b => (string)b.Content! == "Close").IsCancel);
        Assert.False(buttons.Single(b => (string)b.Content! == "Open in a new tab").IsCancel);
        window.Close();
    });
}
