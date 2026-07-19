using System.Collections.Generic;
using System.Linq;
using Squirrel.App.Input;
using Xunit;
using KeyBinding = Squirrel.App.Input.KeyBinding;

namespace Squirrel.App.Tests;

public class KeymapDiffTests
{
    private static Keymap Defaults() => KeymapDefaults.Build();

    [Fact]
    public void No_changes_produces_no_overrides()
        => Assert.Empty(KeymapDiff.ComputeOverrides(Defaults(), Defaults().Bindings));

    [Fact]
    public void A_full_edit_round_trips_cleanly_through_apply()
    {
        var defaults = Defaults();
        var edited = defaults.Bindings.ToList();

        // remove an alias, add a new gesture, rebind a taken one, and bind a palette-only command
        edited.RemoveAll(b => b.CommandId == CommandIds.Run && b.Gesture == GestureParser.Parse("F5"));
        edited.Add(new KeyBinding(KeyScope.Global, GestureParser.Parse("F8"), CommandIds.Run));
        edited.RemoveAll(b => b.CommandId == CommandIds.GridCopy && b.Gesture == GestureParser.Parse("Ctrl+C"));
        edited.Add(new KeyBinding(KeyScope.Grid, GestureParser.Parse("Ctrl+Y"), CommandIds.GridCopy));
        edited.Add(new KeyBinding(KeyScope.Global, GestureParser.Parse("Ctrl+Shift+H"), CommandIds.QueryRunAll));

        var overrides = KeymapDiff.ComputeOverrides(defaults, edited);
        // query.runAll ships unbound, so the loader needs to be told it's a real command (the registry does this).
        var known = new HashSet<string> { CommandIds.QueryRunAll };
        var applied = KeymapLoader.Apply(defaults, overrides, known);

        Assert.Empty(applied.Warnings);                                  // clean apply (unbinds ordered before binds)
        Assert.Equal(edited.ToHashSet(), applied.Keymap.Bindings.ToHashSet()); // exact reconstruction
    }

    [Fact]
    public void Rebinding_a_taken_gesture_round_trips()
    {
        var defaults = Defaults();
        var edited = defaults.Bindings.ToList();
        // Ctrl+S (Save by default) → Run
        edited.RemoveAll(b => b.Gesture == GestureParser.Parse("Ctrl+S"));
        edited.Add(new KeyBinding(KeyScope.Global, GestureParser.Parse("Ctrl+S"), CommandIds.Run));

        var applied = KeymapLoader.Apply(defaults, KeymapDiff.ComputeOverrides(defaults, edited));
        Assert.Empty(applied.Warnings);
        Assert.Equal(edited.ToHashSet(), applied.Keymap.Bindings.ToHashSet());
    }

    [Fact]
    public void Scope_is_emitted_only_for_commands_without_a_default_binding()
    {
        var defaults = Defaults();
        var edited = defaults.Bindings.ToList();
        edited.Add(new KeyBinding(KeyScope.Global, GestureParser.Parse("F9"), CommandIds.QueryRunAll)); // palette-only
        edited.Add(new KeyBinding(KeyScope.Global, GestureParser.Parse("F10"), CommandIds.Run));         // has defaults

        var overrides = KeymapDiff.ComputeOverrides(defaults, edited);
        var runAll = overrides.Single(e => e.Command == CommandIds.QueryRunAll);
        var run = overrides.Single(e => e.Command == CommandIds.Run);
        Assert.Equal("Global", runAll.Scope); // no default → scope spelled out
        Assert.Null(run.Scope);               // has a default → scope inferred, omitted
    }

    [Fact]
    public void Clearing_all_bindings_round_trips_to_an_empty_map()
    {
        var defaults = Defaults();
        var applied = KeymapLoader.Apply(defaults, KeymapDiff.ComputeOverrides(defaults, new List<KeyBinding>()));
        Assert.Empty(applied.Keymap.Bindings);
    }
}
