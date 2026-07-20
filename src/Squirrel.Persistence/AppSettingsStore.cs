using System.Text.Json;
using Squirrel.Core.Workspace;

namespace Squirrel.Persistence;

/// <summary>
/// Reads/writes <see cref="AppSettings"/> as <c>settings.json</c> in the config dir. Best-effort: a
/// missing or malformed file yields defaults so a bad edit can never stop the app from starting.
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? path = null)
        => _path = path ?? Path.Combine(SquirrelPaths.ConfigDir, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, SquirrelJson.Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SquirrelJson.Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
