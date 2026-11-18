using Xunit;

namespace Bearing.Sql.Tests;

/// <summary>
/// The text half of Amazon DynamoDB support: PartiQL read with the vendored PostgreSQL lexer. The write guard
/// (risky verbs, the conservative default for any other lead word, keywords in strings and names), the split,
/// the paging and counting hooks that must answer null, and the corpus that pins the lexer's reading of
/// PartiQL's own punctuation — <c>[ ]</c>, <c>{ }</c>, <c>&lt;&lt; &gt;&gt;</c> and <c>?</c> (§1.2).
/// </summary>
public class PartiQlDialectTests
{
    private static readonly ISqlDialect Pq = PartiQlDialect.Instance;

    [Fact]
    public void Id_matches_the_provider_id() => Assert.Equal("dynamodb", Pq.Id);

    // ---- Write guard ----

    [Theory]
    [InlineData("insert into \"Music\" value {'Artist': 'Acme'}", "INSERT")]
    [InlineData("update \"Music\" set Awards = 1 where Artist = 'Acme'", "UPDATE")]
    [InlineData("DELETE FROM \"Music\" WHERE Artist = 'Acme'", "DELETE")]
    public void The_three_write_verbs_are_named_writes(string sql, string verb)
    {
        var only = Assert.Single(WriteGuard.Describe(Pq, sql));
        Assert.True(only.IsRisky);
        Assert.True(only.NamesAWrite);
        Assert.False(only.FromConservativeDefault);
        Assert.Equal(verb, only.Label);
    }

    [Theory]
    [InlineData("exists(select * from \"Music\" where Artist = 'Acme')", "EXISTS")]
    [InlineData("create table x (a int)", "CREATE")]
    [InlineData("explain select * from \"Music\"", "EXPLAIN")]
    [InlineData("with x as (select 1) select * from x", "WITH")]
    [InlineData("upsert into \"Music\" value {'a': 1}", "UPSERT")]
    [InlineData("(select * from \"Music\")", "(")]
    public void Any_other_lead_word_is_risky_by_the_conservative_default(string sql, string verb)
    {
        var only = Assert.Single(WriteGuard.Describe(Pq, sql));
        Assert.True(only.IsRisky);
        Assert.Equal(verb, only.Verb);
        Assert.True(WriteGuard.HasRisk(Pq, sql));
    }

    [Fact]
    public void An_unknown_lead_word_is_confirmed_but_not_claimed_to_write()
    {
        var only = Assert.Single(WriteGuard.Describe(Pq, "exists(select * from \"Music\")"));
        Assert.True(only.FromConservativeDefault);
        Assert.False(only.NamesAWrite);
    }

    [Theory]
    [InlineData("select * from \"Music\"")]
    [InlineData("SELECT * FROM \"Music\" WHERE Artist = 'Acme'")]
    [InlineData("-- a note\nselect 1 from \"T\"")]
    public void A_select_is_not_risky(string sql)
    {
        Assert.True(Pq.HasDialectAwareGuard);
        var only = Assert.Single(WriteGuard.Describe(Pq, sql));
        Assert.False(only.IsRisky);
        Assert.Equal("SELECT", only.Label);
    }

    [Theory]
    [InlineData("select * from \"Music\" where note = 'delete from \"Music\"; drop table x'")]
    [InlineData("select \"update\", \"delete\" from \"insert\"")]
    [InlineData("select * from \"Music\" -- delete from \"Music\"")]
    [InlineData("select * from \"Music\" /* ; update \"Music\" set a = 1 */ where a = 1")]
    [InlineData("select * from \"Music\" where a = 'it''s; delete'")]
    public void Keywords_inside_strings_quoted_names_and_comments_do_not_trip(string sql)
    {
        Assert.False(WriteGuard.HasRisk(Pq, sql));
        Assert.Single(Pq.SplitStatements(sql));
    }

    [Fact]
    public void A_batch_is_classified_statement_by_statement()
    {
        var described = WriteGuard.Describe(Pq, "select * from \"T\"; delete from \"T\" where pk = 1; select 1 from \"U\"");
        Assert.Equal(new[] { false, true, false }, described.Select(s => s.IsRisky));
        Assert.Equal("DELETE", described[1].Label);
    }

    /// <summary>
    /// PartiQL's own punctuation, from the DynamoDB statement reference: lists, tuples, bags and parameters,
    /// including the delimiters and a <c>;</c> inside strings. Each statement must come out as exactly one
    /// statement with the right verdict — a mis-split here is a write read as a read.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO \"Music\" VALUE {'Artist': 'Acme', 'SongTitle': 'PartiQL; Rocks', 'Tags': ['a', 'b']}", true)]
    [InlineData("UPDATE \"Music\" SET AwardsWon = 1 SET AwardDetail = {'Grammys': [2020, 2018]} WHERE Artist = 'Acme' AND SongTitle = 'PartiQL Rocks'", true)]
    [InlineData("UPDATE \"Music\" SET BandMembers = <<'member1', 'member2'>> WHERE Artist = 'Acme'", true)]
    [InlineData("UPDATE \"Music\" REMOVE AwardDetail.Grammys[2] WHERE Artist = 'Acme'", true)]
    [InlineData("UPDATE \"Music\" SET Tags = list_append(Tags, ['x]', '{y'])  WHERE Artist = ?", true)]
    [InlineData("DELETE FROM \"Music\" WHERE \"Artist\" = 'Acme Band' AND \"SongTitle\" = 'PartiQL Rocks'", true)]
    [InlineData("SELECT * FROM \"Music\" WHERE Artist = ? AND SongTitle = ?", false)]
    [InlineData("SELECT * FROM \"Music\" WHERE Artist=? AND SongTitle=?", false)]
    [InlineData("SELECT * FROM \"Orders\" WHERE OrderID IN [100, 300, 234]", false)]
    [InlineData("SELECT * FROM \"Music\" WHERE Tags = <<'a', '}'>>", false)]
    [InlineData("SELECT Address['city'], Items[0] FROM \"Orders\" WHERE OrderID = 1", false)]
    [InlineData("SELECT * FROM \"Music\" WHERE Detail = {'k': [1, {'n': '>>;<<'}]}", false)]
    [InlineData("SELECT * FROM \"Music\".\"ArtistIndex\" WHERE Artist = 'A' ORDER BY SongTitle DESC", false)]
    public void The_partiql_corpus_splits_and_classifies(string sql, bool risky)
    {
        Assert.Single(Pq.SplitStatements(sql));
        var only = Assert.Single(WriteGuard.Describe(Pq, sql));
        Assert.Equal(risky, only.IsRisky);
        Assert.True(only.GuardIsDialectAware);
    }

    [Fact]
    public void The_corpus_still_splits_when_joined_into_one_batch()
    {
        const string batch = """
            INSERT INTO "Music" VALUE {'Artist': 'Acme', 'Note': 'a;b'};
            SELECT * FROM "Music" WHERE Tags = <<'x;', ']'>> AND Artist = ?;
            UPDATE "Music" SET D = {'k': ['}', ';']} WHERE Artist = 'Acme';
            SELECT * FROM "Orders" WHERE OrderID IN [1, 2]
            """;
        var described = WriteGuard.Describe(Pq, batch);
        Assert.Equal(new[] { "INSERT", "SELECT", "UPDATE", "SELECT" }, described.Select(s => s.Verb));
        Assert.Equal(new[] { true, false, true, false }, described.Select(s => s.IsRisky));
        Assert.Equal(4, Pq.SplitStatements(batch).Count);
    }

    /// <summary>
    /// The counter-examples: forms the PostgreSQL lexer reads differently from DynamoDB (where a string, a
    /// comment or a statement ends). The guard does not trust its own reading of such a batch — every statement
    /// is reported unreadable, so it is confirmed — rather than flipping the whole engine to unreadable.
    /// </summary>
    [Theory]
    [InlineData("select * from \"T\" where a = $$; delete from \"T\" where pk = 1; $$")]
    [InlineData("select * from \"T\" where a = E'\\'; delete from \"T\" where pk = 1; select \\''")]
    [InlineData("select * from \"T\" where a = `x`")]
    [InlineData("select * from \"T\" where a = 'never closed; delete from \"T\"")]
    [InlineData("@set x = 1; delete from \"T\" where pk = 1\nselect * from \"T\"")]
    public void A_batch_the_lexer_would_misread_is_confirmed(string sql)
    {
        var described = WriteGuard.Describe(Pq, sql);
        Assert.NotEmpty(described);
        Assert.All(described, s => Assert.False(s.GuardIsDialectAware));
        Assert.True(WriteGuard.HasRisk(Pq, sql));
    }

    [Fact]
    public void A_write_hidden_in_a_nested_comment_is_still_confirmed()
    {
        // The lexer nests block comments, so it reads all of this as one comment and finds no statement;
        // PartiQL's comment ends at the first */, and the DELETE is a statement there.
        const string sql = "/* /* */ delete from \"T\" where pk = 1; /* */";
        var only = Assert.Single(WriteGuard.Describe(Pq, sql));
        Assert.True(only.IsRisky);
        Assert.False(only.GuardIsDialectAware);
        Assert.Equal(sql, only.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("-- nothing to run")]
    public void Nothing_to_run_describes_nothing(string sql) => Assert.Empty(WriteGuard.Describe(Pq, sql));

    [Fact]
    public void No_statement_is_transaction_control()
    {
        foreach (var s in WriteGuard.Describe(Pq, "begin; commit; rollback"))
        {
            Assert.Null(s.TransactionControl);
            Assert.True(s.IsRisky);
        }

        Assert.Null(Pq.TransactionControl(["COMMIT"]));
        Assert.False(Pq.CommitsImplicitly(["CREATE"]));
    }

    // ---- Splitting ----

    [Fact]
    public void Statements_split_on_semicolons_outside_strings_quoted_names_and_comments()
    {
        var spans = Pq.SplitStatements("select 'a;b', \"c;d\" from \"T\"; -- x;y\nselect 2 from \"U\" /* ; */;");
        Assert.Equal(2, spans.Count);
        Assert.StartsWith("select 'a;b', \"c;d\" from \"T\";", spans[0].Text);
        Assert.StartsWith("select 2 from \"U\"", spans[1].Text.TrimStart());
    }

    // ---- Paging, counting, generated text ----

    [Fact]
    public void The_paging_and_counting_hooks_answer_null()
    {
        Assert.Null(Pq.TryAppendPage("select * from \"T\"", 0, 100));
        Assert.Null(Pq.TryAppendPage("select * from \"T\"", 100, 100));
        Assert.Null(Pq.Wrap("select * from \"T\"", 0, 100));
        Assert.Null(Pq.CountWrap("select * from \"T\""));
    }

    [Fact]
    public void Select_top_rows_has_no_limit()
        => Assert.Equal("select * from \"Orders\"", Pq.SelectTopRows(Pq.Quote("Orders"), 100));

    [Theory]
    [InlineData("Orders", "\"Orders\"")]
    [InlineData("orders", "\"orders\"")]
    [InlineData("my-table.v2", "\"my-table.v2\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    public void Names_are_always_double_quoted(string name, string expected)
    {
        Assert.Equal(expected, Pq.QuoteIfNeeded(name));
        Assert.Equal(expected, Pq.Quote(name));
        Assert.Equal(name, Pq.Unquote(expected));
    }

    [Fact]
    public void Grid_editing_hooks_offer_nothing()
    {
        Assert.Null(Pq.TryEditExpression("now()"));
        Assert.Empty(Pq.EditExpressions);
        Assert.Empty(Pq.OfferedEditExpressions);
        Assert.Throws<NotSupportedException>(() => Pq.InsertStatement("\"T\"", "\"a\"", "@p0", true));
        Assert.Throws<NotSupportedException>(() => Pq.UpdateStatement("\"T\"", "\"a\" = @p0", "\"pk\" = @p1", null));
    }

    [Fact]
    public void Formatting_is_refused_and_plans_are_not_supported()
    {
        Assert.False(string.IsNullOrWhiteSpace(Pq.FormatRefusal));
        Assert.Contains("PartiQL", Pq.FormatRefusal);
        Assert.False(Pq.SupportsExplainPlan);
    }

    [Fact]
    public void Script_variables_and_directives_are_not_recognised()
    {
        Assert.Empty(Pq.FindVariables("select * from \"T\" where pk = :id"));
        Assert.Empty(Pq.FindDirectives("@set id = 1\nselect * from \"T\" where pk = :id"));
    }

    [Fact]
    public void The_row_impact_count_is_declined()
        => Assert.False(Pq.ReadsLikePostgresForCounting("delete from \"T\"", PgParsing.LexAll("delete from \"T\"").ToList()));

    [Fact]
    public void Completion_uses_the_postgres_grammar()
        => Assert.Same(PgParseRules.Instance, Pq.ParseRules);

    [Fact]
    public void String_literals_are_read_with_the_postgres_lexer()
    {
        Assert.True(Pq.InStringLiteral("select * from \"T\" where a = 'abc", 31));
        Assert.False(Pq.InStringLiteral("select * from \"T\" where a = 1", 28));
        Assert.Equal("select * from \"T\" where a = '?'", Pq.RedactLiterals("select * from \"T\" where a = 'secret'"));
    }

    // ---- External access ----

    [Fact]
    public void Only_select_is_a_read_for_an_external_host()
    {
        Assert.Equal(new[] { "SELECT" }, Pq.ExternalReadVerbs);
        Assert.Empty(Pq.ExternalDeniedFunctions);
        Assert.Empty(Pq.ExternalDeniedRelations);
        Assert.Null(ExternalSqlPolicy.Refuse(Pq, "select * from \"T\" where pk = 1"));
        Assert.NotNull(ExternalSqlPolicy.Refuse(Pq, "delete from \"T\" where pk = 1"));
        Assert.NotNull(ExternalSqlPolicy.Refuse(Pq, "exists(select * from \"T\")"));
    }
}
