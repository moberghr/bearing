using System.Linq;
using Avalonia.Input;
using Squirrel.App.Input;
using Xunit;

namespace Squirrel.App.Tests;

public class KeymapLoaderTests
{
    private const PhysicalKey NoPhys = PhysicalKey.None;
    private static Keymap Defaults() => KeymapDefaults.Build();

    private static KeymapLoadResult Apply(params KeyBindingEntry[] entries) => KeymapLoader.Apply(Defaults(), entries);

    [Fact]
    public void No_overrides_leaves_defaults_untouched()
    {
        var r = Apply();
        Assert.Empty(r.Warnings);
        Assert.Equal(Defaults().Bindings.Count, r.Keymap.Bindings.Count);
    }

    [Fact]
    public void Bind_adds_a_gesture_with_scope_inferred_from_the_command()
    {
        var r = Apply(new KeyBindingEntry { Key = "F8", Command = CommandIds.Run }); // F8 is unbound by default
        Assert.Empty(r.Warnings);
        // new gesture works; the default Ctrl+Enter still works too
        Assert.Equal(CommandIds.Run, r.Keymap.Resolve(KeyScope.Global, KeyModifiers.None, Key.F8, NoPhys));
        Assert.Equal(CommandIds.Run, r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Enter, NoPhys));
    }

    [Fact]
    public void Unbind_by_key_removes_one_gesture()
    {
        var r = Apply(new KeyBindingEntry { Key = "F5", Command = "-" + CommandIds.Run });
        Assert.Empty(r.Warnings);
        Assert.Null(r.Keymap.Resolve(KeyScope.Global, KeyModifiers.None, Key.F5, NoPhys));         // gone
        Assert.Equal(CommandIds.Run, r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Enter, NoPhys)); // kept
    }

    [Fact]
    public void Keyless_unbind_removes_all_of_a_commands_gestures()
    {
        var r = Apply(new KeyBindingEntry { Command = "-" + CommandIds.TabNew }); // Ctrl+N and Ctrl+T
        Assert.Empty(r.Warnings);
        Assert.Null(r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.N, NoPhys));
        Assert.Null(r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.T, NoPhys));
        Assert.DoesNotContain(r.Keymap.Bindings, b => b.CommandId == CommandIds.TabNew);
    }

    [Fact]
    public void Rebind_is_unbind_plus_bind()
    {
        var r = Apply(
            new KeyBindingEntry { Command = "-" + CommandIds.ViewToggleResults },       // drop Ctrl+R default
            new KeyBindingEntry { Key = "Ctrl+Shift+R", Command = CommandIds.ViewToggleResults });
        Assert.Empty(r.Warnings);
        Assert.Null(r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.R, NoPhys));
        Assert.Equal(CommandIds.ViewToggleResults,
            r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.R, NoPhys));
    }

    [Fact]
    public void Binding_a_taken_gesture_displaces_the_old_command_and_warns()
    {
        // Ctrl+S is Save by default; rebind it to Run.
        var r = Apply(new KeyBindingEntry { Key = "Ctrl+S", Command = CommandIds.Run });
        Assert.Equal(CommandIds.Run, r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.S, NoPhys));
        Assert.Contains(r.Warnings, w => w.Contains(CommandIds.FileSave) && w.Contains(CommandIds.Run));
    }

    [Fact]
    public void Explicit_scope_binds_in_that_scope()
    {
        var r = Apply(new KeyBindingEntry { Key = "Ctrl+Y", Command = CommandIds.GridCopy, Scope = "Grid" });
        Assert.Empty(r.Warnings);
        Assert.Equal(CommandIds.GridCopy, r.Keymap.Resolve(KeyScope.Grid, KeyModifiers.Control, Key.Y, NoPhys));
        Assert.Null(r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Y, NoPhys));
    }

    [Fact]
    public void Unknown_command_is_skipped_with_a_warning()
    {
        var r = Apply(new KeyBindingEntry { Key = "Ctrl+R", Command = "does.not.exist" });
        Assert.Single(r.Warnings);
        Assert.Equal(Defaults().Bindings.Count, r.Keymap.Bindings.Count); // nothing added
    }

    [Fact]
    public void Unparseable_gesture_is_skipped_with_a_warning()
    {
        var r = Apply(new KeyBindingEntry { Key = "Ctrl+Nonsense", Command = CommandIds.Run });
        Assert.Single(r.Warnings);
        Assert.Contains(r.Warnings, w => w.Contains("unparseable"));
    }

    [Fact]
    public void Unbinding_something_not_bound_warns_but_does_not_throw()
    {
        var r = Apply(new KeyBindingEntry { Key = "Ctrl+Shift+Q", Command = "-" + CommandIds.Run });
        Assert.Single(r.Warnings);
        Assert.Contains(r.Warnings, w => w.Contains("nothing to unbind") || w.Contains("not bound"));
    }

    [Fact]
    public void Parses_the_json_shape_including_the_unbind_prefix()
    {
        var json = """[ { "key": "F8", "command": "run" }, { "command": "-tab.new" } ]""";
        var r = KeymapLoader.LoadFromJson(Defaults(), json);
        Assert.Empty(r.Warnings);
        Assert.Equal(CommandIds.Run, r.Keymap.Resolve(KeyScope.Global, KeyModifiers.None, Key.F8, NoPhys));
        Assert.Null(r.Keymap.Resolve(KeyScope.Global, KeyModifiers.Control, Key.N, NoPhys));
    }

    [Fact]
    public void Malformed_json_falls_back_to_defaults_with_a_warning()
    {
        var r = KeymapLoader.LoadFromJson(Defaults(), "{ this is not valid ]");
        Assert.Single(r.Warnings);
        Assert.Equal(Defaults().Bindings.Count, r.Keymap.Bindings.Count);
    }
}
