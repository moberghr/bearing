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

    // ---- #65: the tab strip's overflow chevron ----------------------------------------------------

    [Fact]
    public Task The_chevron_stays_hidden_while_every_tab_fits() => _ui.Run(async () =>
    {
        // The chevron is the only overflow affordance now, so it appearing when nothing has overflowed would
        // be a standing lie — and the reason the scrollbar was replaced was that it said "there is more"
        // without saying more of what.
        using var shell = await ShellHarness.ShowAsync(nameof(The_chevron_stays_hidden_while_every_tab_fits));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        workspace.NewTab("-- one");
        workspace.NewTab("-- two");
        shell.Pump();

        Assert.False(Chevron(shell).IsVisible);
    });

    [Fact]
    public Task Enough_tabs_light_the_chevron_and_it_carries_the_count() => _ui.Run(async () =>
    {
        // "» 4" — the count is the message. A bare chevron would say only the half the scrollbar already said.
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
        Assert.Contains("»", label);
        // The number has to be a real count, not the tab total and not zero.
        var digits = new string(label.Where(char.IsDigit).ToArray());
        Assert.True(int.TryParse(digits, out var hidden), $"no count on the chevron: {label}");
        Assert.InRange(hidden, 1, workspace.Tabs.Count - 1);
    });

    [Fact]
    public Task The_chevron_opens_a_list_of_every_tab_not_just_the_hidden_ones() => _ui.Run(async () =>
    {
        // Every tab on purpose: a picker whose contents change as you resize the window is one you cannot
        // learn. The chevron's count says how many are hidden; the list is the whole set.
        using var shell = await ShellHarness.ShowAsync(nameof(The_chevron_opens_a_list_of_every_tab_not_just_the_hidden_ones));
        var workspace = shell.Vm.Workspace;
        workspace.Tabs.Clear();
        for (var i = 1; i <= 12; i++) workspace.NewTab($"-- tab {i}");
        shell.Window.Width = 600;
        shell.Pump();

        ClickChevron(shell);
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

        ClickChevron(shell);
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
    /// Click the chevron, having first checked it is actually on screen.
    /// <para>
    /// The precondition is the point, and it was missing: <c>RaiseEvent(Button.ClickEvent)</c> runs the
    /// handler whether the button is visible or not, so a sabotage test that forced the chevron permanently
    /// hidden still saw both picker tests pass. They were testing the picker while claiming to test the
    /// affordance that reaches it.
    /// </para>
    /// </summary>
    private static void ClickChevron(ShellHarness shell)
    {
        var chevron = Chevron(shell);
        Assert.True(chevron.IsVisible, "the chevron is not visible, so a user could not have clicked it");
        chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

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
