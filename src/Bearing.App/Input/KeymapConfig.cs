using System.Collections.Generic;

namespace Bearing.App.Input;

/// <summary>
/// One line of <c>keybindings.json</c>. The file is a top-level JSON array of these, applied over the
/// built-in defaults (VS Code style). Examples:
/// <code>
/// [
///   { "key": "Ctrl+R", "command": "run" },       // bind an extra gesture (scope inferred from the command)
///   { "key": "F5", "command": "-run" },           // unbind a default gesture ("-" prefix)
///   { "command": "-editor.foldAll" },             // no key → unbind ALL of that command's gestures
///   { "key": "Ctrl+Y", "command": "grid.copy", "scope": "Grid" }
/// ]
/// </code>
/// </summary>
public sealed record KeyBindingEntry
{
    /// <summary>The gesture text (see <see cref="GestureParser"/>). Optional only for a keyless unbind.</summary>
    public string? Key { get; init; }

    /// <summary>Command id to bind, or <c>-id</c> to unbind. Required.</summary>
    public string Command { get; init; } = "";

    /// <summary>Optional scope name (Global/Editor/Grid/…). Omit to inherit the command's default scope.</summary>
    public string? Scope { get; init; }
}

/// <summary>The result of layering config over defaults: the effective keymap plus any human-readable
/// warnings for entries that were skipped or that displaced another binding.</summary>
public sealed record KeymapLoadResult(Keymap Keymap, IReadOnlyList<string> Warnings);
