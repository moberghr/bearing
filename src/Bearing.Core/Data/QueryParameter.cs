namespace Bearing.Core.Data;

/// <summary>What a <see cref="QueryParameter"/>'s text stands for.</summary>
public enum QueryParameterKind
{
    /// <summary>A string. Sent as text; the server converts it where the context asks for another type.</summary>
    Text,
    /// <summary>A number as written (<c>42</c>, <c>-1.5</c>). A driver that types its parameters sends it as
    /// one, so <c>limit :n</c> and <c>top (:n)</c> work; Postgres infers it from context either way.</summary>
    Number,
    /// <summary>SQL NULL. <see cref="QueryParameter.Value"/> is null.</summary>
    Null,
}

/// <summary>
/// A value bound to a named placeholder in the SQL a run sends (#103) — never spliced into the text. The
/// placeholder spelling is the caller's (<c>Bearing.Sql.SqlVariables.Parameterize</c>); the executor only
/// binds <see cref="Name"/> to <see cref="Value"/>.
/// </summary>
/// <param name="Name">The driver-side name, without its prefix: <c>bearing_id</c> for <c>@bearing_id</c>.</param>
/// <param name="Value">The value's text, or null for <see cref="QueryParameterKind.Null"/>.</param>
/// <param name="Kind">How the driver should send it.</param>
public sealed record QueryParameter(string Name, string? Value, QueryParameterKind Kind);
