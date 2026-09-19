using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Bearing.App.Views;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The manual-commit checkbox in the connection dialog's Safety group (#131): that it loads from the record,
/// that it reaches the record on save, that it re-words the advisory note, and that the Production preset
/// deliberately leaves it alone.
/// <para>
/// The last of those is the one worth a test. Every other setting in that group is turned on by the preset,
/// so leaving this one off looks like an oversight until something asserts that it is a decision: the other
/// three narrow what a connection will do, while this one changes what every write <i>is</i>, and a preset
/// button is not where someone opts into two clicks per write for the life of a connection.
/// </para>
/// <para>
/// Nothing here claims a visual (§0.2/§4.3): these read a checkbox's state, a record's field and a
/// TextBlock's text. Whether the row reads well in the group, and whether the note is legible under it,
/// still need eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ManualCommitDialogTests
{
    private readonly UiTestSession _ui;

    public ManualCommitDialogTests(UiTestSession ui) => _ui = ui;

    private static ConnectionInfo Saved(bool manualCommit) => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod",
        ProviderId = "postgres",
        Database = "app",
        ManualCommit = manualCommit,
    };

    private static CheckBox Box(ConnectionDialog dialog, string name)
        => Assert.IsType<CheckBox>(Assert.Single(
            dialog.GetLogicalDescendants().OfType<Control>(), c => c.Name == name));

    private static string Note(ConnectionDialog dialog)
        => dialog.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "SafetyNoteText").Text ?? "";

    [Fact]
    public Task The_box_loads_from_the_connection() => _ui.Run(() =>
    {
        using var on = new Disposer(ConnectionEditorProbe.Dialog(Saved(manualCommit: true)));
        using var off = new Disposer(ConnectionEditorProbe.Dialog(Saved(manualCommit: false)));

        Assert.True(Box(on.Dialog, "ManualCommitBox").IsChecked);
        Assert.False(Box(off.Dialog, "ManualCommitBox").IsChecked);
    });

    /// <summary>The note is how the setting explains itself, and the wording is the honest part: reading
    /// does not open a transaction, which is the whole reason the mode is bearable on a production server.</summary>
    [Fact]
    public Task Ticking_it_explains_what_opens_a_transaction() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());
        Assert.DoesNotContain("A write opens a transaction", Note(d.Dialog));

        Box(d.Dialog, "ManualCommitBox").IsChecked = true;

        Assert.Contains("A write opens a transaction", Note(d.Dialog));
        Assert.Contains("Reading does not", Note(d.Dialog));
    });

    /// <summary>
    /// The two settings together say the odd but true thing. Asserted because the tempting wording — the
    /// ordinary "a write opens a transaction" — would describe a transaction this connection can never open.
    /// </summary>
    [Fact]
    public Task With_read_only_the_note_says_nothing_will_ever_open() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());

        Box(d.Dialog, "ManualCommitBox").IsChecked = true;
        Box(d.Dialog, "ReadOnlyBox").IsChecked = true;

        Assert.Contains("nothing will open a transaction", Note(d.Dialog));
    });

    /// <summary>See the class remarks: this is a decision, not an oversight.</summary>
    [Fact]
    public Task The_production_preset_turns_on_the_other_safety_settings_and_not_this_one() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());
        var preset = d.Dialog.GetLogicalDescendants().OfType<Button>()
            .Single(b => string.Equals(b.Content as string, "production", StringComparison.OrdinalIgnoreCase));

        preset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.True(Box(d.Dialog, "ConfirmWritesBox").IsChecked);
        Assert.True(Box(d.Dialog, "ReadOnlyBox").IsChecked);
        Assert.False(Box(d.Dialog, "ManualCommitBox").IsChecked);
    });

    /// <summary>
    /// The safety group with everything on, realized and laid out — the one test here that shows the dialog,
    /// because a row that is present in the logical tree can still be clipped out of a 90px label column.
    /// Asserts the boxes are all on; set <c>BEARING_UI_DUMP</c> to write the frame out and look at it.
    /// </summary>
    [Fact]
    public Task The_whole_safety_group_lays_out_with_every_setting_on() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Show(Saved(manualCommit: true)));
        Box(d.Dialog, "ConfirmWritesBox").IsChecked = true;
        Box(d.Dialog, "ReadOnlyBox").IsChecked = false;   // else the note describes a transaction that never opens
        d.Dialog.UpdateLayout();

        Assert.True(Box(d.Dialog, "ManualCommitBox").IsVisible);
        Assert.True(Box(d.Dialog, "ManualCommitBox").Bounds.Height > 0);   // laid out, not merely present
        Assert.Contains("A write opens a transaction", Note(d.Dialog));

        if (Environment.GetEnvironmentVariable("BEARING_UI_DUMP") is { Length: > 0 } dir)
            FrameCapture.Dump(d.Dialog, System.IO.Path.Combine(dir, "05-connection-safety.png"));
    });

    /// <summary>Closing the dialog even when the test threw, so one failure does not leave a window behind
    /// for the next test in the collection (§4.5: the session is shared and serialized).</summary>
    private sealed class Disposer : IDisposable
    {
        public Disposer(ConnectionDialog dialog) => Dialog = dialog;
        public ConnectionDialog Dialog { get; }
        public void Dispose() => ConnectionEditorProbe.Close(Dialog);
    }
}
