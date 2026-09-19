using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Bearing.App.Connections;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The manual-commit chrome on a realized window (#131): that the toolbar pair and the status chip are
/// <b>absent</b> until a transaction is open, present once one is, and that the chip actually draws
/// differently when it goes stale.
/// <para>
/// The last of those is the only one that needs pixels. "IsVisible is true" and "Classes contains stale" are
/// property assertions a binding can satisfy while putting nothing on the surface — the amber is a style
/// setter on a token brush, which is exactly the shape that silently resolves to nothing when a token is
/// renamed. §4.5's rule: reach for a frame when the visual *is* the subject.
/// </para>
/// <para>
/// Still not a claim that it looks right (§0.2/§4.3). These say the marks differ, not that the difference
/// reads as a warning. Set <c>BEARING_UI_DUMP</c> to a directory to write the frames out and look.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ManualCommitChromeTests
{
    private readonly UiTestSession _ui;

    public ManualCommitChromeTests(UiTestSession ui) => _ui = ui;

    private static Control Named(ShellHarness shell, string name)
        => shell.Window.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == name);

    /// <summary>Open a transaction on the shell's first tab, the way a write does.</summary>
    private static async Task<TabTransaction> OpenAsync(ShellHarness shell, FakeProvider provider)
    {
        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Database = "pagila",
            Environment = "production",
            ManualCommit = true,
        };
        await shell.Vm.Connections.AddOrUpdateConnectionAsync(conn, password: null);
        var tab = shell.Vm.Workspace.Tabs[0];
        tab.ConnectionId = conn.Id;
        shell.Vm.Context.SelectedTab = tab;

        await shell.Vm.Execution.ExecuteAsync(
            "update payment set amount = amount * 1.1\nwhere payment_id between 1 and 400;");
        shell.Pump();
        return shell.Vm.Context.Transactions.For(tab)
               ?? throw new InvalidOperationException("the write did not open a transaction");
    }

    [Fact]
    public Task The_pair_and_the_chip_are_absent_until_a_transaction_is_open() => _ui.Run(async () =>
    {
        var provider = new FakeProvider();
        using var shell = await ShellHarness.ShowAsync("tx-chrome-off", provider);
        Dump(shell, "01-no-transaction");

        Assert.False(Named(shell, "CommitButton").IsVisible);
        Assert.False(Named(shell, "RollbackButton").IsVisible);
        Assert.False(Named(shell, "TransactionChip").IsVisible);

        await OpenAsync(shell, provider);
        Dump(shell, "02-transaction-open");

        Assert.True(Named(shell, "CommitButton").IsVisible);
        Assert.True(Named(shell, "RollbackButton").IsVisible);
        Assert.True(Named(shell, "TransactionChip").IsVisible);
        Assert.Contains("uncommitted", shell.Vm.Execution.TransactionText);
    });

    /// <summary>
    /// The amber. A style setter on <c>Warn.Amber</c> selected by a class the view-model drives — so the
    /// pixels are the only thing that can say the class arrived <i>and</i> the setter resolved.
    /// </summary>
    [Fact]
    public Task The_chip_draws_differently_once_the_transaction_goes_stale() => _ui.Run(async () =>
    {
        var provider = new FakeProvider();
        using var shell = await ShellHarness.ShowAsync("tx-chrome-stale", provider);
        var open = await OpenAsync(shell, provider);
        var chip = Named(shell, "TransactionChip");

        var quiet = FrameCapture.Of(shell.Window).Within(chip, shell.Window);
        Assert.False(shell.Vm.Execution.TransactionIsStale);

        // Past the warning threshold and deliberately short of the rollback one (5 and 15 by default):
        // idle far enough and the sweep correctly ends the transaction instead of colouring it, which
        // leaves nothing to photograph.
        Assert.IsType<FakeTransactionScope>(open.Scope).Idle(TimeSpan.FromMinutes(6));
        await shell.Vm.Execution.SweepTransactionsAsync();
        shell.Pump();
        Dump(shell, "03-transaction-stale");

        Assert.True(shell.Vm.Execution.TransactionIsStale);
        var stale = FrameCapture.Of(shell.Window).Within(chip, shell.Window);

        Assert.Equal(quiet.Count, stale.Count);
        Assert.NotEqual(quiet, stale);   // the border actually changed on the surface
    });

    /// <summary>A statement that failed takes Commit out of reach and leaves Rollback — the way out of that
    /// state — so the two buttons must not be enabled together here.</summary>
    [Fact]
    public Task An_aborted_transaction_disables_commit_but_not_rollback() => _ui.Run(async () =>
    {
        var executor = new FakeExecutor();
        var provider = new FakeProvider { Executor = executor };
        using var shell = await ShellHarness.ShowAsync("tx-chrome-aborted", provider);
        await OpenAsync(shell, provider);

        executor.FailWith = "column \"amont\" does not exist";
        await shell.Vm.Execution.ExecuteAsync("update payment set amont = 1;");
        shell.Pump();
        Dump(shell, "04-transaction-aborted");

        Assert.False(Named(shell, "CommitButton").IsEnabled);
        Assert.True(Named(shell, "RollbackButton").IsEnabled);
        Assert.True(shell.Vm.Execution.TransactionIsStale);   // amber, without waiting for the idle clock
    });

    /// <summary>
    /// The mode pill: visible whenever the tab has a connection (unlike the two buttons), marked when the
    /// mode in force is not the one saved, and actually able to flip the mode by being clicked.
    /// </summary>
    [Fact]
    public Task The_mode_pill_shows_the_mode_and_flips_it() => _ui.Run(async () =>
    {
        var provider = new FakeProvider();
        using var shell = await ShellHarness.ShowAsync("tx-mode-pill", provider);
        var pill = Named(shell, "CommitModeButton");
        Assert.False(pill.IsVisible);                    // no connection on the tab yet

        var conn = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "prod",
            ProviderId = "postgres",
            Database = "pagila",
        };
        await shell.Vm.Connections.AddOrUpdateConnectionAsync(conn, password: null);
        shell.Vm.Workspace.Tabs[0].ConnectionId = conn.Id;
        shell.Vm.Context.SelectedTab = shell.Vm.Workspace.Tabs[0];
        shell.Vm.Execution.RefreshCommitMode();
        shell.Pump();
        Dump(shell, "06-mode-auto");

        Assert.True(pill.IsVisible);
        Assert.Equal("Auto", shell.Vm.Execution.CommitModeLabel);
        Assert.DoesNotContain("manual", pill.Classes);

        // Clicked, the way the toolbar reaches it.
        pill.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        shell.Pump();
        Dump(shell, "07-mode-manual-overridden");

        Assert.Equal("Manual •", shell.Vm.Execution.CommitModeLabel);
        Assert.Contains("manual", pill.Classes);
        Assert.Contains("overridden", pill.Classes);   // the dialog still says auto-commit
        Assert.Contains("This session only", shell.Vm.Execution.CommitModeTip);
    });

    /// <summary>Write the frame out when <c>BEARING_UI_DUMP</c> names a directory. Off in CI and in an
    /// ordinary run: these tests assert pixels, they do not collect them.</summary>
    private static void Dump(ShellHarness shell, string name)
    {
        if (Environment.GetEnvironmentVariable("BEARING_UI_DUMP") is not { Length: > 0 } dir) return;
        shell.Pump();
        FrameCapture.Dump(shell.Window, Path.Combine(dir, name + ".png"));
    }
}
