using System.Linq;
using Avalonia.Input;
using Bearing.App.Input;
using Xunit;

namespace Bearing.App.Tests;

public class MruListTests
{
    private sealed class Item { public required string Name; public override string ToString() => Name; }

    [Fact]
    public void Use_moves_to_front_most_recent_first()
    {
        var a = new Item { Name = "a" }; var b = new Item { Name = "b" }; var c = new Item { Name = "c" };
        var mru = new MruList<Item>();
        mru.Use(a); mru.Use(b); mru.Use(c);            // c most recent
        Assert.Equal(new[] { c, b, a }, mru.Items);
        mru.Use(a);                                    // touching a promotes it
        Assert.Equal(new[] { a, c, b }, mru.Items);
    }

    [Fact]
    public void Sync_prunes_gone_items_and_appends_newcomers_as_least_recent()
    {
        var a = new Item { Name = "a" }; var b = new Item { Name = "b" }; var c = new Item { Name = "c" };
        var mru = new MruList<Item>();
        mru.Use(a); mru.Use(b);                        // [b, a]
        mru.Sync(new[] { b, c });                      // a gone, c is new
        Assert.Equal(new[] { b, c }, mru.Items);       // b stays front, c appended last
    }

    [Fact]
    public void Remove_drops_the_item()
    {
        var a = new Item { Name = "a" }; var b = new Item { Name = "b" };
        var mru = new MruList<Item>();
        mru.Use(a); mru.Use(b);
        mru.Remove(b);
        Assert.Equal(new[] { a }, mru.Items);
    }
}

public class TabAndFocusBindingTests
{
    private const PhysicalKey NoPhys = PhysicalKey.None;
    private static readonly Keymap Defaults = KeymapDefaults.Build();

    [Fact]
    public void Mru_and_visual_tab_switching_are_on_distinct_gestures()
    {
        Assert.Equal(CommandIds.TabMruNext, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.Tab, NoPhys));
        Assert.Equal(CommandIds.TabMruPrev, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.Tab, NoPhys));
        Assert.Equal(CommandIds.TabNext, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.PageDown, NoPhys));
        Assert.Equal(CommandIds.TabPrev, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.PageUp, NoPhys));
    }

    [Theory]
    [InlineData(Key.D1, "tab.goto1")]
    [InlineData(Key.D5, "tab.goto5")]
    [InlineData(Key.D9, "tab.goto9")]
    public void Ctrl_digit_jumps_to_a_tab(Key key, string commandId)
        => Assert.Equal(commandId, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, key, NoPhys));

    [Fact]
    public void Focus_and_picker_shortcuts_are_bound()
    {
        Assert.Equal(CommandIds.FocusEditor, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control, Key.D0, NoPhys));
        Assert.Equal(CommandIds.FocusResults, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.D0, NoPhys));
        Assert.Equal(CommandIds.SelectConnection, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.C, NoPhys));
        Assert.Equal(CommandIds.SelectDatabase, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.D, NoPhys));
        Assert.Equal(CommandIds.SelectProject, Defaults.Resolve(KeyScope.Global, KeyModifiers.Control | KeyModifiers.Shift, Key.J, NoPhys));
    }
}
