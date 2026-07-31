namespace Squirrel.App.Connections;

/// <summary>
/// The live-session state of the selected tab's connection, surfaced by the toolbar/status-bar
/// indicator. Independent of the database <em>environment</em> color: the status dot is always
/// green when <see cref="Connected"/>, amber while <see cref="Connecting"/>, red when
/// <see cref="Disconnected"/>, whatever environment the connection is tagged with.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}
