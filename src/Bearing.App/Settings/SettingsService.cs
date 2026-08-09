using System;
using Bearing.Core.Workspace;

namespace Bearing.App.Settings;

/// <summary>
/// The single owner of the live <see cref="AppSettings"/>: applies an edit, persists it, and tells
/// everyone who cares. The settings window writes through here on every keystroke/click (there is no
/// Save button), so this is also the one place that decides what an unwritable settings file means —
/// per §5.2, a failed write is reported and swallowed, and the in-memory value still takes effect for
/// the session rather than silently reverting under the user's cursor.
/// </summary>
public sealed class SettingsService
{
    private readonly IAppSettingsStore _store;

    public SettingsService(IAppSettingsStore store, AppSettings? initial = null)
    {
        _store = store;
        Current = initial ?? store.Load();
    }

    /// <summary>A service backed by nothing, for tests and headless construction: edits take effect in
    /// memory and are dropped instead of written. Keeps a test's "settings" one expression long.</summary>
    public static SettingsService InMemory(AppSettings? initial = null)
        => new(new MemoryStore(), initial ?? new AppSettings());

    private sealed class MemoryStore : IAppSettingsStore
    {
        private AppSettings _settings = new();
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings) => _settings = settings;
        public string Location => "(not persisted)";
    }

    /// <summary>The settings in force right now. Read through this (not a cached copy) so an edit lands.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Raised after <see cref="Current"/> changes, with the new value. Subscribers that cache a
    /// derived value (font size, idle timeout) re-read it here.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>Where the file lives, for the settings window's footer.</summary>
    public string Location => _store.Location;

    /// <summary>Reported when a save fails, so the caller can surface it in the status bar. Set by the
    /// composition root; a null sink means the failure is dropped, never thrown.</summary>
    public Action<string>? SaveFailed { get; set; }

    /// <summary>Apply an edit. No-ops (and doesn't write) when the edit changes nothing, so re-selecting
    /// the current dropdown value doesn't churn the file.</summary>
    public void Update(Func<AppSettings, AppSettings> edit)
    {
        var next = edit(Current);
        if (next == Current) return;    // records compare structurally
        Current = next;
        Persist();
        Changed?.Invoke(Current);
    }

    /// <summary>Set one described setting from a boxed UI value (bool / int / enum member name).</summary>
    public void Set(SettingDescriptor descriptor, object? value)
        => Update(s => descriptor.Write(s, value));

    /// <summary>Put one setting back to its shipped default.</summary>
    public void Reset(SettingDescriptor descriptor)
        => Update(descriptor.Reset);

    /// <summary>Put every <i>described</i> setting back to its default, leaving persisted state
    /// (window size) alone — that isn't a preference and resetting it would surprise.</summary>
    public void ResetAll()
        => Update(s =>
        {
            foreach (var d in SettingsCatalog.All) s = d.Reset(s);
            return s;
        });

    private void Persist()
    {
        try
        {
            _store.Save(Current);
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke($"Couldn't save settings: {ex.Message}");
        }
    }
}
