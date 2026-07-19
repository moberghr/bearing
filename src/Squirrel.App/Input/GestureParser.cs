using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Squirrel.App.Input;

/// <summary>
/// Text ↔ <see cref="Gesture"/>. The text form is what lives in <c>keybindings.json</c> and what the
/// menu / palette display, so it must round-trip. Grammar: <c>Mod+Mod+Key</c> where each Mod is one of
/// <c>Ctrl|Shift|Alt|Meta</c> and Key is a logical key (<c>Enter</c>, <c>F5</c>, <c>A</c>, <c>1</c>,
/// <c>/</c>) or a physical key prefixed <c>Phys</c> (<c>PhysBracketLeft</c>).
/// </summary>
public static class GestureParser
{
    // Friendly aliases for logical keys whose Avalonia enum name is unobvious. Round-trips both ways.
    private static readonly (string Text, Key Key)[] Aliases =
    {
        ("Enter", Key.Enter), // Key.Enter == Key.Return, whose ToString is "Return"; prefer the friendly name
        ("/", Key.OemQuestion), ("-", Key.OemMinus), ("=", Key.OemPlus),
        ("[", Key.OemOpenBrackets), ("]", Key.OemCloseBrackets),
        (",", Key.OemComma), (".", Key.OemPeriod), (";", Key.OemSemicolon),
        ("1", Key.D1), ("2", Key.D2), ("3", Key.D3), ("4", Key.D4), ("5", Key.D5),
        ("6", Key.D6), ("7", Key.D7), ("8", Key.D8), ("9", Key.D9), ("0", Key.D0),
    };

    public static bool TryParse(string text, out Gesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var mods = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Control; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "meta" or "cmd" or "win" or "super": mods |= KeyModifiers.Meta; break;
                default: return false; // an unknown modifier token → reject rather than mis-bind
            }
        }

        var keyToken = parts[^1];
        if (keyToken.StartsWith("Phys", StringComparison.Ordinal))
        {
            if (!Enum.TryParse<PhysicalKey>(keyToken[4..], ignoreCase: true, out var phys)) return false;
            gesture = Gesture.ForPhysical(mods, phys);
            return true;
        }

        var alias = Aliases.FirstOrDefault(a => a.Text == keyToken);
        if (alias.Text is not null) { gesture = Gesture.ForKey(mods, alias.Key); return true; }

        if (Enum.TryParse<Key>(keyToken, ignoreCase: true, out var key) && key != Key.None)
        {
            gesture = Gesture.ForKey(mods, key);
            return true;
        }
        return false;
    }

    public static Gesture Parse(string text) =>
        TryParse(text, out var g) ? g : throw new FormatException($"Unparseable gesture: '{text}'");

    public static string Format(Gesture g)
    {
        var tokens = new List<string>();
        if (g.Modifiers.HasFlag(KeyModifiers.Control)) tokens.Add("Ctrl");
        if (g.Modifiers.HasFlag(KeyModifiers.Shift)) tokens.Add("Shift");
        if (g.Modifiers.HasFlag(KeyModifiers.Alt)) tokens.Add("Alt");
        if (g.Modifiers.HasFlag(KeyModifiers.Meta)) tokens.Add("Meta");

        if (g.Physical is { } p) tokens.Add("Phys" + p);
        else if (g.Logical is { } k)
        {
            var alias = Aliases.FirstOrDefault(a => a.Key == k);
            tokens.Add(alias.Text ?? k.ToString());
        }
        return string.Join("+", tokens);
    }
}
