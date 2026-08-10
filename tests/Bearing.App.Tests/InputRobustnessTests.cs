using Avalonia.Input;
using Bearing.App.Input;
using Xunit;
using KeyBinding = Bearing.App.Input.KeyBinding;  // disambiguate from Avalonia.Input.KeyBinding

namespace Bearing.App.Tests;

/// <summary>
/// Two ways the input pipeline used to misbehave on a hand-written <c>keybindings.json</c>: a gesture token
/// that parses as a *number* rather than a key name, and an MRU cycle that could only ever be ended by Ctrl.
/// </summary>
public class InputRobustnessTests
{
    [Theory]
    [InlineData("Ctrl+16")]        // (Key)16 — parses, isn't a key anyone can press
    [InlineData("Ctrl+0x10")]
    [InlineData("Ctrl+-1")]
    [InlineData("PhysNope")]
    [InlineData("Phys16")]         // same trap on the physical-key path
    [InlineData("Ctrl+NotAKey")]
    public void Undefined_and_numeric_key_tokens_are_rejected(string text)
        => Assert.False(GestureParser.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Tab")]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("PhysBracketLeft")]
    [InlineData("F5")]
    public void Real_gestures_still_parse(string text)
        => Assert.True(GestureParser.TryParse(text, out _));

    [Fact]
    public void The_cycle_ends_on_whatever_modifier_the_binding_actually_holds()
    {
        var ctrlBound = new Keymap(
        [
            new KeyBinding(KeyScope.Global, GestureParser.Parse("Ctrl+Tab"), CommandIds.TabMruNext),
            new KeyBinding(KeyScope.Global, GestureParser.Parse("Ctrl+Shift+Tab"), CommandIds.TabMruPrev),
        ]);

        var mods = MruCycle.ModifiersOf(ctrlBound);
        Assert.Equal(KeyModifiers.Control, mods);            // Shift is excluded: it only picks the direction
        Assert.True(MruCycle.EndsCycle(mods, Key.LeftCtrl));
        Assert.True(MruCycle.EndsCycle(mods, Key.RightCtrl));
        Assert.False(MruCycle.EndsCycle(mods, Key.LeftShift)); // reversing direction must not commit early
        Assert.False(MruCycle.EndsCycle(mods, Key.LeftAlt));
    }

    [Fact]
    public void Rebinding_the_cycle_to_another_modifier_still_ends_it()
    {
        // The bug: the end-of-cycle test was literally `Key.LeftCtrl or Key.RightCtrl`, so this binding left
        // the cycle flag stuck on and MRU order frozen (order is only recorded when a cycle ends).
        var altBound = new Keymap(
            [new KeyBinding(KeyScope.Global, GestureParser.Parse("Alt+Tab"), CommandIds.TabMruNext)]);

        var mods = MruCycle.ModifiersOf(altBound);
        Assert.Equal(KeyModifiers.Alt, mods);
        Assert.True(MruCycle.EndsCycle(mods, Key.LeftAlt));
        Assert.False(MruCycle.EndsCycle(mods, Key.LeftCtrl));
    }

    [Fact]
    public void A_binding_with_no_modifier_reports_none_so_the_view_commits_immediately()
    {
        var keyOnly = new Keymap(
            [new KeyBinding(KeyScope.Global, GestureParser.Parse("F6"), CommandIds.TabMruNext)]);

        Assert.Equal(KeyModifiers.None, MruCycle.ModifiersOf(keyOnly));
        Assert.False(MruCycle.EndsCycle(KeyModifiers.None, Key.LeftCtrl)); // nothing to wait for
    }
}
