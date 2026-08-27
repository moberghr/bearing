using System;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// Identity of a live session: a connection <b>and</b> the database it is open on. A pool is bound to one
/// database (it is in the connection string), so keying sessions by connection id alone made a database
/// switch look like "settings changed" and threw away a working pool — with its TLS handshake, temp tables,
/// <c>SET</c> values and prepared statements — on every switch, in both directions (#54).
///
/// Equality is ordinal on <see cref="Database"/>, matching how the rest of the app compares database names.
/// </summary>
public readonly record struct SessionKey(Guid ConnectionId, string Database)
{
    /// <summary>The key for a resolved connection — normally a <see cref="Workspace.WorkspaceContext.EffectiveConnection"/>
    /// effective connection, which has the tab's active database substituted in.</summary>
    public static SessionKey For(ConnectionInfo info) => new(info.Id, info.Database);

    public override string ToString() => $"{ConnectionId:D}/{Database}";
}
