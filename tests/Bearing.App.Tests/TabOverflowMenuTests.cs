using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Bearing.App.Controls;
using Bearing.App.ViewModels;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The tab strip's dropdown — every open tab, click one to go to it, in Visual Studio's place on the strip.
/// <para>
/// What is worth pinning here is what the list <i>says</i>: the two rows stay two groups, the active tab is
/// marked, an unsaved one is marked, a very long name is trimmed but its marker is not, and the two other
/// routes to a tab (expand the strip, the modal picker) are the last two items rather than a second button.
/// </para>
/// </summary>
public class TabOverflowMenuTests
{
    private static EditorTabViewModel Tab(string name, bool dirty = false)
    {
        var tab = new EditorTabViewModel(name, "-- text", $"C:/scripts/{name}.sql");
        if (dirty) tab.Text = "-- edited";   // a named script with an edited buffer is what IsDirty means
        return tab;
    }

    private static MenuFlyout Fill(
        IReadOnlyList<EditorTabViewModel> pinned,
        IReadOnlyList<EditorTabViewModel> unpinned,
        EditorTabViewModel? selected = null,
        Action<EditorTabViewModel>? go = null,
        bool expanded = false,
        Action? toggleExpand = null,
        Action? openPicker = null)
    {
        var menu = new MenuFlyout();
        TabOverflowMenu.Fill(menu, pinned, unpinned, selected,
            go ?? (_ => { }), expanded, toggleExpand ?? (() => { }), openPicker ?? (() => { }));
        return menu;
    }

    private static string[] Headers(MenuFlyout menu)
        => menu.Items.OfType<MenuItem>().Select(i => i.Header as string ?? "").ToArray();

    [Fact]
    public void Every_open_tab_is_listed_in_strip_order()
    {
        // Every tab, not just the hidden ones: a list whose contents change as you resize the window is one
        // you cannot learn. The count on the button says how many are off the edge; the list is the whole set.
        var tabs = new[] { Tab("one"), Tab("two"), Tab("three") };

        var menu = Fill([], tabs);

        Assert.Equal(["one.sql", "two.sql", "three.sql"], Headers(menu).Take(3).ToArray());
    }

    [Fact]
    public void The_pinned_row_is_listed_first_and_kept_apart()
    {
        var pinned = new[] { Tab("kept") };
        var rest = new[] { Tab("scratch") };

        var menu = Fill(pinned, rest);

        Assert.Equal(["kept.sql", "scratch.sql"], Headers(menu).Take(2).ToArray());
        // A separator between them, so the two rows the strip draws read as two groups here too.
        Assert.IsType<Separator>(menu.Items[1]);
    }

    [Fact]
    public void With_nothing_pinned_there_is_no_leading_separator()
    {
        var menu = Fill([], [Tab("only")]);

        Assert.IsType<MenuItem>(menu.Items[0]);
    }

    /// <summary>Nothing else in a menu says "you are here": without the mark the list reads as a set of
    /// destinations, one of which is where you already are.</summary>
    [Fact]
    public void The_active_tab_is_the_only_one_marked()
    {
        var tabs = new[] { Tab("one"), Tab("two"), Tab("three") };

        var menu = Fill([], tabs, selected: tabs[1]);

        var items = menu.Items.OfType<MenuItem>().Take(3).ToArray();
        Assert.Null(items[0].Icon);
        Assert.NotNull(items[1].Icon);
        Assert.Null(items[2].Icon);
    }

    [Fact]
    public void An_unsaved_tab_carries_the_strips_own_dirty_dot()
    {
        var dirty = Tab("edited", dirty: true);
        Assert.True(dirty.IsDirty, "the fixture must be dirty, or this asserts nothing");

        Assert.Equal("edited.sql ●", TabOverflowMenu.Label(dirty));
        Assert.Equal("clean.sql", TabOverflowMenu.Label(Tab("clean")));
    }

    /// <summary>A dropdown as wide as the longest filename someone has open covers the results grid. The
    /// dot survives the trim — a marker lost to the ellipsis is worse than no marker.</summary>
    [Fact]
    public void A_very_long_name_is_trimmed_and_keeps_its_dot()
    {
        var long_ = Tab(new string('q', 80), dirty: true);

        var label = TabOverflowMenu.Label(long_);

        Assert.EndsWith("… ●", label);
        Assert.True(label.Length < 50, $"still {label.Length} characters: {label}");
    }

    [Fact]
    public void Picking_a_tab_goes_to_it()
    {
        var tabs = new[] { Tab("one"), Tab("two") };
        EditorTabViewModel? went = null;

        var menu = Fill([], tabs, go: t => went = t);
        Click(menu.Items.OfType<MenuItem>().ElementAt(1));

        Assert.Same(tabs[1], went);
    }

    [Fact]
    public void The_last_two_items_are_the_other_two_routes_to_a_tab()
    {
        var expanded = 0;
        var picked = 0;

        var menu = Fill([], [Tab("one")], toggleExpand: () => expanded++, openPicker: () => picked++);

        var items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal("Show all tabs in the strip", items[^2].Header);
        Assert.Equal("Go to tab…", items[^1].Header);

        Click(items[^2]);
        Click(items[^1]);
        Assert.Equal(1, expanded);
        Assert.Equal(1, picked);
    }

    /// <summary>The item says what it will do to the strip as it is now — the same "built per press, so the
    /// label can simply be true" rule the tab context menu's Pin / Unpin follows.</summary>
    [Fact]
    public void With_the_strip_already_expanded_the_item_offers_the_way_back()
    {
        var menu = Fill([], [Tab("one")], expanded: true);

        Assert.Equal("Collapse the tab strip", menu.Items.OfType<MenuItem>().ToArray()[^2].Header);
    }

    [Fact]
    public void Refilling_replaces_the_previous_list()
    {
        // The flyout is refilled on every open rather than built once, so a tab that has closed must not
        // still be offered.
        var menu = Fill([], [Tab("one"), Tab("two")]);
        var before = menu.Items.Count;

        TabOverflowMenu.Fill(menu, [], [Tab("one")], null, _ => { }, false, () => { }, () => { });

        Assert.Equal(before - 1, menu.Items.Count);
        Assert.DoesNotContain("two.sql", Headers(menu));
    }

    private static void Click(MenuItem item)
        => item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
}
