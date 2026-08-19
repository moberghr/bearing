using System;
using System.Collections.Generic;

namespace Bearing.App.ViewModels;

/// <summary>
/// Naming and ordering rules for the flat object list under a database node. Objects outside the
/// connection's default schema carry a <c>schema.</c> prefix, so a row says which schema it is in without
/// being selected, and the default schema's objects — the ones a bare query name resolves to — stay
/// unprefixed and sort above everything else. Pure, so the rules are unit-testable without a tree (§2.5).
/// </summary>
internal static class SchemaObjectLabel
{
    /// <summary>
    /// The schema an unqualified name resolves to: search_path's first entry (<c>current_schema</c>),
    /// falling back to <c>public</c> for a snapshot that reported no reachable schema at all.
    /// </summary>
    public static string DefaultSchemaOf(IReadOnlyList<string> searchPath)
        => searchPath.Count > 0 ? searchPath[0] : "public";

    /// <summary>Ordinal on purpose: both names come from the catalog, and two schemas may differ only by case.</summary>
    public static bool IsDefault(string schema, string defaultSchema)
        => string.Equals(schema, defaultSchema, StringComparison.Ordinal);

    /// <summary>Sort bucket: the default schema's objects first, everything else after.</summary>
    public static int SchemaRank(string schema, string defaultSchema) => IsDefault(schema, defaultSchema) ? 0 : 1;

    /// <summary>Bare name in the default schema, <c>schema.name</c> everywhere else.</summary>
    public static string Title(string schema, string name, string defaultSchema)
        => IsDefault(schema, defaultSchema) ? name : $"{schema}.{name}";

    /// <summary>
    /// The schema belongs on a row exactly once, so it stays in the detail line only while the title is
    /// bare — a prefixed title would otherwise repeat it two words later.
    /// </summary>
    public static string Detail(string kindLabel, string schema, string defaultSchema)
        => IsDefault(schema, defaultSchema) ? $"{kindLabel} · {schema}" : kindLabel;
}
