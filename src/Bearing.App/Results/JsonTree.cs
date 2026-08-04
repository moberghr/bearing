using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bearing.App.Results;

/// <summary>The JSON value kinds the inspector distinguishes (drives syntax coloring).</summary>
public enum JsonNodeKind { Object, Array, String, Number, Boolean, Null }

/// <summary>
/// One node in a parsed JSON value, for the cell inspector's fold-tree. Containers hold
/// <see cref="Children"/>; scalars carry a display <see cref="Value"/>. <see cref="IsExpanded"/> and
/// <see cref="IsMatch"/> are view state driven by fold controls and find. Built by <see cref="JsonTree"/>.
/// </summary>
public sealed partial class JsonTreeNode : ObservableObject
{
    /// <summary>Property name for an object member; null for the root and array elements.</summary>
    public string? Key { get; init; }

    public JsonNodeKind Kind { get; init; }

    /// <summary>Scalar display text (unquoted string / raw number / "true"/"false"/"null"); null for containers.</summary>
    public string? Value { get; init; }

    public IReadOnlyList<JsonTreeNode> Children { get; init; } = Array.Empty<JsonTreeNode>();

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Set by <see cref="JsonTree.ApplyFind"/> when this node's key/value matches the query.</summary>
    [ObservableProperty] private bool _isMatch;

    public bool IsContainer => Kind is JsonNodeKind.Object or JsonNodeKind.Array;
    public int ChildCount => Children.Count;

    /// <summary>Collapsed placeholder for a container ("{…3…}" / "[…5…]").</summary>
    public string CollapsedSummary => Kind == JsonNodeKind.Array ? $"[…{ChildCount}…]" : $"{{…{ChildCount}…}}";
}

/// <summary>
/// Pure JSON → <see cref="JsonTreeNode"/> tree builder plus fold/find helpers for the cell inspector.
/// No UI dependencies — unit-tested independently of the Avalonia view.
/// </summary>
public static class JsonTree
{
    /// <summary>Parse JSON into a fold-tree; returns null if the text isn't valid JSON.</summary>
    public static JsonTreeNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Build(null, doc.RootElement); // fully materialized before the doc is disposed
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pretty-print JSON (indented); returns the input unchanged if it isn't valid JSON.</summary>
    public static string Prettify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    /// <summary>Expand or collapse every container node in the tree.</summary>
    public static void SetExpandedAll(JsonTreeNode root, bool expanded)
    {
        if (root.IsContainer) root.IsExpanded = expanded;
        foreach (var c in root.Children) SetExpandedAll(c, expanded);
    }

    /// <summary>
    /// Flag nodes whose key or scalar value contains <paramref name="query"/> (case-insensitive),
    /// expanding the ancestors of every match so they're visible. Returns the match count. An empty
    /// query clears all match flags.
    /// </summary>
    public static int ApplyFind(JsonTreeNode root, string? query)
    {
        var q = query?.Trim() ?? "";
        return Visit(root, q);

        static int Visit(JsonTreeNode node, string q)
        {
            var self = q.Length > 0 && (Contains(node.Key, q) || Contains(node.Value, q));
            node.IsMatch = self;

            var count = self ? 1 : 0;
            var childHasMatch = false;
            foreach (var child in node.Children)
            {
                var c = Visit(child, q);
                if (c > 0) childHasMatch = true;
                count += c;
            }

            // Reveal any subtree that contains a match (leave fold state alone when there's no query).
            if (q.Length > 0 && (self || childHasMatch) && node.IsContainer) node.IsExpanded = true;
            return count;
        }
    }

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static JsonTreeNode Build(string? key, JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => new JsonTreeNode
        {
            Key = key, Kind = JsonNodeKind.Object,
            Children = el.EnumerateObject().Select(p => Build(p.Name, p.Value)).ToList(),
        },
        JsonValueKind.Array => new JsonTreeNode
        {
            Key = key, Kind = JsonNodeKind.Array,
            Children = el.EnumerateArray().Select(v => Build(null, v)).ToList(),
        },
        JsonValueKind.String => new JsonTreeNode { Key = key, Kind = JsonNodeKind.String, Value = el.GetString() },
        JsonValueKind.Number => new JsonTreeNode { Key = key, Kind = JsonNodeKind.Number, Value = el.GetRawText() },
        JsonValueKind.True or JsonValueKind.False => new JsonTreeNode { Key = key, Kind = JsonNodeKind.Boolean, Value = el.GetRawText() },
        _ => new JsonTreeNode { Key = key, Kind = JsonNodeKind.Null, Value = "null" },
    };
}
