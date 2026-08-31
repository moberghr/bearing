using Avalonia.Input;
using Bearing.App.Input;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Which mouse button does what on a tab header (#66). Pointer gestures are not in the keymap, so this is
/// the only place the rule is stated — and the handlers that read it are code-behind, which nothing else
/// can reach.
/// </summary>
public class TabPointerGestureTests
{
    [Fact]
    public void Middle_click_closes_a_tab()
        => Assert.True(TabPointerGestures.ClosesTab(PointerUpdateKind.MiddleButtonPressed));

    [Theory]
    [InlineData(PointerUpdateKind.LeftButtonPressed)]
    [InlineData(PointerUpdateKind.RightButtonPressed)]
    [InlineData(PointerUpdateKind.XButton1Pressed)]
    [InlineData(PointerUpdateKind.XButton2Pressed)]
    [InlineData(PointerUpdateKind.Other)]
    public void No_other_press_closes_a_tab(PointerUpdateKind kind)
        // Left selects the tab and right opens its menu; closing on either would take the tab away from
        // under the gesture the user actually made.
        => Assert.False(TabPointerGestures.ClosesTab(kind));

    [Theory]
    [InlineData(PointerUpdateKind.MiddleButtonReleased)]
    [InlineData(PointerUpdateKind.LeftButtonReleased)]
    public void A_release_is_not_a_close(PointerUpdateKind kind)
        // The handler runs on PointerPressed; a release reaching it would close a second time.
        => Assert.False(TabPointerGestures.ClosesTab(kind));

    [Fact]
    public void Only_the_left_button_activates_the_close_button()
    {
        Assert.True(TabPointerGestures.ActivatesCloseButton(PointerUpdateKind.LeftButtonPressed));
        // The regression this pairs with: the X used to fire on any press, so a right-click aimed at the
        // context menu closed the tab the menu was for.
        Assert.False(TabPointerGestures.ActivatesCloseButton(PointerUpdateKind.RightButtonPressed));
        // Middle is the header's gesture; taking it here too would run CloseTabAsync twice for one press.
        Assert.False(TabPointerGestures.ActivatesCloseButton(PointerUpdateKind.MiddleButtonPressed));
    }
}
