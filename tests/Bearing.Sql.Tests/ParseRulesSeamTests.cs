using Bearing.Core.Completion;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The seam that lets one <see cref="CompletionEngine"/> read more than one grammar, and the proof it
/// arrived without moving anything. Two halves:
/// <list type="bullet">
/// <item>a pinning half — <see cref="PgParseRules"/>'s token roles are the grammar's own constants, so
/// the move from inline <c>PostgreSQLParser.DOT</c> to <c>rules.Dot</c> cannot have re-chosen one;</item>
/// <item>a freezing half — completion's answer is identical whether the engine is handed no dialect or
/// the Postgres one. It was once identical for the SQL Server one too, because that dialect stood in
/// Postgres' rules while the seam was being introduced; <see cref="TSqlParseRules"/> replaced them, so
/// what is asserted here now is that the two dialects <em>diverge</em>, and
/// <see cref="TSqlCompletionTests"/> says how.</item>
/// </list>
/// The rest of this project's completion tests are the real regression net: they all run through the
/// default constructor, i.e. through the seam.
/// </summary>
public class ParseRulesSeamTests
{
    private static readonly Bearing.Core.Schema.SchemaSnapshot Schema = TestSchema.Build();

    /// <summary>Carets covering every branch in the engine that reads a token role: a table position, a
    /// column position, an alias slot, an <c>alias.</c> qualifier, a join attachment point, a schema
    /// qualifier, and a statement start.</summary>
    public static TheoryData<string, int> Carets => new()
    {
        { "select * from u", 15 },
        { "select id from users", 9 },
        { "select * from users u", 21 },
        { "select * from users u where u.", 30 },
        { "select * from users u join ", 27 },
        { "select * from users u left ", 27 },
        { "select * from users u cross ", 28 },
        { "select * from users u, ", 23 },
        { "select * from audit.", 20 },
        { "select * from users u where u.id = 1 and ", 41 },
        { "select * from public.users as u where u.", 40 },
        { "select 1;\nselect ", 17 },
    };

    // ---- Pinning: the roles are the grammar's own numbers ----

    [Fact]
    public void Postgres_token_roles_are_the_grammars_own_constants()
    {
        var rules = PgParseRules.Instance;

        Assert.Equal(PostgreSQLParser.DOT, rules.Dot);
        Assert.Equal(PostgreSQLParser.AS, rules.As);
        Assert.Equal(PostgreSQLParser.FROM, rules.From);
        Assert.Equal(PostgreSQLParser.JOIN, rules.Join);
        Assert.Equal(PostgreSQLParser.COMMA, rules.Comma);
        Assert.Equal(PostgreSQLParser.ON, rules.On);
        Assert.Equal(PostgreSQLParser.WHERE, rules.Where);
        Assert.Equal(PostgreSQLParser.AND, rules.And);
        Assert.Equal(PostgreSQLParser.OR, rules.Or);
        Assert.Equal(PostgreSQLParser.NOT, rules.Not);
        Assert.Equal(PostgreSQLParser.HAVING, rules.Having);
        Assert.Equal(PostgreSQLParser.OPEN_PAREN, rules.OpenParen);
        Assert.Equal(PostgreSQLParser.LATERAL_P, rules.Lateral);

        Assert.True(rules.IsIdentifier(PostgreSQLParser.Identifier));
        Assert.True(rules.IsIdentifier(PostgreSQLParser.QuotedIdentifier));
        Assert.False(rules.IsIdentifier(PostgreSQLParser.FROM));

        // The two join-qualifier sets are disjoint and complete: an ON-taking qualifier gets the missing
        // `join` keyword, an ON-less one withholds the suggestion entirely.
        Assert.Equal(
            new HashSet<int>
            {
                PostgreSQLParser.LEFT, PostgreSQLParser.RIGHT, PostgreSQLParser.FULL,
                PostgreSQLParser.INNER_P, PostgreSQLParser.OUTER_P,
            },
            rules.JoinQualifiers.ToHashSet());
        Assert.Equal(
            new HashSet<int> { PostgreSQLParser.CROSS, PostgreSQLParser.NATURAL },
            rules.OnlessJoinQualifiers.ToHashSet());
    }

    [Fact]
    public void Postgres_rules_forward_to_the_one_file_that_knows_the_numbers()
    {
        var rules = PgParseRules.Instance;

        Assert.Equal(PgCompletionRules.PreferredRules, rules.PreferredRules);
        Assert.Equal(PgCompletionRules.IgnoredTokens, rules.IgnoredTokens);
        Assert.Equal(CompletionIntent.TablePosition, rules.Classify(PostgreSQLParser.RULE_table_ref));
        Assert.Equal(CompletionIntent.ColumnPosition, rules.Classify(PostgreSQLParser.RULE_columnref));
        Assert.Equal(CompletionIntent.FunctionCall, rules.Classify(PostgreSQLParser.RULE_func_name));
        Assert.Equal(CompletionIntent.Keyword, rules.Classify(PostgreSQLParser.RULE_root));
    }

    [Fact]
    public void Priming_a_parse_leaves_it_rewound_even_when_the_sql_is_half_typed()
    {
        var parsed = PgParseRules.Instance.Parse("select * from ");
        parsed.Tokens.Fill();

        parsed.PrimeForCompletion();   // the parse fails; that is the normal case at a caret

        Assert.Equal(0, parsed.Parser.CurrentToken.TokenIndex);
    }

    // ---- Freezing: the seam moved no answer ----

    [Theory]
    [MemberData(nameof(Carets))]
    public void An_explicit_postgres_dialect_answers_exactly_as_no_dialect_does(string sql, int caret)
    {
        var withoutDialect = new CompletionEngine().Complete(sql, caret, Schema);
        var withDialect = new CompletionEngine(() => PostgresDialect.Instance).Complete(sql, caret, Schema);

        AssertSameAnswer(withoutDialect, withDialect);
    }

    [Fact]
    public void The_sql_server_dialect_no_longer_answers_as_postgres_does()
    {
        // Asserted on this fixture, rather than only on the T-SQL one, so the divergence is visible in
        // the file that used to freeze it: two of these relations need quoting on both engines, and the
        // engines spell that differently. What each dialect does with a real T-SQL catalog — bare
        // PascalCase, bracketed sources resolving — is TSqlCompletionTests.
        const string sql = "select * from ";

        var pg = new CompletionEngine(() => PostgresDialect.Instance).Complete(sql, sql.Length, Schema);
        var ss = new CompletionEngine(() => SqlServerDialect.Instance).Complete(sql, sql.Length, Schema);

        Assert.Equal("\"__MigrationHistory\" mh", Insertion(pg, "__MigrationHistory"));
        Assert.Equal("__MigrationHistory mh", Insertion(ss, "__MigrationHistory"));
        Assert.Equal("\"order\" o", Insertion(pg, "order"));
        Assert.Equal("[order] o", Insertion(ss, "order"));
    }

    [Fact]
    public void Each_dialect_hands_back_its_own_grammars_rules()
    {
        Assert.Same(PgParseRules.Instance, PostgresDialect.Instance.ParseRules);
        Assert.Same(TSqlParseRules.Instance, SqlServerDialect.Instance.ParseRules);
    }

    private static string? Insertion(CompletionResult result, string display)
        => result.Suggestions.First(s => s.DisplayText == display).ReplacementText;

    [Fact]
    public void The_dialect_is_asked_again_on_every_request()
    {
        // The selected tab's engine changes under a single long-lived CompletionEngine, so a dialect
        // captured once at construction would answer for whichever tab happened to be open first.
        var asked = 0;
        var engine = new CompletionEngine(() => { asked++; return PostgresDialect.Instance; });

        engine.Complete("select * from u", 15, Schema);
        engine.Complete("select * from u", 15, Schema);
        engine.IntentsAt("select * from u", 15);

        Assert.Equal(3, asked);
    }

    [Theory]
    [MemberData(nameof(Carets))]
    public void The_from_scan_reads_the_same_sources_through_the_rules_overload(string sql, int caret)
    {
        // TableRef is a plain class, so compare what the engine reads off it rather than the instances.
        static string Shape(Bearing.Core.Completion.TableRef r)
            => $"{r.Schema}|{r.RawName}|{r.Alias}|{r.ReferenceText}|{r.Resolved?.Id}";

        Assert.Equal(
            FromClauseExtractor.Extract(sql, Schema, caret).Select(Shape),
            FromClauseExtractor.Extract(PgParseRules.Instance, sql, Schema, caret).Select(Shape));
    }

    /// <summary>Every field of every suggestion, in order — the replacement text and the ranking included,
    /// since those are what the user actually gets.</summary>
    private static void AssertSameAnswer(CompletionResult expected, CompletionResult actual)
    {
        Assert.Equal(expected.ReplacementStart, actual.ReplacementStart);
        Assert.Equal(expected.ReplacementLength, actual.ReplacementLength);
        Assert.Equal(expected.Suggestions, actual.Suggestions);
    }
}
