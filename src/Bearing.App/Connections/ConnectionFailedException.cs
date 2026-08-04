using System;

namespace Bearing.App.Connections;

/// <summary>Thrown when a connection cannot be established; carries a user-facing message.</summary>
public sealed class ConnectionFailedException : Exception
{
    public ConnectionFailedException(string message, Exception? inner = null) : base(message, inner) { }
}
