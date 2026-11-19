using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;

namespace Bearing.Data.DynamoDb;

/// <summary>What a grid column holds. Chosen once, from the first page (<see cref="DynamoDbColumnLayout"/>).</summary>
internal enum DynamoDbColumnType
{
    /// <summary><c>S</c> — and every column that is mixed, inexact, or holds maps, lists or sets as JSON text.</summary>
    Text,

    /// <summary><c>N</c>, every value of which fits a <see cref="decimal"/> exactly.</summary>
    Number,

    Boolean,

    Binary,
}

/// <summary>One data column of a layout.</summary>
internal sealed record DynamoDbColumn(string Name, DynamoDbColumnType Type);

/// <summary>
/// The columns a result is fixed to. Decided by the first page and then carried (in the continuation) to every
/// later one, because the grid never rebuilds its columns mid-result (§9.10). <see cref="HasOverflow"/> adds the
/// trailing <see cref="DynamoDbValues.OtherAttributes"/> column, which is not one of <see cref="Columns"/>.
/// </summary>
internal sealed record DynamoDbColumnLayout(IReadOnlyList<DynamoDbColumn> Columns, bool HasOverflow)
{
    /// <summary>The descriptors the result carries: the data columns, then the overflow column when present.</summary>
    public IReadOnlyList<ColumnDescriptor> Descriptors()
    {
        var descriptors = Columns
            .Select(c => new ColumnDescriptor(c.Name, DynamoDbValues.TypeName(c.Type), DynamoDbValues.ClrType(c.Type)))
            .ToList();

        if (HasOverflow)
        {
            descriptors.Add(new ColumnDescriptor(
                OverflowName,
                DynamoDbValues.TypeName(DynamoDbColumnType.Text),
                typeof(string)));
        }

        return descriptors;
    }

    /// <summary>
    /// The overflow column's name: <see cref="DynamoDbValues.OtherAttributes"/>, unless an item has a real
    /// attribute of that name — then <c>(other attributes 2)</c>, <c>3</c>, … whichever no data column holds, so a
    /// result never carries two columns of one name. Derived from <see cref="Columns"/> alone, which travel in the
    /// continuation, so every page of a result agrees.
    /// </summary>
    public string OverflowName
    {
        get
        {
            var taken = Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            if (!taken.Contains(DynamoDbValues.OtherAttributes)) return DynamoDbValues.OtherAttributes;

            for (var n = 2; ; n++)
            {
                var candidate = $"(other attributes {n})";
                if (!taken.Contains(candidate)) return candidate;
            }
        }
    }
}

/// <summary>
/// DynamoDB attribute values as grid cells (<i>Behaviour 5 and 7</i>). Pure: an <see cref="AttributeValue"/> in, a
/// cell out, no client and no clock.
/// <para>
/// <b>Reading the SDK's value type.</b> In SDK V4 the collection properties (<c>M</c>, <c>L</c>, <c>SS</c>,
/// <c>NS</c>, <c>BS</c>) are null rather than empty when the service did not send them, and <c>BOOL</c> /
/// <c>NULL</c> are nullable, so each is read for what it is, scalars first. Only when
/// <c>AWSConfigs.InitializeCollections</c> is on (Bearing never sets it) would an unrelated collection be an
/// empty non-null one; the collection read therefore prefers a non-empty one.
/// </para>
/// <para>
/// A missing attribute and a DynamoDB <c>NULL</c> both read as null — the known limitation the spec records.
/// </para>
/// </summary>
internal static partial class DynamoDbValues
{
    /// <summary>The trailing overflow column's name — unless a real attribute holds it
    /// (<see cref="DynamoDbColumnLayout.OverflowName"/>).</summary>
    public const string OtherAttributes = "(other attributes)";

    // The strict JSON number grammar. DynamoDB writes numbers like 100, -1.5 and 1E+126; anything else (it
    // should not happen) is written as a JSON string rather than risk invalid JSON.
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();

    [GeneratedRegex(@"^([+-]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]{1,6}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberShape();

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        // Readable text, not \u-escaped: the cell is shown and copied as written.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    public static string TypeName(DynamoDbColumnType type) => type switch
    {
        DynamoDbColumnType.Number => "number",
        DynamoDbColumnType.Boolean => "bool",
        DynamoDbColumnType.Binary => "binary",
        _ => "string",
    };

    public static Type ClrType(DynamoDbColumnType type) => type switch
    {
        DynamoDbColumnType.Number => typeof(decimal),
        DynamoDbColumnType.Boolean => typeof(bool),
        DynamoDbColumnType.Binary => typeof(byte[]),
        _ => typeof(string),
    };

    // ---- one value ---------------------------------------------------------------------------------

    /// <summary>
    /// The value as its natural cell: <c>S</c> a string; <c>N</c> a <see cref="decimal"/> when it parses exactly,
    /// else its exact text; <c>BOOL</c> a bool; <c>B</c> a <c>byte[]</c>; a collection its JSON text; a missing
    /// attribute or <c>NULL</c> null.
    /// </summary>
    public static object? ToCell(AttributeValue? value)
    {
        if (value is null) return null;
        if (value.S is not null) return value.S;
        if (value.N is not null) return TryExactDecimal(value.N, out var number) ? number : value.N;
        if (value.BOOL is { } flag) return flag;
        if (value.NULL is true) return null;
        if (value.B is not null) return value.B.ToArray();

        return IsCollection(value) ? ToJson(value) : null;
    }

    /// <summary>The value as JSON: maps with their keys in ordinal order, numbers unquoted exactly as written,
    /// binary as base64, sets as arrays (sorted, since a set has no order of its own), <c>NULL</c> as
    /// <c>null</c>. Stable, so the same item always reads the same.</summary>
    public static string ToJson(AttributeValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            Write(writer, value);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// <paramref name="text"/> as a decimal, only when that is <em>exact</em>: .NET parses an over-long or
    /// out-of-range-small number to a rounded value (or zero) without complaint, so the parsed value is compared
    /// back with the text, digit for digit.
    /// </summary>
    public static bool TryExactDecimal(string text, out decimal value)
    {
        value = 0;

        if (!decimal.TryParse(
                text,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed)) return false;

        if (!Canonical(text, out var original) || !Canonical(parsed.ToString(CultureInfo.InvariantCulture), out var roundTripped)) return false;
        if (original != roundTripped) return false;

        value = parsed;

        return true;
    }

    // ---- layout and rows ---------------------------------------------------------------------------

    /// <summary>
    /// The layout of a result from its first page: the key attributes first (partition, then sort — in the order
    /// given), then every other attribute seen, ordinal-alphabetical. A key attribute is a column even when this
    /// page has no value for it. A column's type is that of its first non-null value, or text when its values
    /// disagree (or are all null).
    /// <para>
    /// <paramref name="projected"/> — the names a <c>SELECT</c> projects (<see cref="PartiQlBatch.Projection"/>) —
    /// are columns too, whether or not the first page showed them: a filtered read's first page can come back empty
    /// with a token, and a layout fixed from nothing would push every later attribute into the overflow column.
    /// </para>
    /// </summary>
    public static DynamoDbColumnLayout Layout(
        IEnumerable<IReadOnlyDictionary<string, AttributeValue>> firstPage,
        IReadOnlyList<string> keyAttributes,
        bool hasOverflow,
        IReadOnlyList<string>? projected = null)
    {
        ArgumentNullException.ThrowIfNull(firstPage);
        ArgumentNullException.ThrowIfNull(keyAttributes);

        var items = firstPage.ToList();
        var names = keyAttributes
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var others = items
            .SelectMany(item => item.Keys)
            .Concat(projected ?? [])
            .Distinct(StringComparer.Ordinal)
            .Where(name => !names.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal);

        names.AddRange(others);

        var columns = names
            .Select(name => new DynamoDbColumn(name, TypeOfColumn(items, name)))
            .ToList();

        return new DynamoDbColumnLayout(columns, hasOverflow);
    }

    /// <summary>
    /// One row under a fixed layout. An attribute the layout has no column for — or one whose type does not fit
    /// its column (a number in what the first page made a boolean column) — goes to the
    /// <see cref="OtherAttributes"/> column as JSON when the layout has one, so nothing is dropped. Without
    /// that column a misfit keeps its natural cell rather than vanish.
    /// </summary>
    public static object?[] Row(IReadOnlyDictionary<string, AttributeValue> item, DynamoDbColumnLayout layout)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(layout);

        var width = layout.Columns.Count + (layout.HasOverflow ? 1 : 0);
        var row = new object?[width];
        var spilled = layout.HasOverflow ? new SortedDictionary<string, AttributeValue>(StringComparer.Ordinal) : null;

        for (var i = 0; i < layout.Columns.Count; i++)
        {
            var column = layout.Columns[i];

            if (!item.TryGetValue(column.Name, out var value)) continue;

            if (TryFit(value, column.Type, out var cell))
            {
                row[i] = cell;
            }
            else if (spilled is not null)
            {
                spilled[column.Name] = value;
            }
            else
            {
                row[i] = ToCell(value);
            }
        }

        if (spilled is null) return row;

        var known = layout.Columns
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (name, value) in item)
        {
            if (!known.Contains(name)) spilled[name] = value;
        }

        row[width - 1] = spilled.Count == 0 ? null : ToJson(new AttributeValue { M = new Dictionary<string, AttributeValue>(spilled) });

        return row;
    }

    // ---- internals ---------------------------------------------------------------------------------

    private static DynamoDbColumnType TypeOfColumn(List<IReadOnlyDictionary<string, AttributeValue>> items, string name)
    {
        DynamoDbColumnType? type = null;

        foreach (var item in items)
        {
            if (!item.TryGetValue(name, out var value)) continue;

            var natural = NaturalType(value);
            if (natural is null) continue;

            if (type is null) type = natural;
            else if (type != natural) return DynamoDbColumnType.Text;
        }

        return type ?? DynamoDbColumnType.Text;
    }

    /// <summary>The column type a value would make on its own; null for a value that says nothing (NULL).</summary>
    private static DynamoDbColumnType? NaturalType(AttributeValue value)
    {
        if (value.S is not null) return DynamoDbColumnType.Text;
        if (value.N is not null) return TryExactDecimal(value.N, out _) ? DynamoDbColumnType.Number : DynamoDbColumnType.Text;
        if (value.BOOL is not null) return DynamoDbColumnType.Boolean;
        if (value.NULL is true) return null;
        if (value.B is not null) return DynamoDbColumnType.Binary;

        return IsCollection(value) ? DynamoDbColumnType.Text : null;
    }

    /// <summary>The cell for <paramref name="value"/> in a column of <paramref name="type"/>; false when it does
    /// not fit. A text column takes everything, as text.</summary>
    private static bool TryFit(AttributeValue value, DynamoDbColumnType type, out object? cell)
    {
        cell = null;

        if (value.NULL is true && value.S is null && value.N is null) return true;

        switch (type)
        {
            case DynamoDbColumnType.Text:
                cell = AsText(value);

                return true;

            case DynamoDbColumnType.Number when value.N is not null && TryExactDecimal(value.N, out var number):
                cell = number;

                return true;

            case DynamoDbColumnType.Boolean when value.BOOL is { } flag:
                cell = flag;

                return true;

            case DynamoDbColumnType.Binary when value.B is not null:
                cell = value.B.ToArray();

                return true;

            default:
                return false;
        }
    }

    /// <summary>A value as the text a mixed column shows: the number as written, a flag as true/false, binary as
    /// base64, everything else as <see cref="ToCell"/> would have it.</summary>
    private static string? AsText(AttributeValue value)
    {
        if (value.S is not null) return value.S;
        if (value.N is not null) return value.N;
        if (value.BOOL is { } flag) return flag ? "true" : "false";
        if (value.B is not null) return Convert.ToBase64String(value.B.ToArray());

        return IsCollection(value) ? ToJson(value) : null;
    }

    private static bool IsCollection(AttributeValue value)
        => value.M is not null || value.L is not null || value.SS is not null || value.NS is not null || value.BS is not null;

    private static void Write(Utf8JsonWriter writer, AttributeValue value)
    {
        if (value.S is not null)
        {
            writer.WriteStringValue(value.S);
        }
        else if (value.N is not null)
        {
            WriteNumber(writer, value.N);
        }
        else if (value.BOOL is { } flag)
        {
            writer.WriteBooleanValue(flag);
        }
        else if (value.NULL is true)
        {
            writer.WriteNullValue();
        }
        else if (value.B is not null)
        {
            writer.WriteStringValue(Convert.ToBase64String(value.B.ToArray()));
        }
        else
        {
            WriteCollection(writer, value);
        }
    }

    private static void WriteCollection(Utf8JsonWriter writer, AttributeValue value)
    {
        // The non-empty collection first: a value of one kind carries only that one (see the class remarks).
        if (value.M is { Count: > 0 } || (value.M is not null && IsOnly(value, "M")))
        {
            writer.WriteStartObject();

            foreach (var (name, child) in value.M!.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                Write(writer, child);
            }

            writer.WriteEndObject();
        }
        else if (value.L is { Count: > 0 } || (value.L is not null && IsOnly(value, "L")))
        {
            writer.WriteStartArray();

            foreach (var child in value.L!) Write(writer, child);

            writer.WriteEndArray();
        }
        else if (value.SS is { Count: > 0 } || (value.SS is not null && IsOnly(value, "SS")))
        {
            writer.WriteStartArray();

            foreach (var text in value.SS!.Order(StringComparer.Ordinal)) writer.WriteStringValue(text);

            writer.WriteEndArray();
        }
        else if (value.NS is { Count: > 0 } || (value.NS is not null && IsOnly(value, "NS")))
        {
            writer.WriteStartArray();

            foreach (var number in SortedNumbers(value.NS!)) WriteNumber(writer, number);

            writer.WriteEndArray();
        }
        else if (value.BS is not null)
        {
            writer.WriteStartArray();

            foreach (var text in value.BS.Select(b => Convert.ToBase64String(b.ToArray())).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(text);
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    /// <summary>True when every collection other than <paramref name="which"/> is null or empty — the only way an
    /// empty collection is the value rather than a placeholder for another.</summary>
    private static bool IsOnly(AttributeValue value, string which)
        => (which == "M" || value.M is null or { Count: 0 })
        && (which == "L" || value.L is null or { Count: 0 })
        && (which == "SS" || value.SS is null or { Count: 0 })
        && (which == "NS" || value.NS is null or { Count: 0 })
        && (which == "BS" || value.BS is null or { Count: 0 });

    private static IEnumerable<string> SortedNumbers(List<string> numbers)
    {
        var parsed = numbers
            .Select(n => (Text: n, Ok: decimal.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var d), Value: d))
            .ToList();

        return parsed.All(p => p.Ok)
            ? parsed.OrderBy(p => p.Value).ThenBy(p => p.Text, StringComparer.Ordinal).Select(p => p.Text)
            : numbers.Order(StringComparer.Ordinal);
    }

    private static void WriteNumber(Utf8JsonWriter writer, string number)
    {
        if (JsonNumber().IsMatch(number)) writer.WriteRawValue(number);
        else writer.WriteStringValue(number);
    }

    /// <summary>
    /// A number's significant digits and the position of its decimal point, with leading and trailing zeros
    /// stripped — so <c>1.50</c>, <c>1.5</c> and <c>15E-1</c> are the same number and <c>0.1234…</c> cut short is not.
    /// False for text that is not a number.
    /// </summary>
    private static bool Canonical(string text, out string canonical)
    {
        canonical = "";

        var match = NumberShape().Match(text);
        if (!match.Success) return false;

        var whole = match.Groups[2].Value;
        var fraction = match.Groups[3].Value;
        if (whole.Length + fraction.Length == 0) return false;

        var exponent = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var digits = whole + fraction;
        var point = whole.Length + exponent;

        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits[leading..];
        point -= leading;
        digits = digits.TrimEnd('0');

        // Zero is zero, whatever its sign or spelling.
        canonical = digits.Length == 0
            ? "0"
            : $"{match.Groups[1].Value.Replace("+", "", StringComparison.Ordinal)}{digits}@{point}";

        return true;
    }
}
