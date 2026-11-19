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
/// into a scan, and the confirm button is neither — claims only a shown window holds. What the heading and title say
/// is pure (<see cref="ScanQuestion"/>) and pinned in <c>ScanConfirmationTests</c>, not here (§4.3).
/// <para>
/// Nothing here claims a visual (§0.2/§4.3): these read button flags off a shown window. Whether the dialog reads
/// well still needs eyeball QA.
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
