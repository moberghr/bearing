using System;
using System.Collections.Generic;

namespace Bearing.Core.Schema;

/// <summary>
/// What one relation costs on disk (#76), and roughly how many rows it holds.
/// <para>
/// The total-versus-heap split is the part worth carrying separately: a 2 GB table that is 400 MB of heap and
/// 1.6 GB of indexes is a different problem from the reverse, and one "size" number hides which one you have.
/// </para>
/// </summary>
/// <param name="TotalBytes">Heap + indexes + toast — what dropping the table would give back.</param>
/// <param name="TableBytes">The heap alone, toast excluded.</param>
/// <param name="IndexBytes">Every index on the relation, together.</param>
/// <param name="ToastBytes">Out-of-line storage for oversized values, or 0 when the relation has no toast.</param>
/// <param name="EstimatedRows">
/// <c>pg_class.reltuples</c>: an estimate maintained by ANALYZE, free to read, and the number you actually
/// want beside a size. <b>Null when unknown</b> — a never-analysed table reports -1, which has to render as
/// unknown rather than as a row count.
/// </param>
public sealed record RelationSize(
    long TableId,
    long TotalBytes,
    long TableBytes,
    long IndexBytes,
    long ToastBytes,
    long? EstimatedRows);

/// <summary>
/// One database's size on the server.
/// <para>
/// <see cref="Bytes"/> is null when the size could not be read, which is a normal outcome rather than an
/// error: <c>pg_database_size</c> on a database the user cannot connect to raises instead of returning null,
/// so an unprivileged user gets "unknown" for their colleagues' databases and a working tree for their own.
/// </para>
/// </summary>
public sealed record DatabaseSize(string Database, long? Bytes);

/// <summary>
/// Formats a byte count for a tree row (#76).
/// <para>
/// Local rather than <c>pg_size_pretty()</c> so the raw <c>bigint</c> stays available for sorting — "show me
/// the biggest tables" is the actual question, and the server's text output destroys the ordering. Pure, so
/// the rounding is unit-testable (§2.5).
/// </para>
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "kB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// A short human-readable size: <c>0 B</c>, <c>8.0 kB</c>, <c>1.4 GB</c>.
    /// <para>
    /// Powers of 1024 with Postgres's own unit names, which is what <c>pg_size_pretty</c> does — matching it
    /// matters more than being pedantic about kB versus KiB, because the user will compare these numbers with
    /// what psql told them.
    /// </para>
    /// <para>
    /// One decimal below 10 and none above, so a column of sizes stays narrow and a 1.4 GB table does not
    /// read as 1.43871 GB. Bytes never get a decimal — there is no such thing as half a byte.
    /// </para>
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "?";

        var unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Rounding can push a value back over the threshold (1023.97 kB → "1024.0 kB"), which reads as a unit
        // the next one up should have taken.
        if (unit < Units.Length - 1 && Math.Round(value, unit == 0 ? 0 : 1) >= 1024)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{(long)value} {Units[0]}"
            : $"{value.ToString(value < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture)} {Units[unit]}";
    }

    /// <summary>
    /// The row-count estimate, or null. Labelled as an estimate by the caller — a number presented as exact
    /// when ANALYZE last ran a week ago is worse than one presented as approximate.
    /// </summary>
    public static string? FormatRows(long? estimatedRows)
        => estimatedRows is { } rows and >= 0
            ? $"~{rows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} rows"
            : null;
}
