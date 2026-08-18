using System.Linq;
using Avalonia.Input;
using Bearing.App.Input;
using Xunit;

namespace Bearing.App.Tests;

public class KeybindingTests
{
    // ---- GestureParser ----

    [Theory]
    [InlineData("Ctrl+S", KeyModifiers.Control, Key.S)]
    [InlineData("Ctrl+Shift+S", KeyModifiers.Control | KeyModifiers.Shift, Key.S)]
    [InlineData("Ctrl+Enter", KeyModifiers.Control, Key.Enter)]
    [InlineData("F5", KeyModifiers.None, Key.F5)]
    [InlineData("Alt+Up", KeyModifiers.Alt, Key.Up)]
    [InlineData("Ctrl+/", KeyModifiers.Control, Key.OemQuestion)]
    [InlineData("Ctrl+-", KeyModifiers.Control, Key.OemMinus)]
    [InlineData("Ctrl+Shift+=", KeyModifiers.Control | KeyModifiers.Shift, Key.OemPlus)]
    public void Parses_logical_gestures(string text, KeyModifiers mods, Key key)
    {
        Assert.True(GestureParser.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(key, g.Logical);
        Assert.Null(g.Physical);
    }

    [Fact]
    public void Parses_physical_gesture()
    {
        Assert.True(GestureParser.TryParse("Ctrl+Shift+PhysBracketLeft", out var g));
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, g.Modifiers);
        Assert.Equal(PhysicalKey.BracketLeft, g.Physical);
        Assert.Null(g.Logical);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+A")]        // unknown modifier
    [InlineData("Ctrl+PhysNope")]  // unknown physical key
    public void Rejects_garbage(string text) => Assert.False(GestureParser.TryParse(text, out _));

    [Fact]
    public void Meta_folds_to_control_when_parsed()
    {
        var g = GestureParser.Parse("Meta+C");
        Assert.Equal(KeyModifiers.Control, g.Modifiers); // Cmd/Win normalized to Ctrl
    }

    [Fact]
    public void All_default_bindings_round_trip()
    {
        foreach (var b in KeymapDefaults.Build().Bindings)
        {
            var text = GestureParser.Format(b.Gesture);
            Assert.True(GestureParser.TryParse(text, out var reparsed), $"could not reparse '{text}'");
            Assert.Equal(b.Gesture, reparsed);
        }
    }

    // ---- Matching / resolution ----

    private static readonly Keymap Defaults = KeymapDefaults.Build();

    // Logical-binding resolution ignores the physical key, so pass PhysicalKey.None for these.
    private const PhysicalKey NoPhys = PhysicalKey.None;

    [Fact]
    public void Resolves_global_run_on_both_bindings()
    {
        Assert.Equal(CommandIds.Run, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Enter, NoPhys));
        Assert.Equal(CommandIds.Run, Defaults.Resolve(KeyScope.Global, KeyModifiers.None, Key.F5, NoPhys));
    }

    [Fact]
    public void Meta_matches_a_control_binding()
        => Assert.Equal(CommandIds.FileSave, Defaults.Resolve(KeyScope.Global, KeyModifiers.Meta, Key.S, NoPhys));

    [Fact]
    public void Exact_modifiers_required()
    {
        // plain S is typing, not Save; Ctrl+Shift+S is Save As, not Save.
        Assert.Null(Defaults.Resolve(KeyScope.Global, KeyModifiers.None, Key.S, NoPhys));
        Assert.Equal(CommandIds.FileSaveAs,
            Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.S, NoPhys));
    }

    [Fact]
    public void Scopes_are_isolated()
    {
        // Ctrl+A is Select-statement in the editor, Select-all in the grid, and unbound globally.
        Assert.Equal(CommandIds.EditorSelectStatement, Defaults.Resolve(KeyScope.Editor, KeyModifiers.Control | KeyModifiers.Shift, Key.A, NoPhys));
        Assert.Equal(CommandIds.GridSelectAll, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.A, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.A, NoPhys));
    }

    [Fact]
    public void Enter_means_run_globally_but_edit_in_the_grid()
    {
        Assert.Equal(CommandIds.Run, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Enter, NoPhys));
        Assert.Equal(CommandIds.GridBeginEdit, Defaults.Resolve(KeyScope.Grid, KeyModifiers.None, Key.Enter, NoPhys));
    }

    [Fact]
    public void Space_edits_the_active_cell_like_enter_and_f2()
    {
        // On a checkbox cell "edit" is the value cycle, and the keyboard is now the primary way to change a
        // bool: clicking a grid cell only ever selects it.
        Assert.Equal(CommandIds.GridBeginEdit, Defaults.Resolve(KeyScope.Grid, KeyModifiers.None, Key.Space, NoPhys));
        Assert.Equal(CommandIds.GridBeginEdit, Defaults.Resolve(KeyScope.Grid, KeyModifiers.None, Key.F2, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Global, KeyModifiers.None, Key.Space, NoPhys));
    }

    [Fact]
    public void Ctrl_S_saves_the_grids_rows_but_only_inside_the_grid()
    {
        // grid.save is guarded on there being pending row edits, so a clean grid leaves Ctrl+S unhandled and
        // it bubbles to file.save. The keymap half of that is: the two ids must not collide in one scope.
        Assert.Equal(CommandIds.GridSave, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.S, NoPhys));
        Assert.Equal(CommandIds.FileSave, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.S, NoPhys));
        Assert.Equal("Ctrl+S", Defaults.DisplayGesture(CommandIds.FileSave)); // what the File menu still shows
    }

    [Fact]
    public void Discarding_row_edits_is_not_bound_to_a_plain_undo()
    {
        // It drops *every* pending change; Ctrl+Z would promise a one-step undo it doesn't do.
        Assert.Equal(CommandIds.GridDiscard,
            Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control | KeyModifiers.Alt, Key.Z, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.Z, NoPhys));
    }

    [Fact]
    public void The_grids_insert_family_stays_distinct()
    {
        Assert.Equal(CommandIds.GridCopy, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.Insert, NoPhys));
        Assert.Equal(CommandIds.GridPaste, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Shift, Key.Insert, NoPhys));
        Assert.Equal(CommandIds.GridAddRow, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Alt, Key.Insert, NoPhys));
        Assert.Equal(CommandIds.GridPaste, Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.V, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Grid, KeyModifiers.None, Key.Insert, NoPhys)); // no bare-Insert row
    }

    [Fact]
    public void Comment_vs_fold_all_differ_only_by_shift()
    {
        // Ctrl+- (OemMinus) toggles comment; Ctrl+Shift+- folds all.
        Assert.Equal(CommandIds.EditorToggleComment, Defaults.Resolve(KeyScope.Editor, KeyModifiers.Control, Key.OemMinus, NoPhys));
        Assert.Equal(CommandIds.EditorFoldAll, Defaults.Resolve(KeyScope.Editor, KeyModifiers.Control | KeyModifiers.Shift, Key.OemMinus, NoPhys));
    }

    [Fact]
    public void Ctrl_W_is_the_editors_delete_word_and_no_longer_closes_the_tab()
    {
        // Reclaimed from tab.close, which moved to Ctrl+F4 — so Ctrl+W must be unbound in every other
        // scope, or closing a tab stays one stray keystroke away from the grid/sidebar.
        Assert.Equal(CommandIds.EditorDeleteWordBack, Defaults.Resolve(KeyScope.Editor, KeyModifiers.Control, Key.W, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.W, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.W, NoPhys));

        Assert.Equal(CommandIds.TabClose, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.F4, NoPhys));
        Assert.Equal("Ctrl+F4", Defaults.DisplayGesture(CommandIds.TabClose)); // what the File menu shows
    }

    [Fact]
    public void Ctrl_U_is_editor_scoped_so_the_tunnel_claims_it_before_AvaloniaEdit()
    {
        Assert.Equal(CommandIds.EditorDeleteToLineStart, Defaults.Resolve(KeyScope.Editor, KeyModifiers.Control, Key.U, NoPhys));
        Assert.Null(Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.U, NoPhys));
    }

    [Fact]
    public void Fold_matches_physical_bracket_regardless_of_logical_key()
    {
        // On a non-US layout the physical [ key reports a different logical Key; the physical binding must
        // still match. Pass an unrelated logical key to prove physical is what's consulted.
        Assert.Equal(CommandIds.EditorFoldCurrent,
            Defaults.Resolve(KeyScope.Editor, KeyModifiers.Control | KeyModifiers.Shift, Key.OemTilde, PhysicalKey.BracketLeft));
    }

    [Fact]
    public void Physical_binding_wins_over_logical_on_the_same_keystroke()
    {
        // A keystroke where BOTH bindings match: logical Ctrl+A and physical Ctrl+BracketRight.
        var map = new Keymap(new[]
        {
            new Bearing.App.Input.KeyBinding(KeyScope.Editor, GestureParser.Parse("Ctrl+A"), "logical"),
            new Bearing.App.Input.KeyBinding(KeyScope.Editor, GestureParser.Parse("Ctrl+PhysBracketRight"), "physical"),
        });
        Assert.Equal("physical", map.Resolve(KeyScope.Editor, KeyModifiers.Control, Key.A, PhysicalKey.BracketRight));
    }

    [Fact]
    public void DisplayGesture_prefers_the_first_binding()
    {
        Assert.Equal("Ctrl+Enter", Defaults.DisplayGesture(CommandIds.Run));   // not F5
        Assert.Equal("Ctrl+N", Defaults.DisplayGesture(CommandIds.TabNew));    // not Ctrl+T
        Assert.Null(Defaults.DisplayGesture("does.not.exist"));
    }

    // ---- Registry ----

    [Fact]
    public void Registry_runs_and_gates_commands()
    {
        var registry = new CommandRegistry();
        var ran = 0;
        registry.Register(KeyCommand.Sync("t.run", "Run", KeyScope.Global, "g", () => ran++));
        registry.Register(KeyCommand.Sync("t.gated", "Gated", KeyScope.Global, "g", () => ran++, canRun: () => false));

        Assert.True(registry.Get("t.run")!.CanRun());
        Assert.False(registry.Get("t.gated")!.CanRun());
        Assert.Null(registry.Get("missing"));
    }
}
