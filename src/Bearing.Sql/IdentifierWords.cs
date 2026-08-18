namespace Bearing.Sql;

/// <summary>
/// Splits an identifier into the words a human reads in it, across every casing convention a catalog
/// throws at us: <c>film_actor</c> → film, actor; <c>MigrationHistory</c> → Migration, History;
/// <c>__EFMigrationsHistory</c> → EF, Migrations, History (an acronym run gives up its tail letter to
/// the word that follows). Leading/trailing separators contribute nothing; digits stay attached to
/// the word they trail (<c>pg_stat64</c> → pg, stat64).
/// <para>Pure — the alias generator uses it for initials, and it keeps that behavior testable.</para>
/// </summary>
public static class IdentifierWords
{
    public static IReadOnlyList<string> Split(string identifier)
    {
        var words = new List<string>();
        var start = -1;

        for (var i = 0; i < identifier.Length; i++)
        {
            var ch = identifier[i];
            if (!char.IsLetterOrDigit(ch))                     // '_', '-', '.', space … end the run
            {
                Flush(i);
                continue;
            }

            if (start < 0) { start = i; continue; }

            // camelCase / PascalCase boundary, and the ACRONYMWord case where the break falls one
            // character earlier (EFMigrations → EF | Migrations).
            var prev = identifier[i - 1];
            var upperRun = char.IsUpper(ch) && char.IsUpper(prev)
                           && i + 1 < identifier.Length && char.IsLower(identifier[i + 1]);
            if ((char.IsUpper(ch) && char.IsLower(prev)) || upperRun)
            {
                Flush(i);
                start = i;
            }
        }
        Flush(identifier.Length);

        return words;

        void Flush(int endExclusive)
        {
            if (start >= 0 && endExclusive > start) words.Add(identifier[start..endExclusive]);
            start = -1;
        }
    }
}
