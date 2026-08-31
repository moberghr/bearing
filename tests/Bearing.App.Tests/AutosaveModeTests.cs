using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bearing.App.Services;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.Core.Workspace;
using Bearing.Data;
using Bearing.Persistence;
using Bearing.Testing;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// Configurable autosave (roadmap: scratch scripts phase 3). The mode governs <b>named scripts</b>;
/// a scratch buffer is written at the losing-it checkpoints in every mode, <c>Off</c> included, because
/// its file is the buffer's only home.
/// </summary>
public class AutosaveModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-autosave", Guid.NewGuid().ToString("N"));

    /// <summary>One store for the whole test, so a secret saved through one view model still
    /// resolves through the next — the on-disk store this replaced was shared the same way.</summary>
    private readonly FakeSecretStore _secrets = new();

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private ShellViewModel NewVm(AutosaveMode mode, IDialogService? dialogs = null) => new(
        new ProviderRegistry(),
        new JsonProjectStore(),
        new JsonSessionStore(),
        new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
        new FileRecentProjects(Path.Combine(_root, "recent.json")),
        _secrets,
        dialogs: dialogs ?? new FakeDialogs(),
        settings: SettingsService.InMemory(new AppSettings { AutosaveMode = mode }));

    private async Task<ShellViewModel> Project(AutosaveMode mode, IDialogService? dialogs = null,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var vm = NewVm(mode, dialogs);
        await vm.InitializeAsync(Path.Combine(_root, name + mode));
        return vm;
    }

    /// <summary>A named script tab with clean content on disk.</summary>
    private static async Task<(EditorTabViewModel Tab, string Path)> NamedScript(ShellViewModel vm, string name = "r.sql")
    {
        vm.Workspace.NewTab();                       // keep a spare so closing never auto-replaces
        var tab = vm.Workspace.Tabs[0];
        var path = Path.Combine(vm.ScriptsDirectory!, name);
        await vm.Workspace.SaveScriptAsync(tab, path, "select 1;");
        Assert.False(tab.IsScratch);
        return (tab, path);
    }

    private static async Task<string> WaitForWrite(string path, string expected)
    {
        for (var i = 0; i < 60; i++)
        {
            if (await TryReadAsync(path) == expected) return expected;
            await Task.Delay(50);
        }
        return await TryReadAsync(path) ?? (File.Exists(path) ? "<locked>" : "<missing>");
    }

    /// <summary>The file's content, or null while it doesn't exist <i>or</i> can't be opened yet. Autosave is
    /// writing the same path from a background task, and Windows enforces the share mode other platforms
    /// don't — so landing mid-write is an ordinary outcome of polling, not a failure (#83). Retrying is the
    /// same posture as <c>SecretRetry</c>; only the caller's timeout decides when to give up.</summary>
    private static async Task<string?> TryReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try { return await File.ReadAllTextAsync(path); }
        catch (IOException) { return null; }              // the writer has it open
        catch (UnauthorizedAccessException) { return null; }
    }

    // ---- OnEdit (default) ----

    [Fact]
    public void OnEdit_is_the_default_for_a_fresh_install()
        => Assert.Equal(AutosaveMode.OnEdit, new AppSettings().AutosaveMode);

    [Fact]
    public async Task OnEdit_writes_a_named_script_while_typing_and_clears_the_dirty_marker()
    {
        var vm = await Project(AutosaveMode.OnEdit);
        var (tab, path) = await NamedScript(vm);

        tab.Text = "select 1; -- typed";

        Assert.Equal("select 1; -- typed", await WaitForWrite(path, "select 1; -- typed"));
        Assert.False(tab.IsDirty);          // the dot goes away because there's nothing unsaved
        Assert.False(tab.HasUnsavedWork);
    }

    // ---- OnExecute ----

    [Fact]
    public async Task OnExecute_does_not_write_while_typing()
    {
        var vm = await Project(AutosaveMode.OnExecute);
        var (tab, path) = await NamedScript(vm);

        tab.Text = "select 1; -- typed";
        await Task.Delay(400);

        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task OnExecute_writes_when_the_run_signal_fires()
    {
        var vm = await Project(AutosaveMode.OnExecute);
        var (tab, path) = await NamedScript(vm);
        tab.Text = "select 1; -- ran";

        await vm.Workspace.Autosave.OnExecutedAsync(tab);

        Assert.Equal("select 1; -- ran", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
    }

    [Theory]
    [InlineData(AutosaveMode.OnEdit)]
    [InlineData(AutosaveMode.Off)]
    public async Task The_run_signal_is_a_no_op_outside_OnExecute(AutosaveMode mode)
    {
        var vm = await Project(mode);
        var (tab, path) = await NamedScript(vm);
        await vm.Workspace.FlushScratchAsync();   // settle any OnEdit write from the setup
        await File.WriteAllTextAsync(path, "select 1;");

        tab.Text = "select 1; -- edited";
        await vm.Workspace.Autosave.OnExecutedAsync(tab);

        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));   // only OnExecute writes on a run
    }

    [SkippableFact]
    public async Task OnExecute_writes_through_a_real_run()
    {
        // The signal is raised inside ExecuteAsync past its guards, so covering the wiring (rather than
        // just OnExecutedAsync) needs a run that actually starts — hence a live server (§4.2).
        var vm = await Project(AutosaveMode.OnExecute);
        await PgTestServer.RequireAsync();
        await vm.Connections.SeedDemoConnectionAsync(
            PgTestServer.Host, PgTestServer.Port, PgTestServer.Database, PgTestServer.User, PgTestServer.Password);

        var tab = vm.Workspace.SelectedTab!;
        var path = Path.Combine(vm.ScriptsDirectory!, "ran.sql");
        await vm.Workspace.SaveScriptAsync(tab, path, "select 1;");
        tab.Text = "select 2;";

        await vm.Execution.ExecuteAsync(tab.Text);

        Assert.True(tab.LastResult?.Success, vm.StatusText);
        Assert.Equal("select 2;", await File.ReadAllTextAsync(path));
        await vm.DisposeSessionsAsync();
    }

    // ---- Off ----

    [Fact]
    public async Task Off_never_writes_a_named_script_and_leaves_it_dirty()
    {
        var vm = await Project(AutosaveMode.Off);
        var (tab, path) = await NamedScript(vm);
        vm.Workspace.SelectedTab = tab;

        tab.Text = "select 1; -- typed";
        await Task.Delay(400);
        await vm.Execution.ExecuteAsync(tab.Text);
        await vm.Workspace.FlushScratchAsync();      // even an explicit checkpoint must not write it
        vm.SaveWorkspace();                          // ...nor the shutdown path

        Assert.Equal("select 1;", await File.ReadAllTextAsync(path));
        Assert.True(tab.IsDirty);
    }

    [Fact]
    public async Task Off_still_prompts_before_closing_a_dirty_named_script()
    {
        var dialogs = new FakeDialogs(CloseChoice.Cancel);
        var vm = await Project(AutosaveMode.Off, dialogs);
        var (tab, _) = await NamedScript(vm);
        tab.Text = "select 1; -- typed";

        Assert.False(await vm.Workspace.CloseTabAsync(tab));   // the prompt is the guard when autosave is off
        Assert.Single(dialogs.ClosePrompts);
        Assert.Contains(tab, vm.Workspace.Tabs);
    }

    // ---- scratch is exempt: its file is its home in every mode ----

    [Theory]
    [InlineData(AutosaveMode.OnEdit)]
    [InlineData(AutosaveMode.OnExecute)]
    [InlineData(AutosaveMode.Off)]
    public async Task Scratch_still_reaches_a_file_on_close_in_every_mode(AutosaveMode mode)
    {
        var dialogs = new FakeDialogs(CloseChoice.Cancel);   // would block the close if it were asked
        var vm = await Project(mode, dialogs);
        vm.Workspace.NewTab();
        var tab = vm.Workspace.Tabs[0];
        Assert.True(tab.IsScratch);
        tab.Text = "select 'scratch';";

        Assert.True(await vm.Workspace.CloseTabAsync(tab));
        Assert.Empty(dialogs.ClosePrompts);

        var file = Assert.Single(Directory.EnumerateFiles(Path.Combine(vm.ScriptsDirectory!, "scratch")));
        Assert.Equal("select 'scratch';", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Off_still_backs_scratch_on_a_project_switch()
    {
        var vm = await Project(AutosaveMode.Off);
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 'kept';";
        var scratchDir = Path.Combine(vm.ScriptsDirectory!, "scratch");

        await vm.Workspace.FlushScratchAsync();

        var file = Assert.Single(Directory.EnumerateFiles(scratchDir));
        Assert.Equal("select 'kept';", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Off_does_not_autosave_scratch_while_typing()
    {
        // "Off" still means off: scratch reaches disk at checkpoints, not on every keystroke.
        var vm = await Project(AutosaveMode.Off);
        var tab = vm.Workspace.SelectedTab!;
        tab.Text = "select 'typing';";

        await Task.Delay(400);
        Assert.Null(tab.ScriptPath);
    }
}
