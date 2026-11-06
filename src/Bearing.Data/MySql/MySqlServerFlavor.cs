using System.Globalization;

namespace Bearing.Data.MySql;

/// <summary>
/// Which of the two engines a connection reached, and which version — the facts the shared dialect cannot
/// know (it is text-only, §2.2) and the executor must: whether <c>INSERT … RETURNING</c> exists (MariaDB
/// 10.5+), and nothing else so far. Read from the server's handshake version string, so it costs nothing.
/// </summary>
/// <param name="IsMariaDb">True for MariaDB, false for MySQL (and anything else speaking the protocol).</param>
/// <param name="Major">Major version, 0 when unparseable.</param>
/// <param name="Minor">Minor version, 0 when unparseable.</param>
public readonly record struct MySqlServerFlavor(bool IsMariaDb, int Major, int Minor)
{
    /// <summary>MariaDB 10.5 added <c>RETURNING</c> to INSERT; MySQL has none.</summary>
    public bool SupportsInsertReturning => IsMariaDb && (Major > 10 || (Major == 10 && Minor >= 5));

    /// <summary>
    /// <c>8.4.3</c>, <c>11.4.2-MariaDB-ubu2404</c>, and the old replication-compatible spelling
    /// <c>5.5.5-10.11.6-MariaDB</c>, whose real version follows the fake <c>5.5.5-</c> prefix.
    /// </summary>
    public static MySqlServerFlavor Parse(string? serverVersion)
    {
        var text = serverVersion ?? "";
        var isMaria = text.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        if (isMaria && text.StartsWith("5.5.5-", StringComparison.Ordinal))
        {
            text = text["5.5.5-".Length..];
        }

        var parts = text.Split('.', '-');
        return new MySqlServerFlavor(isMaria, Number(parts, 0), Number(parts, 1));
    }

    private static int Number(string[] parts, int index)
        => index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : 0;
}
