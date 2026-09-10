using System;
using System.Linq;
using Avalonia;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using Bearing.App.Editing;
using Bearing.App.Formatting;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Views;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The handlers between a tested view model and a tested policy — the layer a coverage audit found empty.
/// <para>
/// Each of these is two or three lines, which is exactly why none of them had a test: the logic on either
/// side is well covered, and the wiring between looks too small to break. It is also the layer where three of
/// this branch's review findings actually lived.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class WiringTests : IDisposable
{
    private readonly UiTestSession _ui;

    public WiringTests(UiTestSession ui) => _ui = ui;

    /// <summary>
    /// Put Core's timezone hooks back the way the process found them.
    /// <para>
    /// They are process-wide mutable statics, and <c>AvaloniaTestIsolationLevel.PerTest</c> resets the
    /// <c>Application</c>, not those. Left installed, every later settings test in the run would pass on this
    /// class's setup instead of its own — the shape §4.5 records having already been bitten by, where an
    /// unconditional static let whichever test ran first decide it for all the rest.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        SettingsCatalog.TimeZoneSuggestions = null;
        SettingsCatalog.TimeZoneValidator = null;
        SettingsCatalog.TimeZoneDescriber = null;
    }

    // ---- #76: the sort menu items ----------------------------------------------------------------

    [Fact]
    public Task The_sort_menu_items_reach_the_database_node() => _ui.Run(async () =>
    {
        // The ordering itself is thoroughly tested on the view model; what was not is that the two menu items
        // are wired to it at all.
        using var shell = await ShellHarness.ShowAsync(nameof(The_sort_menu_items_reach_the_database_node),
            new DemoProvider());
        await shell.Vm.StartDemoAsync(shell.ProjectDirectory, DemoMode.WelcomeScript);
        shell.Pump();

        var server = shell.Vm.Connections.ServerNodes.First();
        await server.EnsureChildrenAsync();
        var database = (DatabaseNodeViewModel)server.Children.First();
        await database.EnsureChildrenAsync();
        // Sizes arrive after the tree, so wait for them before ordering by them. Any(size is null), not
        // All(size is null): the loop has to keep going while *some* are still missing, and the All form
        // stopped the moment the first one landed — which is how ordering by size becomes order-dependent.
        for (var i = 0; i < 40 && database.Children.OfType<RelationNodeViewModel>().Any(r => r.Size is null); i++)
        {
            shell.Pump();
            Dispatcher.UIThread.RunJobs();
        }
        Assert.All(database.Children.OfType<RelationNodeViewModel>(), r => Assert.NotNull(r.Size));

        var sidebar = shell.Window.GetVisualDescendants().OfType<Bearing.App.Controls.SidebarView>().First();
        var bySize = MenuItemNamed(sidebar, "Sort tables by size");
        var byName = MenuItemNamed(sidebar, "Sort tables by name");
        Assert.NotNull(bySize);
        Assert.NotNull(byName);

        // Invoked the way a click does, with the node as the item's DataContext.
        bySize!.DataContext = database;
        bySize.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var afterSize = Names(database);

        byName!.DataContext = database;
        byName.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var afterName = Names(database);

        Assert.NotEqual(afterSize, afterName);
        Assert.Equal("document", afterSize[0]);   // 9 MB, the biggest in the demo catalog
    });

    private static string[] Names(DatabaseNodeViewModel database)
        => database.Children.OfType<RelationNodeViewModel>()
            .Select(r => r.Title.Contains('.') ? r.Title.Split('.')[^1] : r.Title)
            .ToArray();

    private static MenuItem? MenuItemNamed(Visual root, string header)
        => root.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => m.Header as string == header)
           ?? Flyouts(root).FirstOrDefault(m => m.Header as string == header);

    /// <summary>Context-menu items are not in the visual tree until the menu opens, so they are reached
    /// through the owning control's ContextMenu instead.</summary>
    private static System.Collections.Generic.IEnumerable<MenuItem> Flyouts(Visual root)
        => root.GetVisualDescendants()
            .OfType<Control>()
            .Select(c => c.ContextMenu)
            .Where(m => m is not null)
            .SelectMany(m => m!.Items.OfType<MenuItem>());

    // ---- #65: the tab strip's end button — the tab dropdown and its count -------------------------

    [Fact]
    public Task The_button_carries_no_count_while_every_tab_fits() => _ui.Run(async () =>
    {
        // It is a dropdown of the open tabs now, so it stays put — but the *count* still may not claim tabs
        // are hidden when none are. That claim is what retired the scrollbar before it.
        using var shell = await ShellHarness.ShowAsync(nameof(The_button_carries_no_count_while_every_tab_fits));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        workspace.NewTab("-- one");
        workspace.NewTab("-- two");
        shell.Pump();

        var chevron = Chevron(shell);
        Assert.True(chevron.IsVisible, "the dropdown is the strip's permanent affordance");
        Assert.DoesNotContain("0", chevron.Content as string ?? "");
    });

    [Fact]
    public Task Enough_tabs_light_the_chevron_and_it_carries_the_count() => _ui.Run(async () =>
    {
        // "▾ 4" — the caret is the dropdown, the count is what the strip cannot show you.
        using var shell = await ShellHarness.ShowAsync(nameof(Enough_tabs_light_the_chevron_and_it_carries_the_count));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 40; i++) workspace.NewTab($"-- tab {i}");
        shell.Window.Width = 700;
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var chevron = Chevron(shell);
        Assert.True(chevron.IsVisible, "40 tabs in a 700px window did not overflow");

        var label = chevron.Content as string ?? "";
        Assert.Contains("▾", label);
        // The number has to be a real count, not the tab total and not zero.
        var digits = new string(label.Where(char.IsDigit).ToArray());
        Assert.True(int.TryParse(digits, out var hidden), $"no count on the chevron: {label}");
        Assert.InRange(hidden, 1, workspace.Tabs.Count - 1);
    });

    [Fact]
    public Task Ctrl_E_opens_a_list_of_every_tab_not_just_the_hidden_ones() => _ui.Run(async () =>
    {
        // Every tab on purpose: a picker whose contents change as you resize the window is one you cannot
        // learn. The chevron's count says how many are hidden; the list is the whole set.
        // The keystroke, not the chevron: pressing the chevron expands the strip now (below), and the modal
        // list stayed on Ctrl+E.
        using var shell = await ShellHarness.ShowAsync(nameof(Ctrl_E_opens_a_list_of_every_tab_not_just_the_hidden_ones));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 12; i++) workspace.NewTab($"-- tab {i}");
        shell.Window.Width = 600;
        shell.Pump();

        OpenTabPicker(shell);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        // The overlay lives in the window's OverlayLayer, and its search box carries the placeholder.
        var search = shell.Window.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.PlaceholderText == "Go to tab…");
        Assert.NotNull(search);

        var rows = shell.Window.GetVisualDescendants().OfType<ListBox>()
            .FirstOrDefault(l => l.ItemCount > 0);
        Assert.NotNull(rows);
        Assert.Equal(workspace.Tabs.Count, rows!.ItemCount);
    });

    [Fact]
    public Task Picking_a_tab_from_the_list_selects_it() => _ui.Run(async () =>
    {
        // The point of the whole feature: you looked at a list and picked, instead of cycling blind.
        using var shell = await ShellHarness.ShowAsync(nameof(Picking_a_tab_from_the_list_selects_it));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 12; i++) workspace.NewTab($"-- tab {i}");
        var wanted = workspace.Tabs[9];
        workspace.SelectedTab = workspace.Tabs[0];
        shell.Window.Width = 600;
        shell.Pump();

        OpenTabPicker(shell);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var rows = shell.Window.GetVisualDescendants().OfType<ListBox>().First(l => l.ItemCount > 0);
        // Prove this is the picker and not some other list in the shell, or index 9 below means nothing.
        Assert.Equal(workspace.Tabs.Count, rows.ItemCount);
        rows.SelectedIndex = 9;
        // Enter is what commits a pick in FilterableListOverlay.
        rows.RaiseEvent(new KeyEventArgs { Key = Key.Enter, RoutedEvent = InputElement.KeyDownEvent });
        shell.Pump();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(wanted, workspace.SelectedTab);
    });

    /// <summary>
    /// The shell renders with an overflowing tab strip.
    /// <para>
    /// The regression is real and a render capture is what found it: the chevron is docked beside the strip,
    /// so showing it narrows the viewport its own count is read from — the count changed <i>because</i> the
    /// chevron appeared, re-laid out, and changed back. Avalonia reports that as
    /// <c>Infinite layout loop detected</c> from inside <c>MediaContext.Render</c>, and the window never
    /// renders at all.
    /// </para>
    /// <para>
    /// Two things this test needs, and the first two versions of it had neither, so both passed with every
    /// safeguard removed and pinned nothing. It must <b>render</b> — the detector lives in the render pass,
    /// not the layout pass. And it must reproduce the <b>exact</b> arrangement: the loop needs the
    /// scroll-into-view that follows selecting the last tab, so a test that merely resizes a window with many
    /// tabs converges happily. These are the conditions from <c>LookProbe.TabStripOverflow</c>, which is where
    /// it actually threw.
    /// </para>
    /// </summary>
    [Fact]
    public Task An_overflowing_strip_renders_instead_of_looping() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(An_overflowing_strip_renders_instead_of_looping));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        foreach (var name in new[]
                 {
                     "quarterly-revenue-reconciliation-by-store-and-category-v7-FINAL",
                     "daily-revenue", "audit-trail", "store-health", "payment-recon", "customer-churn",
                     "index-bloat", "slow-queries", "scratch-1", "scratch-2", "scratch-3",
                 })
            workspace.NewTab($"-- {name}").DisplayName = name;
        workspace.SetPinned(workspace.Tabs[1], true);
        // The last tab, so BringSelectionIntoView scrolls the strip — which is the ingredient that turns the
        // chevron's self-dependence into a cycle rather than a settled disagreement.
        workspace.SelectedTab = workspace.Tabs[^1];
        shell.Window.Width = 900;
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        // The render is the assertion: this throws InvalidOperationException("Infinite layout loop detected")
        // when the chevron's footprint feeds back into its own count.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = shell.Window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        // And the chevron is up with a sane count, so a render that succeeded by not drawing it fails here.
        var chevron = Chevron(shell);
        Assert.True(chevron.IsVisible, "the strip overflowed but no chevron appeared");
        var digits = new string((chevron.Content as string ?? "").Where(char.IsDigit).ToArray());
        Assert.True(int.TryParse(digits, out var hidden) && hidden > 0, "the chevron carries no count");
    });

    private static Button Chevron(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "TabOverflowButton");

    /// <summary>
    /// Press the strip's end button with the pointer, and return the dropdown it opened.
    /// <para>
    /// A real press, not <c>RaiseEvent(Button.ClickEvent)</c>, for two reasons. The button opens its own
    /// <c>Flyout</c> from <c>OnClick</c>, which a raised event skips entirely — so a raised click would
    /// assert against a menu nobody opened. And the precondition is the point: a raised event runs whether
    /// the button is on screen or not, so a sabotage that hid it permanently used to leave these tests
    /// green.
    /// </para>
    /// </summary>
    private static MenuFlyout OpenTabDropdown(ShellHarness shell)
    {
        var chevron = Chevron(shell);
        Assert.True(chevron.IsVisible, "the button is not visible, so a user could not have pressed it");
        var at = chevron.TranslatePoint(
            new Point(chevron.Bounds.Width / 2, chevron.Bounds.Height / 2), shell.Window);
        Assert.NotNull(at);

        shell.Window.MouseDown(at!.Value, MouseButton.Left);
        shell.Window.MouseUp(at.Value, MouseButton.Left);
        shell.Pump();

        var menu = Assert.IsType<MenuFlyout>(chevron.Flyout);
        Assert.True(menu.IsOpen, "the press did not open the dropdown");
        return menu;
    }

    /// <summary>
    /// Pick one item out of an open dropdown.
    /// <para>
    /// The menu is dismissed afterwards, because a raised <c>Click</c> does not do it the way a real pick
    /// does — and a flyout left open swallows the next press on the button as a dismiss, so the second
    /// visit in a test would find nothing open.
    /// </para>
    /// </summary>
    private static void Pick(MenuFlyout menu, string header)
    {
        var item = Assert.Single(menu.Items.OfType<MenuItem>(), i => i.Header as string == header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide();
    }

    /// <summary>
    /// Ctrl+E, through the editor, which is where focus sits in the running app. Not a direct call to the
    /// command: <c>KeybindingTests</c> proves the keymap resolves the gesture and cannot prove the keystroke
    /// ever reaches the window — the editor's tunnel handler and AvaloniaEdit's own bindings both sit in
    /// between (the reason <c>PaneShortcutTests</c> exists).
    /// </summary>
    private static void OpenTabPicker(ShellHarness shell)
    {
        var editor = shell.Window.GetVisualDescendants().OfType<TextEditor>().First(e => e.Name == "Editor");
        editor.TextArea.Focus();
        shell.Pump();
        Assert.True(editor.TextArea.IsFocused, "the editor never took focus, so the keystroke proves nothing");
        shell.Window.KeyPress(Key.E, RawInputModifiers.Control, PhysicalKey.E, null);
    }

    // ---- the dropdown: every open tab, and the two other routes to one ---------------------------

    /// <summary>
    /// Pressing the button lists every open tab, and picking one goes to it. The list is built at open time
    /// from the live workspace, so this is the wiring that makes it not a fixed menu.
    /// </summary>
    [Fact]
    public Task The_button_drops_down_every_open_tab_and_picking_one_goes_to_it() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_button_drops_down_every_open_tab_and_picking_one_goes_to_it));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 12; i++) workspace.NewTab($"-- tab {i}").DisplayName = $"tab-{i:00}";
        var wanted = workspace.Tabs[9];
        workspace.SelectedTab = workspace.Tabs[0];
        shell.Window.Width = 700;
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var menu = OpenTabDropdown(shell);

        // Every tab, plus the two footer items — a list whose contents changed with the window would be one
        // you could not learn.
        Assert.Equal(workspace.Tabs.Count + 2, menu.Items.OfType<MenuItem>().Count());
        Pick(menu, "tab-10");
        shell.Pump();

        Assert.Same(wanted, workspace.SelectedTab);
    });

    /// <summary>
    /// …and the dropdown actually <b>renders</b>. This is the half a flyout test can silently miss: the
    /// menu reported <c>IsOpen</c> and held every item while the popup on screen was empty, because the
    /// presenter had been created and measured before the items existed. Asserting a property is not
    /// asserting that anything was drawn (§4.3).
    /// </summary>
    [Fact]
    public Task The_dropdown_renders_its_items_not_just_an_empty_popup() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_dropdown_renders_its_items_not_just_an_empty_popup));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 6; i++) workspace.NewTab($"-- tab {i}").DisplayName = $"tab-{i}";
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        OpenTabDropdown(shell);
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var presenter = Popups(shell).OfType<MenuFlyoutPresenter>().FirstOrDefault();
        Assert.NotNull(presenter);
        var realized = presenter!.GetVisualDescendants().OfType<MenuItem>().Count();
        Assert.True(realized >= workspace.Tabs.Count,
            $"the popup drew {realized} items for {workspace.Tabs.Count} tabs");
        Assert.True(presenter.Bounds.Height > 0 && presenter.Bounds.Width > 0,
            $"the popup measured {presenter.Bounds}");
    });

    /// <summary>
    /// The dropdown's own "show all tabs" item expands the strip: the rows stop scrolling and wrap, so the
    /// tabs that were off the edge are on screen and clickable where they lie.
    /// <para>
    /// Two halves, each failing on its own for a different reason: the strip has to have grown past one row
    /// (it wrapped) and nothing may be left beyond the right edge (it is not still scrolling).
    /// </para>
    /// </summary>
    [Fact]
    public Task The_dropdowns_expand_item_wraps_the_strip() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_dropdowns_expand_item_wraps_the_strip));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 24; i++) workspace.NewTab($"-- tab {i}");
        shell.Window.Width = 700;
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var scroller = TabScroll(shell);
        var oneRow = scroller.Bounds.Height;
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width + 1,
            "the fixture must overflow, or there is nothing to expand");

        Pick(OpenTabDropdown(shell), "Show all tabs in the strip");
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.True(scroller.Bounds.Height > oneRow,
            $"the strip did not grow: still {scroller.Bounds.Height:0.#}px");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1,
            $"tabs are still off the right edge: extent {scroller.Extent.Width:0.#} > viewport {scroller.Viewport.Width:0.#}");
    });

    [Fact]
    public Task The_item_then_offers_the_way_back() => _ui.Run(async () =>
    {
        using var shell = await ShellHarness.ShowAsync(nameof(The_item_then_offers_the_way_back));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 24; i++) workspace.NewTab($"-- tab {i}");
        shell.Window.Width = 700;
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        var scroller = TabScroll(shell);
        var oneRow = scroller.Bounds.Height;

        Pick(OpenTabDropdown(shell), "Show all tabs in the strip");
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();
        Assert.True(scroller.Bounds.Height > oneRow, "the fixture must expand before it can collapse");

        // The item says what it will do to the strip as it is now, so the second visit reads the other way.
        Pick(OpenTabDropdown(shell), "Collapse the tab strip");
        shell.Pump();
        Dispatcher.UIThread.RunJobs();
        shell.Pump();

        Assert.Equal(oneRow, scroller.Bounds.Height, 1);
    });

    /// <summary>
    /// Every visual in the window's popups. A flyout's presenter is not in the window's own tree — it lives
    /// under the popup host — so the search starts from the open popup roots.
    /// </summary>
    private static IEnumerable<Visual> Popups(ShellHarness shell)
        => shell.Window.GetVisualDescendants()
            .OfType<Popup>()
            .Where(p => p.IsOpen && p.Child is not null)
            .SelectMany(p => p.Child!.GetSelfAndVisualDescendants())
            .Concat(shell.Window.GetVisualDescendants());

    private static ScrollViewer TabScroll(ShellHarness shell)
        => shell.Window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "TabScroll");

    // ---- #23: the connection dialog's encryption default -----------------------------------------

    [Fact]
    public Task A_remote_host_typed_into_a_new_connection_raises_the_encryption_default() => _ui.Run(() =>
    {
        // TlsPolicy.DefaultFor is well tested; that the dialog *follows the host as it is typed* was not.
        // The default has to be computed from the host that ends up in the record, not from the "localhost"
        // the box is pre-filled with.
        var dialog = NewConnectionDialog();
        var host = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "HostBox");
        var picker = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TlsBox");

        Assert.Equal(TlsPolicy.Label(TlsMode.Prefer), picker.SelectedItem);   // localhost

        host.Text = "db.example.com";
        Pump(dialog);

        Assert.Equal(TlsPolicy.Label(TlsMode.Require), picker.SelectedItem);
        dialog.Close();
    });

    [Fact]
    public Task Once_the_user_picks_a_mode_the_host_stops_moving_it() => _ui.Run(() =>
    {
        // Otherwise typing the rest of a hostname would silently undo a deliberate choice.
        var dialog = NewConnectionDialog();
        var host = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "HostBox");
        var picker = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TlsBox");

        picker.SelectedItem = TlsPolicy.Label(TlsMode.Disable);
        Pump(dialog);
        host.Text = "db.example.com";
        Pump(dialog);

        Assert.Equal(TlsPolicy.Label(TlsMode.Disable), picker.SelectedItem);
        dialog.Close();
    });

    [Fact]
    public Task The_warning_says_what_the_chosen_mode_leaves_open() => _ui.Run(() =>
    {
        var dialog = NewConnectionDialog();
        var picker = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TlsBox");

        picker.SelectedItem = TlsPolicy.Label(TlsMode.Require);
        Pump(dialog);
        var warning = dialog.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "TlsWarningText");
        Assert.Equal(TlsPolicy.Advice(TlsMode.Require), warning.Text);

        // …and the strongest mode has nothing to warn about, so the block goes away entirely.
        picker.SelectedItem = TlsPolicy.Label(TlsMode.VerifyFull);
        Pump(dialog);
        var block = dialog.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TlsWarning");
        Assert.False(block.IsVisible);
        dialog.Close();
    });

    // ---- #77: the timestamp-zone settings row ----------------------------------------------------

    [Fact]
    public Task A_zone_the_picker_does_not_offer_is_still_typeable() => _ui.Run(() =>
    {
        // The whole reason the row is an editable ComboBox: a settings file written on Linux carries IANA ids,
        // and on Windows those are not in the suggestion list. Rejecting them would make the file unopenable
        // on the other platform.
        var (window, settings) = OpenSettings("results.displayTimeZone");
        var (combo, note) = ZoneRow(window);

        Assert.NotEmpty(combo.ItemsSource!.Cast<object>());   // the static-initializer bug left this empty

        combo.Text = "Europe/Zagreb";
        Blur(window, combo);

        Assert.Equal("Europe/Zagreb", settings.Current.DisplayTimeZone);
        Assert.Equal("Europe/Zagreb", combo.Text);
        Assert.NotEmpty(note.Text ?? "");
        window.Close();
    });

    [Fact]
    public Task A_zone_that_does_not_resolve_is_refused_and_the_box_snaps_back() => _ui.Run(() =>
    {
        // Storing it would badge every timestamptz column with a zone that does not exist. Leaving the typo
        // on screen is the other half: a box reading "Mars/Olympus" beside timestamps still in UTC says the
        // setting took when it did not.
        var (window, settings) = OpenSettings("results.displayTimeZone");
        var (combo, _) = ZoneRow(window);
        var before = settings.Current.DisplayTimeZone;

        combo.Text = "Mars/Olympus_Mons";
        Blur(window, combo);

        Assert.Equal(before, settings.Current.DisplayTimeZone);
        Assert.Equal(before, combo.Text);
        window.Close();
    });

    [Fact]
    public Task The_row_says_which_zone_system_actually_means() => _ui.Run(() =>
    {
        // "system" on its own does not tell the user what their timestamps will read as, which is the only
        // question the setting exists to answer.
        var (window, _) = OpenSettings("results.displayTimeZone");
        var (combo, note) = ZoneRow(window);

        combo.Text = DisplayTimeZone.SystemId;
        Blur(window, combo);

        Assert.NotEqual(DisplayTimeZone.SystemId, note.Text);
        Assert.NotEmpty(note.Text ?? "");
        window.Close();
    });

    private static (Window Window, SettingsService Settings) OpenSettings(string searchKey)
    {
        // The hooks the app installs at startup. A headless test never reaches
        // OnFrameworkInitializationCompleted (§4.5), so without this the picker is empty and the validator
        // accepts everything — which would make these three tests pass against a row that does nothing.
        DisplayTimeZone.InstallSettingsHooks();
        var settings = new SettingsService(new FakeSettingsStore());
        var window = new SettingsWindow(settings);
        window.Show();
        // Filter to the one row: the catalog is long, and the search box is how the window narrows it.
        var search = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.PlaceholderText is { Length: > 0 });
        search.Text = searchKey;
        Pump(window);
        return (window, settings);
    }

    /// <summary>
    /// Commit the row the way the user does: by focusing it and then focusing something else.
    /// <para>
    /// Not a raised LostFocus — in Avalonia 12 that event carries <c>FocusChangedEventArgs</c>, so a
    /// hand-rolled <c>RoutedEventArgs</c> throws inside the handler adapter. Real focus is also the honest
    /// test, and §4.5 says to assert it landed rather than trust that it did.
    /// </para>
    /// </summary>
    private static void Blur(Window window, ComboBox combo)
    {
        combo.Focus();
        Pump(window);
        Assert.True(combo.IsFocused || combo.IsKeyboardFocusWithin, "the row never took focus");

        var elsewhere = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.PlaceholderText is { Length: > 0 });
        elsewhere.Focus();
        Pump(window);
        Assert.False(combo.IsKeyboardFocusWithin, "focus never left the row, so it was never committed");
    }

    private static (ComboBox Combo, TextBlock Note) ZoneRow(Window window)
    {
        var combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEditable);
        var note = combo.FindAncestorOfType<StackPanel>()!
            .Children.OfType<TextBlock>().First();
        return (combo, note);
    }

    // ---- scrollbars sit beside content, not on top of it ------------------------------------------

    /// <summary>
    /// The settings list's scrollbar takes its own column instead of floating over the rows.
    /// <para>
    /// Avalonia's default is an auto-hiding overlay, which drew the bar straight through the <c>pt</c> beside
    /// both font-size spinners and crowded every checkbox in the list. Reported as "covering text", and the
    /// same call the results grid already made for the same reason.
    /// </para>
    /// <para>
    /// Asserted on the property rather than on pixels: what the bar looks like is eyeball QA (§4.3), but
    /// whether it reserves space is a fact about layout. The rendered check that it no longer crosses the
    /// unit label was done by capture.
    /// </para>
    /// </summary>
    [Fact]
    public Task The_settings_list_reserves_room_for_its_scrollbar() => _ui.Run(() =>
    {
        var (window, _) = OpenSettings("");

        // Identified by its content, not by its scrollbar settings: the category list and the combo boxes
        // bring ScrollViewers of their own, and one of those also disables horizontal scrolling.
        var body = window.GetVisualDescendants().OfType<ScrollViewer>()
            .First(v => v.Content is StackPanel { Children.Count: > 3 });

        Assert.False(ScrollViewer.GetAllowAutoHide(body),
            "the settings scrollbar auto-hides, so it floats over the rows instead of taking a column");
        window.Close();
    });

    // ---- syntax highlighting: the grammar is installed -------------------------------------------

    [Fact]
    public Task The_sql_grammar_is_found_and_installed_on_the_editor() => _ui.Run(() =>
    {
        // §4.5 rules out asserting the colours — the tokenizer colours a line as its visual line is drawn, and
        // a suite written against that was dropped as flaky. What is deterministic, and what actually broke
        // highlighting in practice, is one step earlier: GetLanguageByExtension(".sql") returning null. The
        // install then silently does nothing — no exception, no colour — so this guards the silent half.
        var registry = EditorChrome.SqlRegistry;
        var sql = registry.GetLanguageByExtension(".sql");

        Assert.NotNull(sql);
        Assert.False(string.IsNullOrWhiteSpace(registry.GetScopeByLanguageId(sql!.Id)),
            "the grammar package no longer maps .sql to a scope, so highlighting would silently do nothing");

        var editor = new TextEditor { Text = "-- a comment\nselect 1;" };
        var before = editor.TextArea.TextView.LineTransformers.Count;
        EditorChrome.InstallSqlHighlighting(editor);

        // The transformer is the thing that colours; its absence is highlighting being off.
        Assert.True(editor.TextArea.TextView.LineTransformers.Count > before,
            "InstallSqlHighlighting added no line transformer");
    });

    // ---- the connection dialog's environment presets ---------------------------------------------

    [Fact]
    public Task A_preset_relabels_another_preset_but_never_a_typed_label() => _ui.Run(() =>
    {
        // The colour always follows the click; the label only does when it is still one of ours. Clicking
        // Local and then Production used to leave the connection reading "local" in red.
        var dialog = NewConnectionDialog();
        var env = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "EnvBox");
        var hex = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "EnvColorBox");
        var confirmWrites = dialog.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == "ConfirmWritesBox");

        Preset(dialog, "Local");
        Assert.Equal("local", env.Text);
        Assert.Equal("#3FB950", hex.Text);

        Preset(dialog, "Production");
        Assert.Equal("production", env.Text);
        Assert.Equal("#E5484D", hex.Text);
        Assert.True(confirmWrites.IsChecked);

        env.Text = "staging-eu";
        Preset(dialog, "Staging");
        Assert.Equal("staging-eu", env.Text);      // the user's own label survives
        Assert.Equal("#D29922", hex.Text);         // but the hue is what they clicked
        dialog.Close();
    });

    [Fact]
    public Task The_colour_picker_and_the_hex_box_track_each_other() => _ui.Run(() =>
    {
        var dialog = NewConnectionDialog();
        var hex = dialog.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "EnvColorBox");
        var picker = dialog.GetVisualDescendants().OfType<ColorPicker>().First(p => p.Name == "EnvColorPicker");

        // Typed (or preset) hex reaches the picker...
        hex.Text = "#3FB950";
        Pump(dialog);
        Assert.Equal(Color.Parse("#3FB950"), picker.Color);

        // ...and a pick writes the value back in the #RRGGBB the record stores, without the guard letting
        // the round trip re-enter and undo it.
        picker.Color = Color.Parse("#D29922");
        Pump(dialog);
        Assert.Equal("#D29922", hex.Text);
        Assert.Equal(Color.Parse("#D29922"), picker.Color);

        // The swatch is the picker's button content, bound to the box — so it shows what will be saved.
        var swatch = dialog.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ColorSwatch");
        Assert.Equal(Color.Parse("#D29922"), Assert.IsType<SolidColorBrush>(swatch.Background).Color);
        dialog.Close();
    });

    [Fact]
    public Task Opening_the_swatch_builds_the_picker() => _ui.Run(() =>
    {
        // What a new package's theme include can break, and it breaks at open time: the flyout body and its
        // primitives all resolve out of ColorPicker's own Fluent dictionary, merged in App.axaml.
        var dialog = NewConnectionDialog();
        var picker = dialog.GetVisualDescendants().OfType<ColorPicker>().First(p => p.Name == "EnvColorPicker");
        var button = picker.GetVisualDescendants().OfType<DropDownButton>().First();

        var flyout = Assert.IsType<Flyout>(button.Flyout);
        flyout.ShowAt(button);
        Pump(dialog);
        Assert.True(flyout.IsOpen, "the picker's flyout did not open");

        // Present and *templated*: an unresolved control theme would leave the spectrum an empty shell
        // rather than throw, so the visual children are the part worth asserting.
        var spectrum = ((Control)flyout.Content!).GetVisualDescendants().OfType<ColorSpectrum>().First();
        Assert.NotEmpty(spectrum.GetVisualChildren());
        flyout.Hide();
        dialog.Close();
    });

    private static void Preset(Window dialog, string label)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>()
            .First(b => b.Content as string == label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(dialog);
    }

    private static ConnectionDialog NewConnectionDialog()
    {
        var dialog = new ConnectionDialog(existing: null, existingPassword: null,
            test: (_, _, _) => Task.FromResult(false));
        dialog.Show();
        Pump(dialog);
        return dialog;
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
