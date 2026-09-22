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
/// The external-access checkbox in the connection dialog's Safety group (§1.11): that it loads from the
/// record, reaches the record on save, and explains itself — including the clause that changes per engine.
/// <para>
/// It is the only control in that group that is not about what <i>you</i> may do with the connection but
/// about who else may, which is why the note under it has to say more than a label could.
/// </para>
/// <para>
/// Nothing here claims a visual (§0.2/§4.3): these read a checkbox's state, a record's field and a
/// TextBlock's text. Whether the row sits well in the group still needs eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ExternalAccessDialogTests
{
    private readonly UiTestSession _ui;

    public ExternalAccessDialogTests(UiTestSession ui) => _ui = ui;

    private static ConnectionInfo Saved(ExternalAccess access) => new()
    {
        Id = Guid.NewGuid(),
        Name = "reporting",
        ProviderId = "postgres",
        Database = "app",
        ExternalAccess = access,
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
        using var on = new Disposer(ConnectionEditorProbe.Dialog(Saved(ExternalAccess.ReadOnly)));
        using var off = new Disposer(ConnectionEditorProbe.Dialog(Saved(ExternalAccess.None)));

        Assert.True(Box(on.Dialog, "ExternalAccessBox").IsChecked);
        Assert.False(Box(off.Dialog, "ExternalAccessBox").IsChecked);
    });

    /// <summary>
    /// The gate is off unless someone turns it on — for a connection that predates the setting and for one
    /// nobody thought about. Asserted on a *fresh* dialog, which is the shape a new connection takes.
    /// </summary>
    [Fact]
    public Task A_new_connection_is_not_exposed() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());

        Assert.False(Box(d.Dialog, "ExternalAccessBox").IsChecked);
        Assert.Equal("", Note(d.Dialog));
    });

    /// <summary>
    /// The note is how the setting explains itself, and the honest parts are what it says it does *not*
    /// bound: the boundary is anything running as you, and the thing that actually holds is a database role.
    /// </summary>
    [Fact]
    public Task Ticking_it_says_what_it_lets_in_and_what_it_does_not_bound() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());

        Box(d.Dialog, "ExternalAccessBox").IsChecked = true;

        var note = Note(d.Dialog);
        Assert.Contains("read", note);
        // What it hands over, and what it withholds.
        Assert.Contains("never the host, user, database or password", note);
        // And the two sentences that must not be softened (§1.11).
        Assert.Contains("Anything running as you", note);
        Assert.Contains("database role", note);
    });

    /// <summary>
    /// §1.9a, in the one place a user decides whether to expose a server: on an engine with no session
    /// read-only the client's check is the whole of it, and a write the lexer cannot see reaches the server.
    /// Saying the same sentence for both engines would be inventing an arrangement on one of them (§1.1).
    /// </summary>
    [Fact]
    public Task The_note_says_who_refuses_the_write_and_it_differs_per_engine() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());
        Box(d.Dialog, "ExternalAccessBox").IsChecked = true;

        var postgres = Note(d.Dialog);
        Assert.Contains("the server refuses", postgres);

        ConnectionEditorProbe.Combo(d.Dialog, "ProviderBox").SelectedIndex = 1;

        var sqlServer = Note(d.Dialog);
        Assert.DoesNotContain("the server refuses", sqlServer);
        Assert.Contains("no session read-only", sqlServer);
    });

    [Fact]
    public Task The_box_reaches_the_saved_connection() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog(Saved(ExternalAccess.None)));

        Box(d.Dialog, "ExternalAccessBox").IsChecked = true;

        var saved = d.Dialog.BuildConnection();
        Assert.Equal(ExternalAccess.ReadOnly, saved.ExternalAccess);
        Assert.True(ExternalAccessPolicy.IsExposed(saved));
    });

    [Fact]
    public Task Unticking_it_closes_the_gate_again() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog(Saved(ExternalAccess.ReadOnly)));

        Box(d.Dialog, "ExternalAccessBox").IsChecked = false;

        var saved = d.Dialog.BuildConnection();
        Assert.Equal(ExternalAccess.None, saved.ExternalAccess);
        Assert.Null(ExternalAccessPolicy.ForExternalHost(saved));
    });

    /// <summary>
    /// Exposure and the connection's own read-only setting are independent: the point of the feature is
    /// that you keep writing to a connection an agent cannot write to. Asserted here because the tempting
    /// implementation ties them together.
    /// </summary>
    [Fact]
    public Task Exposing_it_does_not_make_the_connection_read_only_for_you() => _ui.Run(() =>
    {
        using var d = new Disposer(ConnectionEditorProbe.Dialog());

        Box(d.Dialog, "ExternalAccessBox").IsChecked = true;

        var saved = d.Dialog.BuildConnection();
        Assert.False(saved.ReadOnly);
        // …while the connection an external host is handed is read-only regardless.
        Assert.True(ExternalAccessPolicy.ForExternalHost(saved)!.ReadOnly);
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
