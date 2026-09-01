using System;
using System.Collections.Generic;
using System.Linq;

namespace Bearing.App.Results;

/// <summary>
/// Turns a run's wanted sheet names into a set Excel will accept (#12). Pure, because the interesting cases
/// are collisions nobody produces on purpose (§2.5).
/// <para>
/// Excel refuses a workbook outright if two sheets share a name, and it compares them case-insensitively.
/// Three ordinary things collide: two result sets from the same table (one run, two selects — entirely
/// normal), two names that differ only in case, and two long names that
/// <see cref="XlsxWriter.SafeSheetName"/> truncates to the same 31 characters. The last is the reason
/// de-duplication has to run <b>after</b> sanitizing rather than before: sanitizing is itself a source of
/// collisions.
/// </para>
/// </summary>
internal static class SheetNames
{
    /// <summary>Excel's own limit on a sheet name.</summary>
    private const int MaxLength = 31;

    /// <summary>
    /// The names to write, in the order given: each sanitized, then made unique by a <c> (2)</c>, <c> (3)</c>
    /// suffix. The first claim on a name keeps it, so a run's sheets stay in the order the user sees them and
    /// the suffix lands on the later duplicate.
    /// </summary>
    public static IReadOnlyList<string> Unique(IEnumerable<string> names)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var name in names)
        {
            var candidate = XlsxWriter.SafeSheetName(name);
            if (!taken.Add(candidate))
            {
                // Counting from 2: the first one is unsuffixed, so "orders" and "orders (2)" reads as one
                // pair rather than as an unnumbered stray beside a numbered set.
                for (var n = 2; ; n++)
                {
                    var numbered = WithSuffix(candidate, n);
                    if (taken.Add(numbered)) { candidate = numbered; break; }
                }
            }
            result.Add(candidate);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="name"/> with a <c> (n)</c> suffix, trimming the stem to keep the whole within Excel's
    /// limit. The suffix survives the trim, not the stem: a truncated name that is still unique is usable,
    /// where a full name that collides makes the workbook unopenable.
    /// </summary>
    private static string WithSuffix(string name, int n)
    {
        var suffix = $" ({n})";
        var room = MaxLength - suffix.Length;
        var stem = name.Length > room ? name[..room] : name;
        // Trailing space before the bracket reads as a mistake, and a lone "'" is what SafeSheetName strips.
        return stem.TrimEnd(' ', '\'') + suffix;
    }
}
