using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The Scripts filter reaching inside the files (#47): a script whose <em>contents</em> match shows up with
/// the matching line as its reason, an open tab's live buffer is searched instead of the stale file on disk,
/// and the cheap paths (empty filter, one or two characters, a name hit) never read a file.
/// Pure filesystem — no database required.
/// </summary>
public class ScriptContentFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-content-filter", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private async Task<ShellViewModel> Project([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            new FakeSecretStore(),
            // Autosave off is the case the live-buffer rule exists for: a script can sit edited indefinitely.
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        await vm.InitializeAsync(Path.Combine(_root, name));
        return vm;
    }

    private static async Task<string> Script(ShellViewModel vm, string name, string text)
    {
        var path = Path.Combine(vm.ScriptsDirectory!, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        vm.Scripts.RefreshScripts();
        return path;
    }

    /// <summary>Type the filter (name pass, synchronous) then run the content pass to completion — the
    /// production one is debounced and fire-and-forget, which a test can only race.</summary>
    private static async Task Filter(ShellViewModel vm, string filter)
    {
        vm.Scripts.ScriptFilter = filter;
        await vm.Scripts.SearchContentsNowAsync(filter);
    }

    private static ScriptItem[] Leaves(ShellViewModel vm) => Flatten(vm.Scripts.ScriptNodes).ToArray();

    private static System.Collections.Generic.IEnumerable<ScriptItem> Flatten(System.Collections.Generic.IEnumerable<object> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is ScriptItem item) yield return item;
            else if (node is ScriptFolderViewModel folder)
                foreach (var child in Flatten(folder.Children)) yield return child;
        }
    }

    [Fact]
    public async Task A_file_whose_contents_match_is_shown_with_the_matching_line()
    {
        var vm = await Project();
        await Script(vm, Path.Combine("Reports", "monthly.sql"), "select *\nfrom settlements s\nwhere s.paid");
        await Script(vm, "unrelated.sql", "select 1");

        await Filter(vm, "settlements");

        var hit = Assert.Single(Leaves(vm));
        Assert.Equal("monthly.sql", hit.Name);
        Assert.Equal("from settlements s", hit.MatchLine);
        // The folder came back only because it now has a matching descendant.
        var folder = Assert.Single(vm.Scripts.ScriptNodes.OfType<ScriptFolderViewModel>());
        Assert.Equal(1, folder.Count);
    }

    [Fact]
    public async Task A_name_hit_carries_no_match_line_and_is_never_read_for_one()
    {
        var vm = await Project();
        // The name matches; the contents contain the filter too, and it still reports no content line —
        // the row's own name already says why it is here.
        await Script(vm, "settlements.sql", "select * from settlements");

        await Filter(vm, "settlements");

        var hit = Assert.Single(Leaves(vm));
        Assert.Equal("settlements.sql", hit.Name);
        Assert.Null(hit.MatchLine);
    }

    [Fact]
    public async Task An_open_tabs_unsaved_buffer_is_searched_instead_of_the_file_on_disk()
    {
        var vm = await Project();
        var path = await Script(vm, "draft.sql", "select 1");
        await vm.Workspace.OpenScriptInNewTabAsync(path);
        vm.Workspace.SelectedTab!.Text = "select * from settlements";   // dirty, never written (autosave off)
        Assert.True(vm.Workspace.SelectedTab!.IsDirty);
        Assert.Equal("select 1", await File.ReadAllTextAsync(path));    // disk really is stale

        await Filter(vm, "settlements");

        var hit = Assert.Single(Leaves(vm));
        Assert.Equal("draft.sql", hit.Name);
        Assert.Equal("select * from settlements", hit.MatchLine);
        Assert.True(hit.IsUnsaved);
    }

    [Fact]
    public async Task A_buffer_that_no_longer_matches_takes_the_file_off_the_list()
    {
        var vm = await Project();
        var path = await Script(vm, "draft.sql", "select * from settlements");
        await vm.Workspace.OpenScriptInNewTabAsync(path);
        vm.Workspace.SelectedTab!.Text = "select 1";   // the term was deleted, but only in the buffer

        await Filter(vm, "settlements");

        Assert.Empty(Leaves(vm));
    }

    [Fact]
    public async Task A_short_filter_does_not_reach_into_contents()
    {
        var vm = await Project();
        await Script(vm, "monthly.sql", "select * from ab");

        vm.Scripts.ScriptFilter = "ab";                   // two characters: name pass only
        Assert.Empty(Leaves(vm));

        // And nothing arrives late either: the debounced pass is never started for a filter this short.
        await Task.Delay(400);
        Assert.Empty(Leaves(vm));
    }

    [Fact]
    public async Task Clearing_the_filter_brings_every_script_back_with_no_match_lines()
    {
        var vm = await Project();
        await Script(vm, "monthly.sql", "select * from settlements");
        await Script(vm, "unrelated.sql", "select 1");

        await Filter(vm, "settlements");
        Assert.Single(Leaves(vm));

        vm.Scripts.ScriptFilter = "";
        Assert.Equal(2, Leaves(vm).Length);
        Assert.All(Leaves(vm), s => Assert.Null(s.MatchLine));
    }

    [Fact]
    public async Task A_file_too_large_to_search_is_skipped_rather_than_read()
    {
        var vm = await Project();
        // Past MaxContentBytes (1 MiB): the term is in there, and the pass must not go looking for it.
        await Script(vm, "dump.sql", new string('-', (int)ScriptSearch.MaxContentBytes + 1) + "\nsettlements");
        await Script(vm, "small.sql", "select * from settlements");

        await Filter(vm, "settlements");

        Assert.Equal("small.sql", Assert.Single(Leaves(vm)).Name);
    }

    // ---- Last run on the row (the trailer) ----

    [Fact]
    public async Task A_script_row_shows_where_it_was_last_run_once_the_log_is_read()
    {
        await using var log = new SqliteQueryLog(Path.Combine(_root, "use-log.sqlite"));
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(), log,
            new FileRecentProjects(Path.Combine(_root, "recent.json")), new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings()));
        await vm.InitializeAsync(Path.Combine(_root, "uses"));
        var path = await Script(vm, "report.sql", "select 1");
        var row = vm.Scripts.Scripts.Single(s => s.FullPath == path);
        // No run recorded: the file's edit day, said to be that.
        Assert.StartsWith("No run recorded for this script.", row.UseTip);

        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.Appended += _ => logged.TrySetResult();
        log.Append(new Bearing.Core.Logging.QueryLogEntry
        {
            ExecutedAt = DateTimeOffset.Now, ProviderId = "postgres", ConnectionName = "prod",
            Database = "pagila", SqlText = "select 1", Success = true, ScriptPath = path,
        });
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await vm.Scripts.LoadScriptUsesAsync();

        Assert.Equal("pagila · today", row.UseText);
        Assert.StartsWith("Last run on prod / pagila, ", row.UseTip);

        // A rebuilt tree keeps it without another log read.
        vm.Scripts.RefreshScripts();
        Assert.Equal("pagila · today", vm.Scripts.Scripts.Single(s => s.FullPath == path).UseText);
    }

    // ---- Recent scripts on one connection (the server row's menu) ----

    [Fact]
    public async Task A_servers_recent_scripts_are_the_ones_last_run_there_newest_first()
    {
        await using var log = new SqliteQueryLog(Path.Combine(_root, "recent-log.sqlite"));
        var vm = new ShellViewModel(
            new ProviderRegistry(), new JsonProjectStore(), new JsonSessionStore(), log,
            new FileRecentProjects(Path.Combine(_root, "recent.json")), new FakeSecretStore(),
            settings: SettingsService.InMemory(new AppSettings()));
        await vm.InitializeAsync(Path.Combine(_root, "recent"));
        var report = await Script(vm, "report.sql", "select 1");
        var nested = await Script(vm, Path.Combine("Reports", "monthly.sql"), "select 2");
        var gone = await Script(vm, "gone.sql", "select 3");
        var elsewhere = await Script(vm, "elsewhere.sql", "select 4");
        Guid prod = Guid.NewGuid(), test = Guid.NewGuid();

        // Nothing recorded yet: said as that, not as "never run".
        var none = await vm.Scripts.RecentScriptsAsync(prod);
        Assert.Empty(none.Items);
        Assert.Equal("No runs recorded on this connection", none.Note);

        var pending = 0;
        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        log.Appended += _ => { if (Interlocked.Decrement(ref pending) == 0) logged.TrySetResult(); };
        void Run(string path, Guid connection, string database)
            => log.Append(new Bearing.Core.Logging.QueryLogEntry
            {
                ExecutedAt = DateTimeOffset.Now, ProviderId = "postgres", ConnectionName = "c",
                ConnectionId = connection, Database = database, SqlText = "select 1", Success = true,
                ScriptPath = path,
            });
        pending = 5;
        Run(report, prod, "pagila");
        Run(gone, prod, "pagila");
        Run(nested, prod, "sales");
        Run(elsewhere, test, "pagila");
        Run(report, test, "scratch");   // later, but on the other connection: prod's line keeps pagila
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.Delete(gone);

        var recent = await vm.Scripts.RecentScriptsAsync(prod);

        Assert.Null(recent.Note);
        Assert.Equal(new[] { nested, report }, recent.Items.Select(i => i.Path));
        Assert.Equal(new[] { "sales", "pagila" }, recent.Items.Select(i => i.Database));
        // Named by the path inside the scripts folder, so same-named files in two folders read apart.
        Assert.Equal($"{Path.Combine("Reports", "monthly.sql")}  ·  sales · today", recent.Items[0].Label);
        Assert.Equal("report.sql  ·  pagila · today", recent.Items[1].Label);

        // A script a tab already holds is marked, the others are not.
        Assert.All(recent.Items, i => Assert.False(i.IsOpen));
        await vm.Workspace.OpenScriptInNewTabAsync(report);
        var withOpen = await vm.Scripts.RecentScriptsAsync(prod);
        Assert.Equal(new[] { false, true }, withOpen.Items.Select(i => i.IsOpen));

        // The panel's rows say the same, and follow the tab closing without a rebuild.
        var row = vm.Scripts.Scripts.Single(s => s.FullPath == report);
        Assert.True(row.IsOpen);
        Assert.False(vm.Scripts.Scripts.Single(s => s.FullPath == nested).IsOpen);
        vm.Workspace.Tabs.Remove(vm.Workspace.Tabs.Single(t => t.ScriptPath == report));
        Assert.False(row.IsOpen);
    }
}
