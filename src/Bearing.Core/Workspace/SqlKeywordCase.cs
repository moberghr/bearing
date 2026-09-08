namespace Bearing.Core.Workspace;

/// <summary>
/// What the SQL formatter does to keyword case.
/// <para>
/// Only ever cosmetic: Postgres folds unquoted identifiers to lower case, so <c>SELECT</c> and
/// <c>select</c> name the same thing, and a quoted identifier is a different token that the formatter never
/// touches. It is a setting because house styles genuinely differ and the cost of offering the choice is
/// one enum.
/// </para>
/// </summary>
public enum SqlKeywordCase
{
    /// <summary>The traditional SQL house style, and what most formatters produce.</summary>
    Upper,

    /// <summary>Lower case throughout — how SQL is written in this codebase's own tests and examples.</summary>
    Lower,

    /// <summary>Change layout only, and leave every keyword exactly as typed.</summary>
    Preserve,
}
