using Bearing.Core.Completion;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// Completion read through <see cref="TSqlParseRules"/> — the batch that made the parse seam do
/// something. Two defects were measured on a SQL Server connection before this existed, and both are
/// pinned here with the Postgres reading kept alongside as the contrast, because "it now works" is not
/// a claim a test can make on its own:
/// <list type="bullet">
/// <item>every PascalCase name came back quoted — <c>"Customers"</c> for <c>Customers</c> — which is
/// nearly every identifier in a T-SQL catalog, and <c>"Order Details"</c> where T-SQL needs
/// <c>[Order Details]</c>, i.e. a syntax error rather than merely an eyesore;</item>
/// <item><c>select * from [Order Details] o where o.</c> produced <b>no suggestions at all</b>, and
/// <c>dbo.[Order Details]</c> resolved as a relation called <c>dbo</c>.</item>
/// </list>
/// </summary>
public class TSqlCompletionTests
{
    private static readonly SchemaSnapshot Schema = TSqlTestSchema.Build();

    private static CompletionEngine SqlServer => new(() => SqlServerDialect.Instance);
    private static CompletionEngine Postgres => new(() => PostgresDialect.Instance);

    private static string? Insertion(CompletionResult result, string display)
        => result.Suggestions.FirstOrDefault(s => s.DisplayText == display)?.ReplacementText;

    private static CompletionResult Complete(CompletionEngine engine, string sql)
        => engine.Complete(sql, sql.Length, Schema);

    // ---- The quoting fix ----------------------------------------------------------------------

    [Fact]
    public void A_pascal_case_relation_is_inserted_bare()
    {
        const string sql = "select * from ";

        Assert.Equal("Customers c", Insertion(Complete(SqlServer, sql), "Customers"));
        // The measured defect, kept as the contrast: Postgres has to quote it, because it would
        // otherwise fold the name to `customers` and find nothing.
        Assert.Equal("\"Customers\" c", Insertion(Complete(Postgres, sql), "Customers"));
    }

    [Fact]
    public void A_name_with_a_space_is_bracketed_and_not_double_quoted()
    {
        const string sql = "select * from ";

        Assert.Equal("[Order Details] od", Insertion(Complete(SqlServer, sql), "Order Details"));
        Assert.Equal("\"Order Details\" od", Insertion(Complete(Postgres, sql), "Order Details"));
    }

    [Fact]
    public void A_reserved_word_is_still_bracketed_even_though_its_case_is_fine()
        // So "needs quoting" cannot quietly collapse into "has a space": `Order` is PascalCase like
        // `Customers` and reserved like `select`.
        => Assert.Equal("[Order] o", Insertion(Complete(SqlServer, "select * from "), "Order"));

    [Fact]
    public void An_unreachable_schema_qualifies_with_t_sql_quoting_on_both_halves()
        // `sales` is in the catalog but not the default schema, so the insertion carries it — and both
        // halves go through the dialect, not just the relation.
        => Assert.Equal("sales.Regions r", Insertion(Complete(SqlServer, "select * from "), "Regions"));

    [Fact]
    public void A_column_is_inserted_bare_too()
    {
        const string sql = "select * from Customers c where c.";

        Assert.Equal("CompanyName", Insertion(Complete(SqlServer, sql), "CompanyName"));
        Assert.Equal("\"CompanyName\"", Insertion(Complete(Postgres, sql), "CompanyName"));
    }

    [Fact]
    public void An_fk_join_suggestion_brackets_only_the_name_that_needs_it()
    {
        var result = Complete(SqlServer, "select * from Orders o join ");

        Assert.Equal(
            "Customers c on c.CustomerId = o.CustomerId",
            Insertion(result, "Customers"));
        Assert.Equal(
            "[Order Details] od on od.OrderId = o.OrderId",
            Insertion(result, "Order Details"));
    }

    // ---- Bracketed sources resolve --------------------------------------------------------------

    [Fact]
    public void A_bracketed_source_resolves_its_columns()
    {
        const string sql = "select * from [Order Details] o where o.";

        var result = Complete(SqlServer, sql);

        Assert.Equal(
            new[] { "OrderId", "ProductId", "Quantity" },
            result.Suggestions.Where(s => s.Kind == SuggestionKind.Column)
                .Select(s => s.DisplayText));
        // The measured defect verbatim: read with the PostgreSQL lexer there is no
        // delimited-identifier token, the FROM scan finds no name, and the popup is empty.
        Assert.Empty(Complete(Postgres, sql).Suggestions);
    }

    [Fact]
    public void A_schema_qualified_bracketed_source_resolves()
    {
        const string sql = "select * from dbo.[Order Details] as od where od.";

        Assert.Equal(
            new[] { "OrderId", "ProductId", "Quantity" },
            Complete(SqlServer, sql).Suggestions
                .Where(s => s.Kind == SuggestionKind.Column).Select(s => s.DisplayText));
    }

    [Fact]
    public void The_from_scan_reads_the_bracketed_name_and_not_its_schema()
    {
        const string sql = "select * from dbo.[Order Details] as od";

        var tsql = FromClauseExtractor.Extract(TSqlParseRules.Instance, sql, Schema).Single();
        Assert.Equal("dbo", tsql.Schema);
        Assert.Equal("Order Details", tsql.RawName);
        Assert.Equal("od", tsql.Alias);
        Assert.Equal(TSqlTestSchema.OrderDetailsId, tsql.Resolved?.Id);

        // The measured defect, stated precisely: with Postgres' rules `dbo` *is* the table, because the
        // scan stops at the `[` it has no token for — so the source resolves to nothing.
        var pg = FromClauseExtractor.Extract(PgParseRules.Instance, sql, Schema).Single();
        Assert.Equal("dbo", pg.RawName);
        Assert.Null(pg.Resolved);
    }

    [Fact]
    public void A_bracketed_qualifier_is_read_back_unbracketed()
    {
        // The engine matches a qualifier against unquoted catalog names, so `[Order Details].` has to
        // arrive as `Order Details`. Postgres' pattern does not know the delimiter at all.
        const string sql = "select * from dbo.[Order Details] where [Order Details].";

        Assert.Equal("Order Details", TSqlParseRules.Instance.QualifierBefore(sql, sql.Length));
        Assert.Null(PgParseRules.Instance.QualifierBefore(sql, sql.Length));
    }

    // ---- The caret roles still hold on the other grammar ---------------------------------------

    [Fact]
    public void A_from_position_is_a_table_position()
        => Assert.Contains(CompletionIntent.TablePosition, SqlServer.IntentsAt("select * from ", 14));

    [Theory]
    [InlineData("select  from Orders", 7)]                      // a bare select-list item
    [InlineData("select * from Orders o where ", 29)]           // a predicate
    [InlineData("select * from Orders o order by ", 32)]        // ORDER BY
    [InlineData("select * from Orders o group by ", 32)]        // GROUP BY
    [InlineData("select sum() from Orders", 11)]                // a function argument
    [InlineData("update Orders set ", 18)]                      // an update element
    public void Every_expression_position_is_a_column_position(string sql, int caret)
        // Six carets, one intent. The last one is the reason `full_column_name` is preferred as well as
        // `expression`: with `full_column_name` dropped, `update Orders set |` reports no intent at all
        // and offers zero columns (measured) — it is the only caret here the container rule cannot reach.
        => Assert.Contains(CompletionIntent.ColumnPosition, SqlServer.IntentsAt(sql, caret));

    /// <summary>
    /// What the <c>expression</c> preferred rule actually buys, and the assertion that would notice it
    /// going: a predicate caret's popup is <b>short and column-led</b>.
    /// <para>
    /// Measured both ways. With <c>expression</c> preferred, <c>where |</c> offers 7 entries — the
    /// relation's three columns, then four keywords that can genuinely open a predicate; <c>order by |</c>
    /// offers exactly the three columns. With it removed (so <c>full_column_name</c> carries the column
    /// intent alone) the columns still come back, but behind <b>235</b> and <b>231</b> raw keyword
    /// candidates respectively, because c3 then enumerates everything that can follow instead of stopping
    /// at the containing rule. Nothing was broken in that state — it was unusable, which no intent
    /// assertion can see.
    /// </para>
    /// </summary>
    [Fact]
    public void A_predicate_popup_is_short_and_column_led()
    {
        var where = Complete(SqlServer, "select * from Orders o where ");
        // A ceiling rather than an exact count: which keywords can open a predicate is the grammar's
        // business and may shift on a regeneration, while an order of magnitude is the property at stake.
        Assert.InRange(where.Suggestions.Count, 3, 25);
        Assert.Equal(
            new[] { "OrderId", "CustomerId", "Freight" },
            where.Suggestions.Where(s => s.Kind == SuggestionKind.Column).Select(s => s.DisplayText));
        // Column-led, not merely column-containing: ranking is what makes a short list usable.
        Assert.All(where.Suggestions.Take(3), s => Assert.Equal(SuggestionKind.Column, s.Kind));

        // ORDER BY admits nothing but a column here, so this one is exact.
        var order = Complete(SqlServer, "select * from Orders o order by ");
        Assert.Equal(3, order.Suggestions.Count);
        Assert.All(order.Suggestions, s => Assert.Equal(SuggestionKind.Column, s.Kind));
    }

    [Fact]
    public void A_half_typed_alias_is_left_alone()
        // An alias is a name the user is inventing; the alias-slot check keys off the token roles, so it
        // has to keep working when the roles come from a different grammar.
        => Assert.Empty(Complete(SqlServer, "select * from Customers c").Suggestions);

    [Fact]
    public void A_cross_apply_qualifier_withholds_the_fk_join_rather_than_inserting_a_broken_one()
        // `cross` is T-SQL's only ON-less join qualifier (it has no NATURAL), and it leads both
        // CROSS JOIN and CROSS APPLY — neither of which takes the `on` an FK suggestion would attach.
        => Assert.DoesNotContain(
            SuggestionKind.Join,
            Complete(SqlServer, "select * from Orders o cross ").Suggestions.Select(s => s.Kind));

    // ---- Pinning: the roles are the T-SQL grammar's own numbers --------------------------------

    [Fact]
    public void T_sql_token_roles_are_the_grammars_own_constants()
    {
        var rules = TSqlParseRules.Instance;

        Assert.Equal(TSqlParser.DOT, rules.Dot);
        Assert.Equal(TSqlParser.AS, rules.As);
        Assert.Equal(TSqlParser.FROM, rules.From);
        Assert.Equal(TSqlParser.JOIN, rules.Join);
        Assert.Equal(TSqlParser.COMMA, rules.Comma);
        Assert.Equal(TSqlParser.ON, rules.On);
        Assert.Equal(TSqlParser.WHERE, rules.Where);
        Assert.Equal(TSqlParser.AND, rules.And);
        Assert.Equal(TSqlParser.OR, rules.Or);
        Assert.Equal(TSqlParser.NOT, rules.Not);
        Assert.Equal(TSqlParser.HAVING, rules.Having);
        Assert.Equal(TSqlParser.LR_BRACKET, rules.OpenParen);

        // T-SQL spells the lateral join CROSS/OUTER APPLY, and the keyword does not sit where LATERAL
        // does, so the role is deliberately absent. InvalidType is a type no token carries, which makes
        // every comparison against it fail and the branch drop out.
        Assert.Equal(Antlr4.Runtime.TokenConstants.InvalidType, rules.Lateral);

        Assert.True(rules.IsIdentifier(TSqlParser.ID));
        Assert.True(rules.IsIdentifier(TSqlParser.SQUARE_BRACKET_ID));
        Assert.True(rules.IsIdentifier(TSqlParser.DOUBLE_QUOTE_ID));
        Assert.True(rules.IsIdentifier(TSqlParser.TEMP_ID));
        // `id_` admits `keyword` too, but accepting one here would read `where` as the alias of the
        // relation before it and drop the WHERE clause out of the FROM scan.
        Assert.False(rules.IsIdentifier(TSqlParser.WHERE));

        Assert.Equal(
            new HashSet<int>
            {
                TSqlParser.LEFT, TSqlParser.RIGHT, TSqlParser.FULL,
                TSqlParser.INNER, TSqlParser.OUTER,
            },
            rules.JoinQualifiers.ToHashSet());
        Assert.Equal(new HashSet<int> { TSqlParser.CROSS }, rules.OnlessJoinQualifiers.ToHashSet());
    }

    [Fact]
    public void The_rule_choices_are_the_ones_the_comment_names()
    {
        var rules = TSqlParseRules.Instance;

        Assert.Equal(
            new HashSet<int>
            {
                TSqlParser.RULE_table_source,
                TSqlParser.RULE_expression,
                TSqlParser.RULE_full_column_name,
            },
            rules.PreferredRules.ToHashSet());

        Assert.Equal(CompletionIntent.TablePosition, rules.Classify(TSqlParser.RULE_table_source));
        Assert.Equal(CompletionIntent.ColumnPosition, rules.Classify(TSqlParser.RULE_expression));
        Assert.Equal(CompletionIntent.ColumnPosition, rules.Classify(TSqlParser.RULE_full_column_name));
        Assert.Equal(CompletionIntent.Keyword, rules.Classify(TSqlParser.RULE_tsql_file));

        // No function-call rule is preferred, so the intent never arises — see the class comment: it is
        // an alternative of the rule that has to be preferred for a bare column position to be found.
        Assert.Equal(CompletionIntent.Keyword, rules.Classify(TSqlParser.RULE_function_call));
    }

    [Fact]
    public void Priming_a_t_sql_parse_leaves_it_rewound_even_when_the_sql_is_half_typed()
    {
        var parsed = TSqlParseRules.Instance.Parse("select * from ");
        parsed.Tokens.Fill();

        parsed.PrimeForCompletion();   // the parse fails; that is the normal case at a caret

        Assert.Equal(0, parsed.Parser.CurrentToken.TokenIndex);
    }

    [Fact]
    public void The_t_sql_lexer_reads_a_bracketed_name_as_one_token()
    {
        // The one lexical fact everything above rests on, asserted directly so a grammar bump that lost
        // SQUARE_BRACKET_ID would fail here rather than as five confusing completion failures.
        var names = TSqlParseRules.Instance.LexAll("select * from [Order Details] o")
            .Where(t => TSqlParseRules.Instance.IsIdentifier(t.Type))
            .Select(t => t.Text)
            .ToList();

        Assert.Equal(new[] { "[Order Details]", "o" }, names);
    }
}
