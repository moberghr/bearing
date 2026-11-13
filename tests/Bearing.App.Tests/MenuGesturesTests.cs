using Avalonia.Input;
using Bearing.App.Input;
using Xunit;
using KeyBinding = Bearing.App.Input.KeyBinding;

namespace Bearing.App.Tests;

public class MenuGesturesTests
{
    [Fact]
    public void The_keymaps_ctrl_is_the_command_key_in_a_mac_menu()
    {
        var gesture = MenuGestures.From(GestureParser.Parse("Ctrl+Shift+F"), commandKey: true);

        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Shift), gesture);
    }

    [Fact]
    public void Off_the_mac_ctrl_stays_ctrl()
    {
        var gesture = MenuGestures.From(GestureParser.Parse("Ctrl+S"), commandKey: false);

        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), gesture);
    }

    [Fact]
    public void A_gesture_without_ctrl_keeps_its_modifiers()
    {
        var gesture = MenuGestures.From(GestureParser.Parse("Shift+F5"), commandKey: true);

        Assert.Equal(new KeyGesture(Key.F5, KeyModifiers.Shift), gesture);
    }

    [Fact]
    public void A_physical_binding_has_no_menu_gesture()
    {
        var physical = Gesture.ForPhysical(KeyModifiers.Control, PhysicalKey.BracketLeft);

        Assert.Null(MenuGestures.From(physical, commandKey: true));
    }

    [Fact]
    public void The_menu_shows_the_first_logical_binding_and_skips_a_physical_one_ahead_of_it()
    {
        var map = new Keymap(new[]
        {
            new KeyBinding(KeyScope.Editor, Gesture.ForPhysical(KeyModifiers.Control, PhysicalKey.BracketLeft), CommandIds.EditorFoldCurrent),
            new KeyBinding(KeyScope.Editor, GestureParser.Parse("Ctrl+Shift+OemOpenBrackets"), CommandIds.EditorFoldCurrent),
        });

        Assert.Equal(new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Meta | KeyModifiers.Shift),
            MenuGestures.For(map, CommandIds.EditorFoldCurrent, commandKey: true));
    }

    [Fact]
    public void An_unbound_command_has_no_menu_gesture()
        => Assert.Null(MenuGestures.For(new Keymap([]), CommandIds.SettingsOpen, commandKey: true));

    [Fact]
    public void Settings_ships_bound_to_the_platform_settings_key()
        => Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta),
            MenuGestures.For(KeymapDefaults.Build(), CommandIds.SettingsOpen, commandKey: true));

    [Fact]
    public void A_fixed_gesture_in_keymap_text_follows_the_platform()
        => Assert.Equal(new KeyGesture(Key.X, MenuGestures.CommandKey ? KeyModifiers.Meta : KeyModifiers.Control),
            MenuGestures.Parse("Ctrl+X"));
}
