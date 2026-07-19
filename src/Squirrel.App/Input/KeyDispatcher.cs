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
    public Keymap Keymap { get; }
    public CommandRegistry Registry { get; }

    public KeyDispatcher(Keymap keymap, CommandRegistry registry)
    {
        Keymap = keymap;
        Registry = registry;
    }

    public bool TryHandle(KeyEventArgs e, KeyScope scope)
    {
        var id = Keymap.Resolve(scope, e.KeyModifiers, e.Key, e.PhysicalKey);
        if (id is null) return false;

        var command = Registry.Get(id);
        if (command is null || !command.CanRun()) return false;

        // Mark handled synchronously BEFORE awaiting anything — the async command body may yield, and
        // Avalonia reads Handled the moment this returns.
        e.Handled = true;
        _ = command.Run();
        return true;
    }
}
