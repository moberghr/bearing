using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// One spelling for "which server is this", shared by the schema tree's server row, the toolbar pill's
/// tooltip, the credential prompt and the connect-failure message (#79). Those four described the same
/// endpoint four ways — three of them without the port — so two Postgres instances on one host read as the
/// same server everywhere except the error you got when the wrong one failed.
///
/// <para>The port is <b>always</b> shown, never "only when non-default": a bare <c>localhost</c> still means
/// "5432, probably", which is an inference, and it is wrong the moment a remote server is tunnelled to 5432.
/// These surfaces are not space-constrained.</para>
/// </summary>
public static class ConnectionEndpoint
{
    /// <summary>
    /// The server itself: <c>host:port</c>. What a tree row's dim second line shows.
    /// <para>
    /// A port of <b>zero</b> is dropped, and that is not the exception the note above forbids: zero is not a
    /// default anyone could infer, it is the absence of a port — which is what a provider that opens no
    /// socket has (the demo provider, #64). Rendering it as <c>demo:0</c> reads as a bug rather than as a
    /// fact.
    /// </para>
    /// </summary>
    public static string HostPort(ConnectionInfo info)
        => info.Port == 0 || IsNamedInstance(info.Host) ? info.Host : $"{info.Host}:{info.Port}";

    /// <summary>
    /// True when <paramref name="host"/> names a SQL Server <em>instance</em> rather than just a machine
    /// (<c>SQLPROD\SALES</c>). The backslash is the instance separator and is legal in no hostname.
    /// <para>
    /// This is the second exception to "the port is always shown", and it is the same kind as a port of
    /// zero: a named instance is resolved by the SQL Browser service, which hands back whatever dynamic
    /// port that instance listens on. The configured port is not the address, so printing it invites the
    /// user to go and check a number that had nothing to do with their failure. Postgres has no such
    /// form, so its output is unchanged.
    /// </para>
    /// </summary>
    public static bool IsNamedInstance(string? host) => host is not null && host.Contains('\\');

    /// <summary>The server and what it is pointed at: <c>host:port/database</c>. A connection with no
    /// database drops the slash rather than trailing one.</summary>
    public static string Address(ConnectionInfo info)
        => string.IsNullOrWhiteSpace(info.Database) ? HostPort(info) : $"{HostPort(info)}/{info.Database}";

    /// <summary>The fullest form, <c>user@host:port/database</c> — what someone would paste into a bug
    /// report. A blank user drops the <c>@</c>; connections that prompt for their user leave it empty.</summary>
    public static string Full(ConnectionInfo info)
        => string.IsNullOrWhiteSpace(info.User) ? Address(info) : $"{info.User}@{Address(info)}";
}
