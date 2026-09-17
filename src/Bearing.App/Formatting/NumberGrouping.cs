using System.Text;

namespace Bearing.App.Formatting;

/// <summary>
/// Inserts thousands separators into a number's integer part, for the results grid and nothing else.
/// <para>
/// <b>This is a display layer over <see cref="CellFormat.Display"/>, never a change to it.</b> That text is
/// what the clipboard, the CSV and xlsx exports, <c>Copy as ▸ SQL</c> and the in-cell editor's seed all
/// carry, and it is what <c>ResultEditModel.Coerce</c> parses back — invariantly, with
/// <c>AllowThousands</c> deliberately off (see <see cref="CellFormat.TryParseNumber"/>). A grouped number
/// reaching any of those is the tenfold-write class of bug #26 was: <c>1,234</c> re-read without
/// <c>AllowThousands</c> is refused, and <c>9,5</c> re-read *with* it is <b>95</b>. So the grouped form
/// exists only in the <c>TextBlock</c> a cell draws, and the raw form is what every other path reads.
/// </para>
/// <para>
/// The separator is a comma because the decimal point already is one: the grid renders <c>9.5</c>
/// invariantly whatever the machine's culture says, so grouping by the OS culture would produce
/// <c>1.234.567.89</c> on a comma-decimal locale — two different meanings for the same character in one
/// number. One invariant convention for both halves is the only shape that cannot be misread.
/// </para>
/// <para>
/// It works on the rendered string rather than re-formatting the value: the value is already text by the
/// time it gets here whatever its type, and a purely textual insert cannot change a digit. Anything that is
/// not a plain sign-and-digits integer run — <c>NaN</c>, <c>Infinity</c>, the null token, a value whose
/// integer part is three digits or fewer — is returned untouched.
/// </para>
/// <para>
/// <b>Which columns get here is decided by <c>CellStats.IsNumeric</c></b>, whose set is the primitives plus
/// <c>decimal</c>/<c>float</c>/<c>double</c>. A <c>numeric</c> value too large for <c>decimal</c> arrives as
/// a <c>BigInteger</c> (<see cref="CellFormat.Display"/>'s <c>IFormattable</c> arm) and is <i>not</i> in
/// that set, so such a column draws ungrouped. Deliberately left alone rather than widened here: that set
/// also decides the code colour and which columns the quick-stats bar will aggregate, and
/// <c>CellStats.TryParseNumber</c> has no <c>BigInteger</c> arm to aggregate one with.
/// </para>
/// </summary>
public static class NumberGrouping
{
    /// <summary>The separator inserted between groups. See the class remarks for why it is not the OS
    /// culture's.</summary>
    public const char Separator = ',';

    /// <summary>Digits per group. Three everywhere the comma-and-point convention is used at all — the
    /// locales that group differently (Indian lakh/crore, for one) are also the ones whose decimal point
    /// this display does not follow either.</summary>
    private const int GroupSize = 3;

    /// <summary>
    /// Whether the grid groups digits (<c>AppSettings.GroupNumbersInResults</c>). App-global and set from
    /// settings at startup and on change, exactly as <see cref="CellFormat.Zone"/> is: it is one choice
    /// applied to every result, and threading it through the cell factory, the width arithmetic and every
    /// column kind would be the same flag passed a dozen times.
    /// <para>
    /// <see cref="Apply(string, bool)"/> takes it explicitly so a test states the setting it means instead
    /// of mutating shared state and hoping about ordering (§4.5's trap).
    /// </para>
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The display form of an already-rendered number, honouring <see cref="Enabled"/>.</summary>
    public static string Apply(string text) => Apply(text, Enabled);

    /// <summary>The display form under an explicit setting.</summary>
    public static string Apply(string text, bool enabled) => enabled ? Group(text) : text;

    /// <summary>
    /// Group the integer part of <paramref name="text"/>, unconditionally.
    /// <para>
    /// Refuses anything it cannot read as one number: the integer run has to start the string (after an
    /// optional sign) and has to be followed by the end, a decimal point, or an exponent. That last check is
    /// what keeps this off a value that merely begins with digits — an interval, a version, a dimension —
    /// should such a thing ever reach a column the grid calls numeric.
    /// </para>
    /// </summary>
    public static string Group(string text)
    {
        if (text.Length == 0) return text;

        var start = text[0] is '-' or '+' ? 1 : 0;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;

        var digits = end - start;
        if (digits <= GroupSize) return text;                                  // nothing to separate
        if (end < text.Length && text[end] is not ('.' or 'E' or 'e')) return text;  // not a plain number

        // The first group is the remainder: 1234567 → 1 | 234 | 567. A digit count that divides exactly
        // takes a full leading group rather than an empty one, which would emit a leading separator.
        var lead = digits % GroupSize == 0 ? GroupSize : digits % GroupSize;

        var grouped = new StringBuilder(text.Length + (digits - 1) / GroupSize);
        grouped.Append(text, 0, start);        // the sign, if any
        grouped.Append(text, start, lead);
        for (var i = start + lead; i < end; i += GroupSize)
        {
            grouped.Append(Separator);
            grouped.Append(text, i, GroupSize);
        }
        grouped.Append(text, end, text.Length - end);   // the fraction or exponent, untouched
        return grouped.ToString();
    }
}
