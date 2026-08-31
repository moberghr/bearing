using System;
using Bearing.Core.Data;

namespace Bearing.App.Connections;

/// <summary>
/// The pure half of the Connections panel's filter box. Substring and case-insensitive, following
/// <c>ScriptSearch</c> rather than the subsequence matching of <c>SchemaTreeSearch</c>: the
/// type-ahead is a jump-to-the-next-match gesture, where fuzzy earns its looseness, while this box narrows a
/// list you are reading, where fuzzy would leave half the unwanted rows in.
/// <para>
/// The two coexist on one tree by answering different questions. This filters the <b>connections</b> — which
/// servers are on screen at all. The type-ahead searches <b>inside</b> whatever is expanded, down to columns.
/// </para>
/// </summary>
public static class ConnectionSearch
{
    /// <summary>
    /// Whether a connection survives the filter. Matched against the name, the <c>host:port</c> endpoint, the
    /// database, the user and the environment label — everything the row can be recognised by. Filtering on
    /// the name alone would miss the case the panel is worst at today: finding which of several similarly
    /// named connections is the one on 5434.
    /// </summary>
    public static bool Matches(ConnectionInfo c, string filter)
    {
        if (filter.Length == 0) return true;
        return Has(c.Name) || Has(ConnectionEndpoint.HostPort(c)) || Has(c.Database)
            || Has(c.User) || Has(c.Environment);

        bool Has(string? text)
            => text is not null && text.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
