using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using Bearing.App.Completion;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// Completion as the user meets it: real keystrokes into the real editor in the real shell, ending at a
/// realized popup with rows in it.
///
/// <para>
/// This exists because 1.0 shipped with completion dead for every user and a green suite. #94 gave
/// <see cref="Bearing.Sql.CompletionEngine"/> a <c>Func&lt;ISqlDialect&gt;</c> so one engine could serve two
/// grammars, and the engine asked it from inside <c>CompleteCore</c> — which runs on
/// <c>PgParsing.OnDeepStack</c>'s parse thread. The App's answer to that question is "whichever engine the
/// selected tab is on", read off the window's <c>DataContext</c>: an <c>AvaloniaObject</c>, so the call threw
/// <c>VerifyAccess</c> before a single token was parsed. No popup on typing, nothing on Ctrl+Space, and the
/// only trace was one crash-log line per keystroke, because the controller's catch deliberately never
/// reaches the UI.
/// </para>
///
/// <para>
/// <b>Why it has to be here and not in <c>Bearing.Sql.Tests</c>.</b> The engine-level test that pins the
/// same fix (<c>CompletionEngineTests.The_dialect_is_asked_on_the_calling_thread</c>) asserts the callback
/// runs on the caller's thread — which stays true if the caller becomes a thread-pool thread. Putting a
/// <c>Task.Run</c> back around <c>CompleteAsync</c> in <c>CompletionController</c> would kill completion
/// again and leave that test green, because the lambda it can construct there is a Postgres constant with no
/// window behind it. Only the assembled window asks the thread-affine question, so only this can answer it.
/// The two are a pair: the engine test says where the dialect is resolved, this says that the resolution
/// works against the real callback.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class CompletionWiringTests
{
    private readonly UiTestSession _ui;

    public CompletionWiringTests(UiTestSession ui) => _ui = ui;

    /// <summary>The primary symptom: type a table prefix, the popup arrives on the debounce.</summary>
    [Fact]
    public Task Typing_a_table_prefix_opens_the_popup() => _ui.Run(async () =>
    {
        using var shell = await ConnectedShell(nameof(Typing_a_table_prefix_opens_the_popup));
        var editor = Focused(shell);

        shell.Window.KeyTextInput("select * from d");
        shell.Pump();
        Assert.Equal("select * from d", editor.Text);   // the keystrokes landed, before blaming completion

        var list = await WaitForPopup(shell);

        Assert.NotNull(list);
        Assert.Contains("document", Rows(list!));
    });

    /// <summary>
    /// And the second symptom the report named. It is a different route into the same request — the command
    /// registry rather than <c>TextEntered</c>, bypassing the debounce — so it fails for its own reasons and
    /// is worth its own keystroke.
    /// </summary>
    [Fact]
    public Task Ctrl_Space_opens_the_popup() => _ui.Run(async () =>
    {
        using var shell = await ConnectedShell(nameof(Ctrl_Space_opens_the_popup));
        var editor = Focused(shell);
        editor.Text = "select * from d";
        editor.CaretOffset = editor.Text.Length;
        shell.Pump();

        shell.Window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        shell.Window.KeyRelease(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);

        var list = await WaitForPopup(shell);

        Assert.NotNull(list);
        Assert.Contains("document", Rows(list!));
    });

    /// <summary>
    /// And with no connection at all. The shell here has no catalog and never had one, so
    /// <c>SnapshotForSelectedTab</c> is null — which <c>TriggerAsync</c> answered by returning, making a
    /// freshly opened Bearing complete nothing whatever until you connected something. The keywords never
    /// needed the server.
    /// </summary>
    [Fact]
    public Task A_tab_with_no_connection_still_completes_keywords() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(A_tab_with_no_connection_still_completes_keywords));
        Assert.Null(shell.Vm.Execution.SnapshotForSelectedTab());   // the state under test, not an accident

        var editor = Focused(shell);
        shell.Window.KeyTextInput("sel");
        shell.Pump();
        Assert.Equal("sel", editor.Text);

        var list = await WaitForPopup(shell);

        Assert.NotNull(list);
        Assert.Contains("SELECT", Rows(list!));
    });

    // ---- Harness ---------------------------------------------------------------------------------

    /// <summary>
    /// The shell on the demo catalog, connected.
    /// <para>
    /// The connect is not ceremony: <c>SnapshotForSelectedTab</c> is null until a schema has been read, and
    /// only a catalog puts a relation in the popup — so these two tests need one to have <c>document</c> to
    /// assert on. Asserted rather than assumed for that reason. A null snapshot no longer stops completion
    /// (that is <see cref="A_tab_with_no_connection_still_completes_keywords"/>), so without the connect
    /// these would still open a popup and would be asserting the keyword case twice over. The demo provider
    /// serves the catalog with no server (§4.6).
    /// </para>
    /// </summary>
    private static async Task<ShellHarness> ConnectedShell(string name)
    {
        var shell = await ShellHarness.ShowAsync(name, new DemoProvider());
        await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
        shell.Pump();

        await shell.Vm.Connections.ToggleConnectionCommand.ExecuteAsync(null);
        shell.Pump();
        Assert.NotNull(shell.Vm.Execution.SnapshotForSelectedTab());

        return shell;
    }

    /// <summary>The editor, focused and empty — text input goes to whatever has the keyboard (§4.5).</summary>
    private static TextEditor Focused(ShellHarness shell)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus");
        editor.Text = "";
        return editor;
    }

    /// <summary>
    /// The popup's list once it is up, or null if it never came.
    /// <para>
    /// Polled against the clock rather than pumped, because neither wait a pump can perform is a dispatcher
    /// job: the typing route goes through a 150 ms <c>DispatcherTimer</c>, and both routes then hand the
    /// parse to a real thread of <c>PgParsing</c>'s own (a fresh one with a deep stack, not the pool). A
    /// pump-only loop found nothing after sixty passes and would have read as "no popup".
    /// </para>
    /// <para>
    /// <c>CompletionWindow</c> is a <c>Popup</c>, and a shown one is in the shell window's <b>visual</b>
    /// tree — not its logical one, and not the <c>TextArea</c>'s. Measured; the other two return zero even
    /// with the popup open.
    /// </para>
    /// </summary>
    private static async Task<CompletionList?> WaitForPopup(ShellHarness shell)
    {
        for (var i = 0; i < 40; i++)
        {
            shell.Pump();
            if (shell.Window.GetVisualDescendants().OfType<CompletionList>().FirstOrDefault() is { } list)
                return list;
            await Task.Delay(25);
        }
        return null;
    }

    private static string[] Rows(CompletionList list)
        => list.CompletionData.OfType<BearingCompletionData>().Select(d => d.DisplayText).ToArray();
}
