using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bearing.App.Services;
using Bearing.App.Views;
using Bearing.Core.Data;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The full-scan confirmation (W4), realized: Cancel is both the Esc and the Enter button, so neither key is a way
/// into a scan; the confirm button is neither; and a run Bearing could not check is headed "could not check", never
/// a scan.
/// <para>
/// Nothing here claims a visual (§0.2/§4.3): these read button flags and a TextBlock's text off a shown window.
/// Whether the dialog reads well still needs eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ConfirmScanDialogTests
{
    private readonly UiTestSession _ui;

    public ConfirmScanDialogTests(UiTestSession ui) => _ui = ui;

    private static readonly ConnectionInfo Prod = new()
    {
        Id = Guid.NewGuid(),
        Name = "orders-prod",
        ProviderId = "dynamodb",
        Environment = "production",
        EnvironmentColor = "#E5484D",
    };

    private static readonly ScanTarget Orders = new("select * from \"Orders\"", "Orders", null, [], Readable: true);

    private static ScanQuestion Question(ScanVerdictKind kind) => new(
        Prod,
        ScanQuestionKind.Run,
        [
            kind == ScanVerdictKind.FullScan
                ? new ScanVerdict(Orders, ScanVerdictKind.FullScan, "customerId", 12_345, null)
                : new ScanVerdict(Orders, ScanVerdictKind.Undetermined, null, null, ScanUncertainty.UnknownTable),
        ],
        "body");

    private static ConfirmScanDialog Show(ScanQuestion question)
    {
        var dialog = new ConfirmScanDialog(question);
        dialog.Show();
        for (var i = 0; i < 2; i++)
        {
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        }

        return dialog;
    }

    private static Button ButtonLabelled(Window dialog, string label)
        => dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == label);

    private static string HeadingText(Window dialog)
        => dialog.GetLogicalDescendants().OfType<TextBlock>().First().Text ?? "";

    [Theory]
    [InlineData(ScanVerdictKind.FullScan)]
    [InlineData(ScanVerdictKind.Undetermined)]
    public Task Cancel_is_the_escape_and_the_default_and_the_confirm_button_is_neither(ScanVerdictKind kind) => _ui.Run(() =>
    {
        var dialog = Show(Question(kind));
        try
        {
            var cancel = ButtonLabelled(dialog, "Cancel");
            var proceed = ButtonLabelled(dialog, "Run anyway");

            Assert.True(cancel.IsCancel);
            Assert.True(cancel.IsDefault);
            Assert.False(proceed.IsCancel);
            Assert.False(proceed.IsDefault);
            Assert.True(cancel.IsEffectivelyVisible);
            Assert.True(proceed.IsEffectivelyVisible);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task A_known_scan_is_headed_as_one() => _ui.Run(() =>
    {
        var dialog = Show(Question(ScanVerdictKind.FullScan));
        try
        {
            Assert.Equal("Read a whole table on orders-prod · production?", HeadingText(dialog));
            Assert.Equal("Confirm full table scan", dialog.Title);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task A_read_bearing_could_not_check_says_so_and_never_calls_it_a_scan() => _ui.Run(() =>
    {
        var dialog = Show(Question(ScanVerdictKind.Undetermined));
        try
        {
            var heading = HeadingText(dialog);
            Assert.Contains("could not check", heading);
            Assert.DoesNotContain("scan", heading, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scan", dialog.Title ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            dialog.Close();
        }
    });

    /// <summary>TC2's dialog half: a Fetch-all question with no connection to name still realizes, headed without
    /// one, with the same safe buttons.</summary>
    [Fact]
    public Task A_fetch_all_question_with_no_connection_still_asks_safely() => _ui.Run(() =>
    {
        var dialog = Show(ScanConfirmation.FetchAllQuestion(null, 5_000));
        try
        {
            Assert.Equal("Keep reading the whole table?", HeadingText(dialog));
            var cancel = ButtonLabelled(dialog, "Cancel");
            Assert.True(cancel.IsCancel && cancel.IsDefault);
            var proceed = ButtonLabelled(dialog, "Fetch all");
            Assert.False(proceed.IsCancel || proceed.IsDefault);
        }
        finally
        {
            dialog.Close();
        }
    });
}
