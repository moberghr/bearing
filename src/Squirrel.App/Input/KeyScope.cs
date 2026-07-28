namespace Squirrel.App.Input;

/// <summary>
/// The context a keystroke is resolved against. A control's tunnel-phase handler resolves ONLY its
/// own scope; anything it doesn't claim bubbles up to the window, which resolves <see cref="Global"/>.
/// So "fall back to Global" is achieved by event bubbling, not by the resolver.
/// </summary>
public enum KeyScope
{
    /// <summary>Window-level shortcuts (Run, Save, tab management…). Resolved on the bubble path.</summary>
    Global,
    /// <summary>SQL editor editing commands (fold, comment, open-line…). Resolved in the editor's tunnel.</summary>
    Editor,
    /// <summary>Results grid discrete commands (copy, select-all, delete, begin-edit…). Grid tunnel.</summary>
    Grid,
}
