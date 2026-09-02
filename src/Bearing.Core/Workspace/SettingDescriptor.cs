using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.Core.Workspace;

/// <summary>
/// One user-facing preference, described well enough that a generic UI can render, search and reset it
/// without knowing what it is. Reading and writing go through typed lambdas over <see cref="AppSettings"/>,
/// so a descriptor is compile-checked against the property it describes — no reflection, no string keys
/// resolved at runtime, and a renamed property breaks the build rather than the settings window.
/// </summary>
/// <remarks>
/// Subclasses carry the <i>kind</i> of value (bool / int / choice), which is the only thing the renderer
/// switches on. Adding a new kind means one new subclass here and one new arm in the window's row builder.
/// </remarks>
public abstract record SettingDescriptor
{
    /// <summary>Stable dotted id (<c>editor.autosaveMode</c>). Used for search and for referring to a
    /// setting in docs; not the JSON property name.</summary>
    public required string Key { get; init; }

    /// <summary>Id of the <see cref="SettingsCategory"/> this appears under.</summary>
    public required string CategoryId { get; init; }

    /// <summary>Row label.</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences under the label — say what changes, not what the control is.</summary>
    public string Description { get; init; } = "";

    /// <summary>Extra search terms that aren't in the title or description (synonyms, old names).</summary>
    public string Keywords { get; init; } = "";

    /// <summary>Set when a change does <b>not</b> take effect immediately; shown on the row so an
    /// immediate-apply window never implies more than it delivers.</summary>
    public string? AppliesNote { get; init; }

    /// <summary>The current value, boxed.</summary>
    public abstract object? Read(AppSettings settings);

    /// <summary>The settings with this one value replaced. Out-of-range or wrongly-typed input is coerced
    /// rather than thrown — the caller is a UI control, and a rejected edit must not lose the rest.</summary>
    public abstract AppSettings Write(AppSettings settings, object? value);

    /// <summary>The as-shipped value, read off <see cref="AppSettings.Defaults"/>.</summary>
    public object? Default => Read(AppSettings.Defaults);

    /// <summary>Whether this setting is untouched, i.e. still at its shipped default.</summary>
    public bool IsDefault(AppSettings settings) => Equals(Read(settings), Default);

    /// <summary>The settings with this one entry back at its default.</summary>
    public AppSettings Reset(AppSettings settings) => Write(settings, Default);

    /// <summary>Everything a search should match on, lowest-signal last.</summary>
    public string SearchText => $"{Title} {Description} {Keywords} {Key}";
}

/// <summary>A yes/no setting. Rendered as a checkbox.</summary>
public sealed record BoolSetting : SettingDescriptor
{
    public required Func<AppSettings, bool> Get { get; init; }
    public required Func<AppSettings, bool, AppSettings> Set { get; init; }

    public override object? Read(AppSettings settings) => Get(settings);

    public override AppSettings Write(AppSettings settings, object? value)
        => Set(settings, value as bool? ?? Get(settings));
}

/// <summary>A whole-number setting with a range. Rendered as a spinner; values are clamped, never rejected.</summary>
public sealed record IntSetting : SettingDescriptor
{
    public required Func<AppSettings, int> Get { get; init; }
    public required Func<AppSettings, int, AppSettings> Set { get; init; }

    public int Min { get; init; } = 0;
    public int Max { get; init; } = int.MaxValue;

    /// <summary>Trailing unit shown after the spinner ("days", "minutes", "rows").</summary>
    public string? Unit { get; init; }

    public override object? Read(AppSettings settings) => Get(settings);

    public override AppSettings Write(AppSettings settings, object? value)
    {
        var n = value switch
        {
            int i => i,
            double d => (int)Math.Round(d),
            decimal m => (int)Math.Round(m),
            _ => Get(settings),
        };
        return Set(settings, Math.Clamp(n, Min, Max));
    }
}

/// <summary>One choice of an enum-backed setting.</summary>
/// <param name="Value">The enum member name, as persisted.</param>
/// <param name="Title">What the dropdown shows.</param>
/// <param name="Description">Optional detail shown under the row when this option is selected.</param>
public sealed record SettingOption(string Value, string Title, string Description = "");

/// <summary>
/// A pick-one setting backed by an enum. Values are carried as enum <i>member names</i> so the renderer
/// needs no generics; <see cref="Write"/> only accepts a name that is one of <see cref="Options"/>, which
/// is what makes a plain <c>Enum.Parse</c> in a descriptor's <see cref="Set"/> safe by construction.
/// A catalog test pins that every option value is a real member of its enum.
/// </summary>
public sealed record EnumSetting : SettingDescriptor
{
    public required IReadOnlyList<SettingOption> Options { get; init; }
    public required Func<AppSettings, string> Get { get; init; }
    public required Func<AppSettings, string, AppSettings> Set { get; init; }

    public override object? Read(AppSettings settings) => Get(settings);

    public override AppSettings Write(AppSettings settings, object? value)
        => value is string s && Options.Any(o => o.Value == s) ? Set(settings, s) : settings;

    /// <summary>The option matching the current value, or null if the file holds something unknown.</summary>
    public SettingOption? Selected(AppSettings settings)
        => Options.FirstOrDefault(o => o.Value == Get(settings));
}

/// <summary>
/// A free-text setting whose value is validated rather than bounded — an identifier drawn from a set too
/// large to enumerate as options (#77's timezone: there are some six hundred).
/// <para>
/// <see cref="Suggestions"/> is a picker's contents, not a constraint: a value the user typed that is not in
/// the list is still accepted if <see cref="IsValid"/> takes it, which is what lets a settings file written
/// on another platform (IANA ids on a Windows machine) survive being opened here.
/// </para>
/// </summary>
public sealed record StringSetting : SettingDescriptor
{
    public required Func<AppSettings, string> Get { get; init; }
    public required Func<AppSettings, string, AppSettings> Set { get; init; }

    /// <summary>What a picker offers. Empty means "free text with no suggestions".</summary>
    public Func<IReadOnlyList<string>>? Suggestions { get; init; }

    /// <summary>Whether a value is usable. Defaults to accepting anything; a rejected write leaves the
    /// previous value rather than storing something that will not resolve.</summary>
    public Func<string, bool>? IsValid { get; init; }

    /// <summary>How the current value reads on the row, when its raw form is not the clearest ("system"
    /// alone does not say which zone that is).</summary>
    public Func<string, string>? Describe { get; init; }

    public override object? Read(AppSettings settings) => Get(settings);

    public override AppSettings Write(AppSettings settings, object? value)
        => value is string s && (IsValid?.Invoke(s) ?? true) ? Set(settings, s) : settings;
}

/// <summary>A section of the settings window. Ordering is the declaration order in <see cref="SettingsCatalog"/>.</summary>
/// <param name="Id">Stable id, matching <see cref="SettingDescriptor.CategoryId"/>.</param>
/// <param name="Title">Section heading and nav-list label.</param>
/// <param name="Description">One line under the heading.</param>
public sealed record SettingsCategory(string Id, string Title, string Description = "");
