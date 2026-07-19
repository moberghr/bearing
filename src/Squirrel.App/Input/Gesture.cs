using Avalonia.Input;

namespace Squirrel.App.Input;

/// <summary>
/// A normalized keystroke: a set of modifiers plus EITHER a logical <see cref="Key"/> (layout-dependent,
/// e.g. <c>Ctrl+/</c>) OR a <see cref="PhysicalKey"/> (layout-independent, e.g. fold on the physical
/// <c>[</c> key, which types <c>š</c> on the Croatian layout). Physical bindings win over logical when
/// both could match the same keystroke — see <see cref="Keymap"/>.
/// </summary>
public readonly record struct Gesture
{
    public KeyModifiers Modifiers { get; }
    public Key? Logical { get; }
    public PhysicalKey? Physical { get; }

    private Gesture(KeyModifiers modifiers, Key? logical, PhysicalKey? physical)
    {
        Modifiers = modifiers;
        Logical = logical;
        Physical = physical;
    }

    public static Gesture ForKey(KeyModifiers modifiers, Key key) => new(Normalize(modifiers), key, null);
    public static Gesture ForPhysical(KeyModifiers modifiers, PhysicalKey key) => new(Normalize(modifiers), null, key);

    public bool IsPhysical => Physical is not null;

    /// <summary>Fold Meta (Cmd/Win) into Control so a <c>Ctrl+…</c> binding also fires on macOS Cmd,
    /// and keep only the four modifiers we bind against.</summary>
    private static KeyModifiers Normalize(KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Meta)) mods = (mods & ~KeyModifiers.Meta) | KeyModifiers.Control;
        return mods & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);
    }

    /// <summary>True when this gesture matches a raw keystroke. Modifiers are normalized the same way
    /// on both sides so Meta==Control and stray modifiers (e.g. numlock) are ignored.</summary>
    public bool Matches(KeyModifiers mods, Key key, PhysicalKey physical)
    {
        if (Normalize(mods) != Modifiers) return false;
        return Physical is { } p ? physical == p : Logical == key;
    }
}
