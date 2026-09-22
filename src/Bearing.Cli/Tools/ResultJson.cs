using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Bearing.Core.Data;

namespace Bearing.Cli.Tools;

/// <summary>
/// A <see cref="QueryResult"/> as JSON for a caller that is a program, not a person.
/// <para>
/// <b>Deliberately not the grid's text.</b> <c>CellFormat.Display</c> renders for a human and for the
/// clipboard — it applies the display time zone, and digit grouping sits over the top of it (§9.10c) — and
/// every one of those choices is wrong here: a consumer that has to parse <c>1,234</c> back out of a string
/// is worse off than one handed <c>1234</c>. Numbers stay numbers, booleans stay booleans, and null stays
/// null rather than becoming a token that collides with the string "null".
/// </para>
/// <para>
/// <b>Timestamps carry §5.5 on their face.</b> Round-trip format, so a <c>timestamptz</c> (which the driver
/// hands over as <c>Kind = Utc</c>) ends in <c>Z</c> and a <c>timestamp</c> (<c>Kind = Unspecified</c>) ends
/// in nothing at all. That asymmetry is the whole point and must not be tidied up: appending an offset to
/// the zone-less one would invent information the column never had, and stripping it from the other would
/// throw away the only thing that makes it an instant. The display zone is not applied — this is data, and
/// the caller is not sitting in the user's time zone.
/// </para>
/// </summary>
public static class ResultJson
{
    /// <summary>One result set: its columns, its rows, and whether the rows are all of them.</summary>
    public static JsonObject Of(QueryResult result)
    {
        var columns = new JsonArray();
        foreach (var column in result.Columns)
            columns.Add(new JsonObject { ["name"] = column.Name, ["type"] = column.DataTypeName });

        var rows = new JsonArray();
        foreach (var row in result.Rows)
        {
            var cells = new JsonArray();
            foreach (var value in row) cells.Add(Value(value));
            rows.Add(cells);
        }

        return new JsonObject
        {
            ["columns"] = columns,
            ["rows"] = rows,
            ["row_count"] = result.Rows.Count,
            // Not "there are more rows" — "this read stopped at the ceiling with rows still waiting". A
            // caller that needs the rest narrows the query; paging it would hand back a different snapshot.
            ["truncated"] = result.Truncated,
            ["duration_ms"] = (long)result.Duration.TotalMilliseconds,
        };
    }

    /// <summary>
    /// One value, in the nearest JSON type that does not lose what the column meant.
    /// <para>
    /// The fallback is <c>ToString()</c> under the invariant culture, and the culture matters: a
    /// <c>ToString()</c> on a Croatian machine renders a date <c>21.09.2026.</c> and a decimal with a comma,
    /// so a consumer's parse would depend on where the user was sitting (§9.10c reached from the other
    /// side).
    /// </para>
    /// </summary>
    public static JsonNode? Value(object? value) => value switch
    {
        null or DBNull => null,

        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),

        // Every integral and floating width the two drivers produce, kept as numbers. System.Text.Json
        // writes a decimal's digits exactly, so a numeric column does not go through a double on the way
        // out — a consumer that parses into a float has made that choice itself.
        byte n => JsonValue.Create(n),
        sbyte n => JsonValue.Create(n),
        short n => JsonValue.Create(n),
        ushort n => JsonValue.Create(n),
        int n => JsonValue.Create(n),
        uint n => JsonValue.Create(n),
        long n => JsonValue.Create(n),
        ulong n => JsonValue.Create(n),
        decimal n => JsonValue.Create(n),
        double n => JsonValue.Create(n),
        float n => JsonValue.Create(n),

        // A `numeric` too large for decimal. A string rather than a number: it has no JSON number that
        // round-trips, and quietly narrowing it would be worse than making the consumer decide.
        BigInteger n => JsonValue.Create(n.ToString(CultureInfo.InvariantCulture)),

        // "O" is what encodes §5.5: Utc ends in Z, Unspecified ends in nothing, and an offset keeps its own.
        DateTime d => JsonValue.Create(d.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset d => JsonValue.Create(d.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("O", CultureInfo.InvariantCulture)),
        TimeOnly t => JsonValue.Create(t.ToString("O", CultureInfo.InvariantCulture)),
        TimeSpan t => JsonValue.Create(t.ToString("c", CultureInfo.InvariantCulture)),

        Guid g => JsonValue.Create(g.ToString()),

        // Base64 rather than the engine's own literal spelling, which is not the same on both (`\x…` against
        // `0x…`) and would make a consumer branch on which engine answered.
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),

        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };
}
