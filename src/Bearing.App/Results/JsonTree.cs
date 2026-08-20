using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Bearing.App.Results;

/// <summary>The JSON value kinds the inspector distinguishes (drives syntax coloring).</summary>
public enum JsonNodeKind { Object, Array, String, Number, Boolean, Null }

/// <summary>
/// One node in a parsed JSON value. Containers hold <see cref="Children"/>; scalars carry a display
/// <see cref="Value"/>. Built by <see cref="JsonTree"/> and rendered as indented JSON text by
/// <see cref="JsonText"/> — this is a parse model, not view state, so it holds no fold or match flags.
/// </summary>
public sealed class JsonTreeNode
{
    /// <summary>Property name for an object member; null for the root and array elements.</summary>
    public string? Key { get; init; }

    public JsonNodeKind Kind { get; init; }

    /// <summary>Scalar display text (unquoted string / raw number / "true"/"false"/"null"); null for containers.</summary>
    public string? Value { get; init; }

    public IReadOnlyList<JsonTreeNode> Children { get; init; } = Array.Empty<JsonTreeNode>();

    public bool IsContainer => Kind is JsonNodeKind.Object or JsonNodeKind.Array;
    public int ChildCount => Children.Count;
}

/// <summary>
/// Pure JSON → <see cref="JsonTreeNode"/> parser for the cell inspector. No UI dependencies — unit-tested
/// independently of the Avalonia view. Formatting lives in <see cref="JsonText"/>.
/// </summary>
public static class JsonTree
{
    /// <summary>Parse JSON into a node tree; returns null if the text isn't valid JSON.</summary>
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

    private static JsonTreeNode Build(string? key, JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => new JsonTreeNode
        {
            Key = key,
            Kind = JsonNodeKind.Object,
            Children = el.EnumerateObject().Select(p => Build(p.Name, p.Value)).ToList(),
        },
        JsonValueKind.Array => new JsonTreeNode
        {
            Key = key,
            Kind = JsonNodeKind.Array,
            Children = el.EnumerateArray().Select(v => Build(null, v)).ToList(),
        },
        JsonValueKind.String => new JsonTreeNode { Key = key, Kind = JsonNodeKind.String, Value = el.GetString() },
        JsonValueKind.Number => new JsonTreeNode { Key = key, Kind = JsonNodeKind.Number, Value = el.GetRawText() },
        JsonValueKind.True or JsonValueKind.False => new JsonTreeNode { Key = key, Kind = JsonNodeKind.Boolean, Value = el.GetRawText() },
        _ => new JsonTreeNode { Key = key, Kind = JsonNodeKind.Null, Value = "null" },
    };
}
