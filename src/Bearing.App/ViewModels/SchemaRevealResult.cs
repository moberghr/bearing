namespace Bearing.App.ViewModels;

/// <summary>
/// How far a schema-tree reveal got (#117). Named outcomes rather than a bool, because each one is a
/// different sentence to put in the status bar — and "the table is not there" and "the connection is not
/// there" send the user to different places.
/// </summary>
public enum SchemaRevealResult
{
    /// <summary>The row was found, expanded to and selected.</summary>
    Revealed,

    /// <summary>No server row for that connection — it was deleted, or the project changed under us.</summary>
    NoConnection,

    /// <summary>The server has no such database.</summary>
    NoDatabase,

    /// <summary>The database has no such relation. The commonest real answer: a stale snapshot, or a table
    /// in a schema the browser filters out.</summary>
    NoRelation,

    /// <summary>The relation was reached and selected, but it has no such column.</summary>
    NoColumn,
}
