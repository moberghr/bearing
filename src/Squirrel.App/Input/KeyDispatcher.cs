using System.Collections.Generic;
using Avalonia.Input;

namespace Squirrel.App.Input;

/// <summary>
/// The one place a keystroke turns into an action. Each control's key handler calls
/// <see cref="TryHandle"/> with its own scope; on a match it marks the event handled and runs the
/// command. A command whose <see cref="KeyCommand.CanRun"/> is false leaves the event unhandled so it
/// bubbles on (e.g. Escape with nothing to dismiss falls through to the window).
/// </summary>
public sealed class KeyDispatcher
{
    /// <summary>The active keymap. Settable so the settings UI can swap in an edited map live.</summary>
    public Keymap Keymap { get; set; }
    public CommandRegistry Registry { get; }

    public KeyDispatcher(Keymap keymap, CommandRegistry registry)
    {
        Keymap = keymap;
        Registry = registry;
    }

    /// <param name="only">When given, only handle the keystroke if the resolved command id is in this set.
    /// Used by the window's tunnel handler to claim navigation keys before the framework/editor, while
    /// leaving every other key to the normal tunnel/bubble path.</param>
    public bool TryHandle(KeyEventArgs e, KeyScope scope, ISet<string>? only = null)
    {
        var id = Keymap.Resolve(scope, e.KeyModifiers, e.Key, e.PhysicalKey);
        if (id is null) return false;
        if (only is not null && !only.Contains(id)) return false;

        var command = Registry.Get(id);
        if (command is null || !command.CanRun()) return false;

        // Mark handled synchronously BEFORE awaiting anything — the async command body may yield, and
        // Avalonia reads Handled the moment this returns.
        e.Handled = true;
        CrashReporter.Observe(command.Run(), $"command '{command.Id}'");
        return true;
    }
}
