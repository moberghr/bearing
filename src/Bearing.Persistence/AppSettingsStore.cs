using System.Text.Json;
using Bearing.Core.Workspace;

namespace Bearing.Persistence;

/// <summary>
/// Reads/writes <see cref="AppSettings"/> as <c>settings.json</c> in the config dir. Best-effort: a
/// missing or malformed file yields defaults so a bad edit can never stop the app from starting.
/// </summary>
public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? path = null)
        => _path = path ?? Path.Combine(BearingPaths.ConfigDir, "settings.json");

    public string Location => _path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, BearingJson.Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, BearingJson.Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
