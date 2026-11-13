using Avalonia.Input;
using Bearing.App.Views;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// When the macOS bar replays a key (#186 review): only when AppKit's current key-down <i>is</i> the item's
/// gesture. A menu item chosen by keyboard some other way (Help ▸ Search + Return, Full Keyboard Access,
/// VoiceOver) arrives with that Return as the current event, and must run as a click.
/// </summary>
public class MacKeyEquivalentTests
{
    private const ulong Shift = 1UL << 17;
    private const ulong Control = 1UL << 18;
    private const ulong Option = 1UL << 19;
    private const ulong Command = 1UL << 20;
    private const ulong Function = 1UL << 23;

    private static readonly KeyGesture CmdSlash = new(Key.OemQuestion, KeyModifiers.Meta);

    [Fact]
    public void The_items_own_key_equivalent_matches()
        => Assert.True(MacKeyEquivalent.Matches(CmdSlash, Command, "/"));

    [Fact]
    public void Return_from_help_search_is_not_the_items_key_equivalent()
    {
        Assert.False(MacKeyEquivalent.Matches(CmdSlash, 0, "\r"));
        // Full Keyboard Access / VoiceOver pick an item with Return or Space too.
        Assert.False(MacKeyEquivalent.Matches(CmdSlash, 0, " "));
    }

    [Fact]
    public void Modifiers_must_be_exactly_the_gestures()
    {
        Assert.False(MacKeyEquivalent.Matches(CmdSlash, 0, "/"));
        Assert.False(MacKeyEquivalent.Matches(CmdSlash, Command | Option, "/"));
        Assert.False(MacKeyEquivalent.Matches(CmdSlash, Control, "/"));
        Assert.True(MacKeyEquivalent.Matches(
            new KeyGesture(Key.S, KeyModifiers.Meta | KeyModifiers.Shift), Command | Shift, "S"));
    }

    [Fact]
    public void A_shifted_key_matches_its_shifted_character()
        => Assert.True(MacKeyEquivalent.Matches(
            new KeyGesture(Key.OemQuestion, KeyModifiers.Meta | KeyModifiers.Shift), Command | Shift, "?"));

    [Fact]
    public void Letters_ignore_case_and_other_keys_do_not_match()
    {
        Assert.True(MacKeyEquivalent.Matches(new KeyGesture(Key.S, KeyModifiers.Meta), Command, "s"));
        Assert.False(MacKeyEquivalent.Matches(new KeyGesture(Key.S, KeyModifiers.Meta), Command, "d"));
    }

    [Fact]
    public void Function_keys_use_appkits_code_points_and_ignore_the_fn_flag()
    {
        Assert.True(MacKeyEquivalent.Matches(new KeyGesture(Key.F2), Function, ""));
        Assert.True(MacKeyEquivalent.Matches(new KeyGesture(Key.F5), Function, ""));
        Assert.False(MacKeyEquivalent.Matches(new KeyGesture(Key.F2), Function, "\r"));
    }

    [Fact]
    public void A_key_with_no_known_character_never_replays()
        => Assert.False(MacKeyEquivalent.Matches(new KeyGesture(Key.MediaPlayPause), 0, "x"));

    [Fact]
    public void On_a_layout_where_slash_needs_shift_cmd_slash_still_matches()
    {
        // Croatian: '/' is Shift+7, so ⌘/ arrives as ⌘⇧ with "/".
        Assert.True(MacKeyEquivalent.Matches(CmdSlash, Command | Shift, "/"));
        // US: Shift turns '/' into '?' — that is ⌘⇧/, a different gesture.
        Assert.False(MacKeyEquivalent.Matches(CmdSlash, Command | Shift, "?"));
        // A letter never gets the allowance: ⌘⇧S is not ⌘S.
        Assert.False(MacKeyEquivalent.Matches(new KeyGesture(Key.S, KeyModifiers.Meta), Command | Shift, "s"));
    }
}
