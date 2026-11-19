using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Bearing.App.Connections;
using Bearing.Core.Data;

namespace Bearing.App.Views;

/// <summary>
/// The connection editor's control for a field with <see cref="ConnectionField.Suggestions"/>: an editable
/// dropdown, so the list is a convenience and any other value stays typeable (a region newer than the SDK,
/// DynamoDB Local's dummy region, a profile that lives only in the credentials file). Visuals only — what a
/// pick does to other fields is <see cref="ConnectionFieldModel.Edit"/>'s decision (§2.3), reached through
/// <c>edited</c>.
/// <para>
/// Each entry shows its value and description ("eu-west-1 — Europe (Ireland)"), but the box takes the
/// <b>value alone</b>: an editable <see cref="ComboBox"/> writes a picked item's text, which is the item's
/// <see cref="object.ToString"/>, and <see cref="Item"/> returns the value there. So what is saved is the code,
/// never the display text.
/// </para>
/// </summary>
internal static class SuggestionFieldBox
{
    /// <summary>One entry. <see cref="ToString"/> is the value, because that is what the editable box puts in
    /// its text when the entry is picked — and what it matches typed text against.</summary>
    internal sealed class Item(ConnectionSuggestion suggestion)
    {
        public ConnectionSuggestion Suggestion { get; } = suggestion;

        /// <summary>What the list shows: the value, then its description when it has one.</summary>
        public string Label => Suggestion.Description is { Length: > 0 } description
            ? $"{Suggestion.Value} — {description}"
            : Suggestion.Value;

        public override string ToString() => Suggestion.Value;
    }

    /// <summary>The editable dropdown for <paramref name="field"/>, holding its current value. Every change to
    /// the box's text — typed, or a pick — is passed to <paramref name="edited"/>.</summary>
    public static ComboBox Build(ConnectionFieldState field, Action<string> edited)
    {
        var combo = new ComboBox
        {
            // The {Key}Box name every code-built row carries, so a headless UI test finds it (§4.5).
            Name = field.Key + "Box",
            ItemsSource = field.Suggestions.Select(s => new Item(s)).ToList(),
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
            PlaceholderText = field.Field.Default ?? "",
            // No recycling: the row's text is set once from its item, not bound.
            ItemTemplate = new FuncDataTemplate<Item>(
                (item, _) => new TextBlock { Text = item?.Label ?? "" }, supportsRecycling: false),
        };
        // After ItemsSource, so a value that is one of the entries also selects it.
        combo.Text = field.Value;
        combo.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) edited(combo.Text ?? "");
        };
        return combo;
    }

    /// <summary>Put a value the model wrote (a pick's fill) into the box. Raises the same text change a pick
    /// does, which the model reads as the value it already holds.</summary>
    public static void Show(ComboBox combo, string value)
    {
        if (!string.Equals(combo.Text ?? "", value, StringComparison.Ordinal)) combo.Text = value;
    }
}
