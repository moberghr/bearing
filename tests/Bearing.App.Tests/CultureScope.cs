using System;
using System.Globalization;

namespace Bearing.App.Tests;

/// <summary>
/// Runs a test body under a chosen <see cref="CultureInfo.CurrentCulture"/>, then restores it. Culture is
/// per-execution-context, so this does not disturb tests running in parallel.
/// <para>
/// Two reasons a test reaches for this, and they pull opposite ways — say which one applies:
/// <list type="bullet">
/// <item><b>Expose</b> a locale bug that an en-US or invariant box hides, the way
/// <c>CultureInvarianceTests</c> pins hr-HR to catch a decimal separator leaking into parsed text.</item>
/// <item><b>Pin</b> a deliberately localized string so its literal stays readable. The row-count phrase is
/// the example: "1,000" on en-US and "1.000" on hr-HR are both correct, so an unpinned assertion is really
/// asserting the developer's machine (#83).</item>
/// </list>
/// </para>
/// </summary>
internal static class CultureScope
{
    public static void In(string name, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try { body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
