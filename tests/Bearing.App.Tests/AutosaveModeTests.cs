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
        // A generous budget (~12s) rather than 3, because the write is debounced onto a background task and
        // the machine may be loaded. It is not what fixed #83 — the flake was this poll's own read handle
        // blocking the writer, see TryReadAsync — but the budget costs nothing on success and a genuinely
        // slow write should not read as a missing one.
        for (var i = 0; i < 240; i++)
        {
            if (await TryReadAsync(path) == expected) return expected;
            await Task.Delay(50);
        }
        // Distinguish the three ways this can end, because they point at different causes: the writer still
        // holding the file, no file at all, or a file that simply never received the edit.
        return await TryReadAsync(path) ?? (File.Exists(path) ? "<locked>" : "<missing>");
    }

    /// <summary>
    /// The file's content, or null while it doesn't exist yet.
    /// <para>
    /// Opened with a sharing mode that lets the writer keep working, which is the whole fix for #83: this
    /// polls every 50ms while autosave writes the same path from a background task, and a plain
    /// <c>File.ReadAllTextAsync</c> takes a handle that <i>excludes</i> writers on Windows. The read was not
    /// losing a race with the write — it was <b>causing</b> the write to fail, and autosave is best-effort
    /// (§5.2), so it reported the failure to the status bar and gave up. The test was the aggressor.
    /// </para>
    /// <para>
    /// Sharing a writer means a read can land mid-write and come back partial; the caller compares against
    /// the expected content, so a partial read simply doesn't match and it polls again.
    /// </para>
    /// </summary>
    private static async Task<string?> TryReadAsync(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch (FileNotFoundException) { return null; }     // not written yet
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }               // a replace in flight; try again
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

        var edits = 0;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditorTabViewModel.Text)) edits++; };

        var written = await WaitForWrite(path, "select 1; -- typed");
        // Everything in one message: this used to fail intermittently under a full run and the file's content
        // alone never said why (#83). It was the status line that gave it away — "could not autosave …
        // because it is being used by another process" — which is how the poll below was found to be locking
        // the file it was watching. Kept, because it is what makes the next failure diagnosable rather than
        // a re-run.
        //
        // Worth knowing beyond the test: autosave treats a failed write as final. It reports and drops the
        // pending write, and nothing retries until the next keystroke — so one transient lock (antivirus, a
        // sync client, another editor) silently stops autosaving that buffer for as long as you keep typing
        // in a different one.
        Assert.True(written == "select 1; -- typed",
            $"file={written}; tab: dirty={tab.IsDirty} unsaved={tab.HasUnsavedWork} "
            + $"scratch={tab.IsScratch} path={tab.ScriptPath} text='{tab.Text}' "
            + $"stillOpen={vm.Workspace.Tabs.Contains(tab)} laterEdits={edits} status='{vm.StatusText}'");
        Assert.False(tab.IsDirty);          // the dot goes away because there's nothing unsaved
        Assert.False(tab.HasUnsavedWork);
    }

    [Fact]
    public async Task A_locked_file_is_retried_rather_than_abandoned()
    {
        // Autosave used to treat a failed write as final: it reported and dropped the pending write, and
        // nothing wrote again until the next keystroke — so one transient lock from antivirus, a sync client
        // or another editor silently stopped autosaving that buffer while the user carried on typing
        // elsewhere. Found while diagnosing #83, where the lock was this suite's own polling read.
        var vm = await Project(AutosaveMode.OnEdit);
        var (tab, path) = await NamedScript(vm);
        await vm.Workspace.FlushScratchAsync();     // settle the setup write

        // Released on the retry notice rather than after a delay, so the retry path is proven to have run
        // instead of being raced past by a slow debounce.
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.StatusText)
                && vm.StatusText.StartsWith("Retrying autosave", StringComparison.Ordinal))
                retried.TrySetResult();
        };

        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            tab.Text = "select 1; -- typed while locked";
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            stream.Dispose();                        // the lock lets go; the next attempt should land
        }

        Assert.Equal("select 1; -- typed while locked",
            await WaitForWrite(path, "select 1; -- typed while locked"));
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task A_write_that_never_succeeds_gives_up_and_says_so()
    {
        // The other end of the retry: a path that stays unwritable must reach the status bar promptly rather
        // than retrying forever, and the tab must stay dirty so the close prompt is still the backstop.
        var vm = await Project(AutosaveMode.OnEdit);
        var (tab, path) = await NamedScript(vm);
        await vm.Workspace.FlushScratchAsync();

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.StatusText)
                && vm.StatusText.StartsWith("Could not autosave", StringComparison.Ordinal))
                failed.TrySetResult();
        };

        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        tab.Text = "select 1; -- never lands";

        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(tab.IsDirty);
        Assert.True(tab.HasUnsavedWork);
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
