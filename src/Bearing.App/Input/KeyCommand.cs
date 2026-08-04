using System;
using System.Threading.Tasks;

namespace Bearing.App.Input;

/// <summary>
/// One keyboard-triggerable action, identified by a stable <see cref="Id"/>. The keymap binds gestures
/// to these ids; the registry maps ids to the delegate that runs them. This is the single command model
/// the command palette enumerates and invokes without a keystroke.
/// </summary>
public sealed class KeyCommand
{
    public string Id { get; }
    public string Title { get; }
    public KeyScope Scope { get; }
    public string Group { get; }

    private readonly Func<ValueTask> _run;
    private readonly Func<bool>? _canRun;

    public KeyCommand(string id, string title, KeyScope scope, string group,
        Func<ValueTask> run, Func<bool>? canRun = null)
    {
        Id = id;
        Title = title;
        Scope = scope;
        Group = group;
        _run = run;
        _canRun = canRun;
    }

    /// <summary>Convenience for synchronous command bodies.</summary>
    public static KeyCommand Sync(string id, string title, KeyScope scope, string group,
        Action run, Func<bool>? canRun = null) =>
        new(id, title, scope, group, () => { run(); return ValueTask.CompletedTask; }, canRun);

    /// <summary>Whether the command is currently applicable. When false the dispatcher does NOT mark the
    /// keystroke handled, so it falls through / bubbles (this is how contextual keys like Escape work).</summary>
    public bool CanRun() => _canRun?.Invoke() ?? true;

    public ValueTask Run() => _run();
}
