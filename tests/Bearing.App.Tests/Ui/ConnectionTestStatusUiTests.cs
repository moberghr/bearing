using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bearing.App.Connections;
using Bearing.App.Controls;
using Bearing.App.Views;
using Bearing.Core.Data;
using Bearing.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The connection dialog's Test button and status row, as wired: that a press starts a test and turns the
/// button into Stop, that Stop cancels the attempt and a late answer cannot overwrite it, that a result
/// reaches the realized headline, and that editing a field retires the result.
/// <para>
/// The wording is unit-tested in <c>ConnectionTestStatusTests</c>. Nothing here claims a visual (§0.2): these
/// read the row's status, a <c>TextBlock</c>'s text, a button's content and an <c>IsVisible</c> flag. How the
/// row looks — the beacon, the colours, the spacing — needs the user's eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ConnectionTestStatusUiTests
{
    private readonly UiTestSession _ui;

    public ConnectionTestStatusUiTests(UiTestSession ui) => _ui = ui;

    /// <summary>A test the fixture finishes by hand, recording the token it was given.</summary>
    private sealed class PendingTest
    {
        public readonly TaskCompletionSource<bool> Result = new();
        public CancellationToken Token;
        public int Calls;

        public Task<bool> Run(ConnectionInfo info, string? password, string? sshSecret, CancellationToken ct)
        {
            Calls++;
            Token = ct;
            return Result.Task;
        }
    }

    private static ConnectionInfo Existing() => new()
    {
        Id = Guid.NewGuid(), Name = "orders", ProviderId = "postgres",
        Host = "db.example", Port = 5432, Database = "orders", User = "app",
    };

    private static ConnectionDialog Show(Func<ConnectionInfo, string?, string?, CancellationToken, Task<bool>> test)
    {
        var dialog = new ConnectionDialog(Existing(), "secret", test, providers: new ProviderRegistry())
        {
            Width = 560,
            Height = 900,
        };
        dialog.Show();
        Pump(dialog);
        return dialog;
    }

    private static void Pump(ConnectionDialog dialog)
    {
        for (var i = 0; i < 3; i++)
        {
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Pump until the row leaves the testing state. A finished test is held on screen for a moment
    /// before its result shows, so one pump is not enough; this waits on real time, with a deadline.</summary>
    private static void PumpUntilSettled(ConnectionDialog dialog)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Row(dialog).Status.Phase == ConnectionTestPhase.Testing && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Pump(dialog);
        }
    }

    private static Button TestButton(ConnectionDialog dialog) => dialog.FindControl<Button>("TestButton")!;

    private static ConnectionTestStatusView Row(ConnectionDialog dialog)
        => dialog.FindControl<ConnectionTestStatusView>("TestStatus")!;

    private static T Part<T>(ConnectionDialog dialog, string name) where T : Control
        => Row(dialog).GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Press(ConnectionDialog dialog)
    {
        TestButton(dialog).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(dialog);
    }

    [Fact]
    public Task A_dialog_opens_untested() => _ui.Run(() =>
    {
        var dialog = Show((_, _, _, _) => Task.FromResult(true));
        try
        {
            Assert.Equal(ConnectionTestPhase.NotTested, Row(dialog).Status.Phase);
            Assert.Equal("Not tested", Part<TextBlock>(dialog, "TestHeadline").Text);
            Assert.Equal("Test", TestButton(dialog).Content);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task Pressing_test_starts_one_and_offers_stop() => _ui.Run(() =>
    {
        var pending = new PendingTest();
        var dialog = Show(pending.Run);
        try
        {
            Press(dialog);

            Assert.Equal(1, pending.Calls);
            Assert.Equal(ConnectionTestPhase.Testing, Row(dialog).Status.Phase);
            Assert.Equal("Testing db.example:5432…", Part<TextBlock>(dialog, "TestHeadline").Text);
            Assert.Equal("Stop", TestButton(dialog).Content);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task Stop_cancels_the_attempt_and_a_late_answer_does_not_overwrite_it() => _ui.Run(() =>
    {
        var pending = new PendingTest();
        var dialog = Show(pending.Run);
        try
        {
            Press(dialog);
            Press(dialog);   // now "Stop"

            Assert.Equal(1, pending.Calls);   // the second press did not start a second test
            Assert.True(pending.Token.IsCancellationRequested);
            Assert.Equal(ConnectionTestPhase.Stopped, Row(dialog).Status.Phase);
            Assert.Equal("Test", TestButton(dialog).Content);

            // A driver slow to honour the cancellation answers anyway.
            pending.Result.SetResult(true);
            Thread.Sleep(600);   // past the hold a live result would have waited out
            Pump(dialog);

            Assert.Equal(ConnectionTestPhase.Stopped, Row(dialog).Status.Phase);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task A_success_reaches_the_row() => _ui.Run(() =>
    {
        var pending = new PendingTest();
        var dialog = Show(pending.Run);
        try
        {
            Press(dialog);
            pending.Result.SetResult(true);
            PumpUntilSettled(dialog);

            Assert.Equal(ConnectionTestPhase.Succeeded, Row(dialog).Status.Phase);
            Assert.Equal("Connected", Part<TextBlock>(dialog, "TestHeadline").Text);
            Assert.Equal("app@db.example:5432/orders", Part<SelectableTextBlock>(dialog, "TestDetail").Text);
            Assert.Equal("Test", TestButton(dialog).Content);
            Assert.False(Part<Button>(dialog, "TestCopy").IsVisible);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task A_thrown_error_is_shown_redacted_with_copy() => _ui.Run(() =>
    {
        var pending = new PendingTest();
        var dialog = Show(pending.Run);
        try
        {
            Press(dialog);
            pending.Result.SetException(new InvalidOperationException("no route; Password=secret;Host=db"));
            PumpUntilSettled(dialog);

            var detail = Part<SelectableTextBlock>(dialog, "TestDetail").Text!;
            Assert.Equal(ConnectionTestPhase.Failed, Row(dialog).Status.Phase);
            Assert.Contains("no route", detail);
            Assert.DoesNotContain("secret", detail);
            Assert.True(Part<Button>(dialog, "TestCopy").IsVisible);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task Editing_a_field_retires_the_result() => _ui.Run(() =>
    {
        var dialog = Show((_, _, _, _) => Task.FromResult(true));
        try
        {
            Press(dialog);
            PumpUntilSettled(dialog);
            Assert.Equal(ConnectionTestPhase.Succeeded, Row(dialog).Status.Phase);

            ConnectionEditorProbe.Type(dialog, "Host", "other.example");

            Assert.Equal(ConnectionTestPhase.NotTested, Row(dialog).Status.Phase);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task Editing_a_field_abandons_a_running_test() => _ui.Run(() =>
    {
        var pending = new PendingTest();
        var dialog = Show(pending.Run);
        try
        {
            Press(dialog);
            ConnectionEditorProbe.Type(dialog, "Host", "other.example");

            Assert.True(pending.Token.IsCancellationRequested);
            Assert.Equal(ConnectionTestPhase.NotTested, Row(dialog).Status.Phase);
            Assert.Equal("Test", TestButton(dialog).Content);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    [Fact]
    public Task Switching_tabs_keeps_the_result() => _ui.Run(() =>
    {
        var dialog = Show((_, _, _, _) => Task.FromResult(true));
        try
        {
            Press(dialog);
            PumpUntilSettled(dialog);
            ConnectionEditorProbe.Open(dialog, "Safety");

            Assert.Equal(ConnectionTestPhase.Succeeded, Row(dialog).Status.Phase);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });
}
