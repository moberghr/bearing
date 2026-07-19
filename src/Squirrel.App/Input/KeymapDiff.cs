using System.Collections.Generic;
using System.Linq;

namespace Squirrel.App.Input;

/// <summary>
/// Turns an edited keymap into the minimal set of <see cref="KeyBindingEntry"/> overrides that, layered
/// over the defaults by <see cref="KeymapLoader.Apply"/>, reproduces it exactly. The settings UI edits
/// the effective keymap; this is what gets written to <c>keybindings.json</c> so the file stays a small,
/// readable diff from the defaults rather than a full dump.
/// </summary>
public static class KeymapDiff
{
    public static IReadOnlyList<KeyBindingEntry> ComputeOverrides(Keymap defaults, IEnumerable<KeyBinding> edited)
    {
        var defaultSet = defaults.Bindings.ToHashSet();
        var editedList = edited.ToList();
        var editedSet = editedList.ToHashSet();
        var defaultCommands = defaults.Bindings.Select(b => b.CommandId).ToHashSet();

        var entries = new List<KeyBindingEntry>();

        // Unbinds first so a rebind of an already-taken gesture applies cleanly (unbind the old, then
        // bind the new onto a now-free gesture — no displacement warning).
        foreach (var b in defaults.Bindings)
            if (!editedSet.Contains(b))
                entries.Add(new KeyBindingEntry
                {
                    Key = GestureParser.Format(b.Gesture),
                    Command = "-" + b.CommandId,
                    Scope = ScopeIfNeeded(b, defaultCommands),
                });

        foreach (var b in editedList)
            if (!defaultSet.Contains(b))
                entries.Add(new KeyBindingEntry
                {
                    Key = GestureParser.Format(b.Gesture),
                    Command = b.CommandId,
                    Scope = ScopeIfNeeded(b, defaultCommands),
                });

        return entries;
    }

    /// <summary>Scope is intrinsic to a command that has a default binding, so it's omitted (inferred on
    /// load). It's only spelled out for commands that ship unbound (nothing to infer from).</summary>
    private static string? ScopeIfNeeded(KeyBinding b, IReadOnlySet<string> defaultCommands)
        => defaultCommands.Contains(b.CommandId) ? null : b.Scope.ToString();
}
