using Bearing.Core.Data;

namespace Bearing.Sessions;

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
    /// <para>
    /// Through an SSH tunnel the configured host is where the <em>bastion</em> sends the traffic, so on its
    /// own it describes the wrong machine: <c>127.0.0.1:3306</c> reads as this computer. A loopback target
    /// means the database runs on the bastion itself, so the bastion is named in its place
    /// (<c>db.example.com:3306 via ssh</c>). Any other target keeps its address and names the bastion after
    /// it (<c>10.0.0.5:3306 via ssh bastion.example.com</c>).
    /// </para>
    /// </summary>
    public static string HostPort(ConnectionInfo info)
    {
        var (where, via) = Split(info);
        return where + via;
    }

    /// <summary>Where the server is, and the tunnel suffix (empty without one) that every form puts last, so
    /// <see cref="Address"/> reads <c>host:port/db via ssh</c> rather than <c>host:port via ssh/db</c>.</summary>
    private static (string Where, string Via) Split(ConnectionInfo info)
    {
        var host = info.Host;
        var via = "";
        if (info.SshTunnel is { } ssh && !string.IsNullOrWhiteSpace(ssh.Host))
        {
            // A loopback target is the bastion itself, so the bastion is named in its place.
            var onBastion = TlsPolicy.IsLoopback(info.Host);
            host = onBastion ? ssh.Host : info.Host;
            via = onBastion ? " via ssh" : $" via ssh {ssh.Host}";
        }
        return (info.Port == 0 || IsNamedInstance(host) ? host : $"{host}:{info.Port}", via);
    }

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
    {
        var (where, via) = Split(info);
        return string.IsNullOrWhiteSpace(info.Database) ? where + via : $"{where}/{info.Database}{via}";
    }

    /// <summary>The fullest form, <c>user@host:port/database</c> — what someone would paste into a bug
    /// report. A blank user drops the <c>@</c>; connections that prompt for their user leave it empty. One that
    /// asks az for its user says so in the user's place, since the name is not known until it connects.</summary>
    public static string Full(ConnectionInfo info)
        => EntraTokenProvider.WantsUser(info) ? $"(az login)@{Address(info)}"
           : string.IsNullOrWhiteSpace(info.User) ? Address(info) : $"{info.User}@{Address(info)}";
}
