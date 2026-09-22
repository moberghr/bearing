using System.Collections;
using System.Globalization;
using System.Numerics;
using Bearing.Core.Data;

namespace Bearing.Cli.Tools;

/// <summary>
/// One cell, converted to the CLR type that serialises to the right JSON — for a caller that is a program,
/// not a person.
/// <para>
/// <b>Deliberately not the grid's text.</b> <c>CellFormat.Display</c> renders for a human and for the
/// clipboard — it applies the display time zone, and digit grouping sits over the top of it (§9.10c) — and
/// every one of those choices is wrong here: a consumer that has to parse <c>1234</c> back out of
/// <c>"1,234"</c> is worse off than one handed a number. Numbers stay numbers, booleans stay booleans, and
/// null stays null rather than becoming a token that collides with the string "null".
/// </para>
/// <para>
/// <b>Timestamps carry §5.5 on their face.</b> Round-trip format, so a <c>timestamptz</c> (which the driver
/// hands over as <c>Kind = Utc</c>) ends in <c>Z</c> and a <c>timestamp</c> (<c>Kind = Unspecified</c>) ends
/// in nothing at all. That asymmetry is the whole point and must not be tidied up: appending an offset to
/// the zone-less one would invent information the column never had, and stripping it from the other would
/// throw away the only thing that makes it an instant. The display zone is not applied — this is data, and
/// the caller is not sitting in the user's time zone.
/// </para>
/// <para>
/// Converting here rather than in a <c>JsonConverter</c> keeps it a pure function of one value, which is
/// what makes every case above a unit test rather than a round trip through a serializer.
/// </para>
/// </summary>
public static class CellValue
{
    public static object? For(object? value) => value switch
    {
        null or DBNull => null,

        string s => s,
        bool b => b,

        // Every integral and floating width the two drivers produce, kept as numbers. System.Text.Json
        // writes a decimal's digits exactly, so a numeric column does not go through a double on the way
        // out — a consumer that parses into a float has made that choice itself.
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal or double or float
            => value,

        // A `numeric` too large for decimal. A string rather than a number: it has no JSON number that
        // round-trips, and quietly narrowing it would be worse than making the consumer decide.
        BigInteger n => n.ToString(CultureInfo.InvariantCulture),

        // "O" is what encodes §5.5: Utc ends in Z, Unspecified ends in nothing, and an offset keeps its own.
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),

        Guid g => g.ToString(),

        // Base64 rather than the engine's own literal spelling, which is not the same on both (`\x…`
        // against `0x…`) and would make a consumer branch on which engine answered.
        // Before the arms below it, because a byte[] is an Array and a bytea is not a list of 200 numbers.
        byte[] bytes => Convert.ToBase64String(bytes),

        // A Postgres array as a JSON array, element by element — so text[] nests, int[] stays numbers, and a
        // null element stays null. Without this arm it fell to the invariant ToString below, and an Array is
        // neither IConvertible nor IFormattable, so `select special_features from film` answered with the
        // literal string "System.String[]": the value gone rather than merely formatted oddly, and looking
        // enough like data to be stored. The same shape TableFormats.Json already writes for the clipboard.
        Array array => array.Cast<object?>().Select(For).ToList(),

        // hstore, which Npgsql hands over as a dictionary. A JSON object, values converted like any other
        // cell — it reached the same ToString and produced the same class of nonsense.
        IDictionary dictionary => dictionary.Keys
            .Cast<object>()
            .ToDictionary(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? "", k => For(dictionary[k])),

        // The fallback is invariant, and the culture matters: ToString() on a Croatian machine renders a
        // decimal with a comma, so a consumer's parse would depend on where the user was sitting (§9.10c
        // reached from the other side).
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>One result set, as the record the caller is handed.</summary>
    public static ResultSet SetOf(QueryResult result)
    {
        var columns = result.Columns
            .Select(c => new ColumnHeader(c.Name, c.DataTypeName))
            .ToList();

        var rows = result.Rows
            .Select(row => (IReadOnlyList<object?>)row.Select(For).ToList())
            .ToList();

        return new ResultSet(
            columns,
            rows,
            result.Rows.Count,
            // Not "there are more rows" — "this read stopped at the ceiling with rows still waiting". A
            // caller that needs the rest narrows the query; paging it would hand back a different snapshot.
            result.Truncated,
            (long)result.Duration.TotalMilliseconds)
        {
            // A statement that returned no grid still said something ("SELECT 0", an affected-row count).
            // It is the only answer such a statement has, so dropping it would report success with nothing.
            Message = string.IsNullOrWhiteSpace(result.Message) ? null : result.Message,
        };
    }
}
