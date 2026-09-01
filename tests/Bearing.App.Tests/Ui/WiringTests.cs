using System.Linq;
using Avalonia;
using System.Threading.Tasks;
using Avalonia.Controls;
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
public class WiringTests
{
    private readonly UiTestSession _ui;

    public WiringTests(UiTestSession ui) => _ui = ui;

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
        // Sizes arrive after the tree, so give the read a chance to land before ordering by it.
        for (var i = 0; i < 20 && database.Children.OfType<RelationNodeViewModel>().All(r => r.Size is null); i++)
        {
            shell.Pump();
            Dispatcher.UIThread.RunJobs();
        }

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
