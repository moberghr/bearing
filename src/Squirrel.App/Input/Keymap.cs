using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Squirrel.App.Input;

/// <summary>One default (or user-configured) binding: a gesture, in a scope, triggers a command id.</summary>
public readonly record struct KeyBinding(KeyScope Scope, Gesture Gesture, string CommandId);

/// <summary>
/// The gesture ↔ command mapping. This is the single matcher that replaces the three hand-rolled key
/// dispatchers. <see cref="Resolve"/> is pure (takes primitives, not a UI event) so it's fully unit-testable.
/// </summary>
public sealed class Keymap
{
    private readonly List<KeyBinding> _bindings;

    public Keymap(IEnumerable<KeyBinding> bindings) => _bindings = bindings.ToList();

    public IReadOnlyList<KeyBinding> Bindings => _bindings;

    /// <summary>
    /// The command bound to this keystroke in <paramref name="scope"/>, or null. A physical-key binding
    /// wins over a logical one when both match — that's what keeps the layout-independent fold/comment
    /// keys working on non-US layouts.
    /// </summary>
    public string? Resolve(KeyScope scope, KeyModifiers mods, Key key, PhysicalKey physical)
    {
        string? logicalHit = null;
        foreach (var b in _bindings)
        {
            if (b.Scope != scope || !b.Gesture.Matches(mods, key, physical)) continue;
            if (b.Gesture.IsPhysical) return b.CommandId;
            logicalHit ??= b.CommandId;
        }
        return logicalHit;
    }

    /// <summary>The gestures bound to a command (for menu / palette display), logical ones first.</summary>
    public IEnumerable<Gesture> GesturesFor(string commandId) =>
        _bindings.Where(b => b.CommandId == commandId)
                 .OrderBy(b => b.Gesture.IsPhysical)
                 .Select(b => b.Gesture);

    /// <summary>The first (preferably logical) gesture for a command, formatted for display; null if unbound.</summary>
    public string? DisplayGesture(string commandId)
    {
        var g = GesturesFor(commandId).Cast<Gesture?>().FirstOrDefault();
        return g is { } gesture ? GestureParser.Format(gesture) : null;
    }
}
