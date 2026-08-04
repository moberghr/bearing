using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Bearing.Persistence;

namespace Bearing.App.Input;

/// <summary>
/// Layers a user <c>keybindings.json</c> over the built-in defaults. Everything is best-effort: a bad
/// file, an unknown command, or an unparseable gesture is skipped with a warning — the app always ends
/// up with a usable keymap, never a crash.
/// </summary>
public static class KeymapLoader
{
    public static string ConfigPath => Path.Combine(BearingPaths.ConfigDir, "keybindings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Persist override entries to <see cref="ConfigPath"/> (atomic write). An empty set removes
    /// the file entirely, so the app falls back to pure defaults.</summary>
    public static void SaveOverrides(IReadOnlyList<KeyBindingEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
            return;
        }
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, WriteOptions));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>Load overrides from <see cref="ConfigPath"/> (if present) and layer them over
    /// <paramref name="defaults"/>. A missing file yields the defaults unchanged. <paramref name="knownCommands"/>
    /// (the registry's ids) lets config bind commands that ship unbound; null = only commands with defaults.</summary>
    public static KeymapLoadResult LoadFromConfig(Keymap defaults, IReadOnlySet<string>? knownCommands = null)
    {
        string json;
        try
        {
            if (!File.Exists(ConfigPath)) return new KeymapLoadResult(defaults, Array.Empty<string>());
            json = File.ReadAllText(ConfigPath);
        }
        catch (Exception ex)
        {
            return new KeymapLoadResult(defaults, new[] { $"keybindings.json unreadable — {ex.Message}" });
        }
        return LoadFromJson(defaults, json, knownCommands);
    }

    /// <summary>Deserialize override entries from a JSON string and layer them over the defaults.
    /// Malformed JSON is non-fatal — the defaults come back with a warning.</summary>
    public static KeymapLoadResult LoadFromJson(Keymap defaults, string json, IReadOnlySet<string>? knownCommands = null)
    {
        List<KeyBindingEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<KeyBindingEntry>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return new KeymapLoadResult(defaults, new[] { $"keybindings.json ignored — {ex.Message}" });
        }
        return entries is null ? new KeymapLoadResult(defaults, Array.Empty<string>()) : Apply(defaults, entries, knownCommands);
    }

    /// <summary>Pure layering — no file IO, so it's unit-testable. Applies each override in order.
    /// <paramref name="knownCommands"/> augments the typo-guard so commands that ship unbound (palette-only)
    /// can still be bound; they must carry an explicit scope since there's no default to infer from.</summary>
    public static KeymapLoadResult Apply(Keymap defaults, IEnumerable<KeyBindingEntry> overrides, IReadOnlySet<string>? knownCommands = null)
    {
        var bindings = defaults.Bindings.ToList();
        var warnings = new List<string>();

        // A command's scope is intrinsic (it's the scope of its default binding), so config entries can
        // omit scope and we infer it.
        var scopeByCommand = defaults.Bindings
            .GroupBy(b => b.CommandId)
            .ToDictionary(g => g.Key, g => g.First().Scope);

        // Valid command ids for the typo-guard: every command with a default, plus any registered extras.
        var known = new HashSet<string>(scopeByCommand.Keys);
        if (knownCommands is not null) known.UnionWith(knownCommands);

        foreach (var entry in overrides)
        {
            var raw = entry.Command?.Trim() ?? "";
            var remove = raw.StartsWith('-');
            var command = remove ? raw[1..] : raw;
            if (command.Length == 0) { warnings.Add("keybindings.json: entry with no command was skipped"); continue; }

            if (!TryResolveScope(entry, command, scopeByCommand, out var scope))
            {
                warnings.Add($"keybindings.json: unknown command '{command}' (or scope) — skipped");
                continue;
            }

            // Keyless unbind: drop every gesture bound to this command in the scope.
            if (remove && string.IsNullOrWhiteSpace(entry.Key))
            {
                if (bindings.RemoveAll(b => b.Scope == scope && b.CommandId == command) == 0)
                    warnings.Add($"keybindings.json: nothing to unbind for '{command}'");
                continue;
            }

            if (!GestureParser.TryParse(entry.Key ?? "", out var gesture))
            {
                warnings.Add($"keybindings.json: unparseable key '{entry.Key}' — skipped");
                continue;
            }

            if (remove)
            {
                if (bindings.RemoveAll(b => b.Scope == scope && b.Gesture == gesture && b.CommandId == command) == 0)
                    warnings.Add($"keybindings.json: '{entry.Key}' was not bound to '{command}' — nothing to unbind");
                continue;
            }

            if (!known.Contains(command))
            {
                warnings.Add($"keybindings.json: unknown command '{command}' — skipped");
                continue;
            }

            // One command per (scope, gesture): a new binding displaces whatever held that gesture.
            var displaced = bindings.Where(b => b.Scope == scope && b.Gesture == gesture && b.CommandId != command)
                                    .Select(b => b.CommandId).Distinct().ToList();
            bindings.RemoveAll(b => b.Scope == scope && b.Gesture == gesture);
            bindings.Add(new KeyBinding(scope, gesture, command));
            foreach (var d in displaced)
                warnings.Add($"keybindings.json: '{entry.Key}' ({scope}) rebound from '{d}' to '{command}'");
        }

        return new KeymapLoadResult(new Keymap(bindings), warnings);
    }

    private static bool TryResolveScope(KeyBindingEntry entry, string command,
        IReadOnlyDictionary<string, KeyScope> scopeByCommand, out KeyScope scope)
    {
        if (!string.IsNullOrWhiteSpace(entry.Scope))
            return Enum.TryParse(entry.Scope, ignoreCase: true, out scope);
        return scopeByCommand.TryGetValue(command, out scope);
    }
}
