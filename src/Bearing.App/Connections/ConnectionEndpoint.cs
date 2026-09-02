using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// How a connection's endpoint is written when the app has to name it in a message. Pure, so the one
/// awkward case is testable (§2.5).
/// <para>
/// SQL Server's <c>HOST\INSTANCE</c> form is why this is a function and not an interpolation: a named
/// instance is resolved by the SQL Browser service, which means the port is not part of the address and
/// printing one alongside it invites the user to go and check a port that has nothing to do with their
/// failure. Postgres has no such form, so its output is unchanged.
/// </para>
/// </summary>
internal static class ConnectionEndpoint
{
    /// <summary>True when <paramref name="host"/> names a SQL Server instance rather than just a machine
    /// (<c>SQLPROD\SALES</c>). The backslash is the instance separator and is legal in no hostname.</summary>
    public static bool IsNamedInstance(string? host) => host is not null && host.Contains('\\');

    /// <summary><c>host:port/database</c>, or <c>host\instance/database</c> when the port would be
    /// meaningless.</summary>
    public static string Of(ConnectionInfo info) => Of(info.Host, info.Port, info.Database);

    /// <inheritdoc cref="Of(ConnectionInfo)"/>
    public static string Of(string host, int port, string database)
        => IsNamedInstance(host) ? $"{host}/{database}" : $"{host}:{port}/{database}";
}
