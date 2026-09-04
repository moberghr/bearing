using System.Linq;

namespace Bearing.Sql;

/// <summary>
/// The Microsoft SQL Server (T-SQL) dialect: text generation, the paging rules, and the write guard.
/// <para>
/// <b>Everything here lexes with <see cref="TSqlScanner"/>, never the vendored PostgreSQL grammar.</b>
/// That is not a preference. The PG lexer has no delimited-identifier concept, so it emitted the words
/// inside <c>[Order Details]</c> as ordinary tokens — and a table with that name read as a query carrying
/// a top-level <c>ORDER BY</c>, which made the pager append <c>OFFSET/FETCH</c> to a statement that had
/// none. SQL Server rejected it, so the user's first page died on a syntax error they never typed. Any
/// rule that asks a <em>positive</em> question of a token stream ("is this a write?", "may I append a
/// clause?") is wrong in a way that reaches the server when the lexer cannot read the dialect.
/// </para>
/// <para>
/// The editor's statement boundaries came off the PG lexer too (<see cref="SplitStatements"/>): the
/// statement-at-caret Run executes, the highlight margin, folding and completion's statement scope all
/// ask this dialect now, so a <c>GO</c>-separated batch runs one batch at a time instead of the whole
/// buffer. Completion's own parse is the last piece and is no longer an exception either — see
/// <see cref="ParseRules"/>, which hands the one <see cref="CompletionEngine"/> the vendored T-SQL
/// grammar rather than the PostgreSQL one.
/// </para>
/// </summary>
public sealed class SqlServerDialect : ISqlDialect
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static SqlServerDialect Instance { get; } = new();

    /// <summary>Matches <c>SqlServerProvider.ProviderId</c>, which lives in <c>Bearing.Data</c> and so
    /// cannot be referenced from here (§2.2).</summary>
    public string Id => "sqlserver";

    public string Quote(string identifier) => SqlServerIdentifier.Quote(identifier);
    public string QuoteIfNeeded(string identifier) => SqlServerIdentifier.QuoteIfNeeded(identifier);
    public bool NeedsQuoting(string identifier) => SqlServerIdentifier.NeedsQuoting(identifier);
    public string Unquote(string identifier) => SqlServerIdentifier.Unquote(identifier);

    // Same two leaders as Postgres: only a plain row-returning read can take a page suffix.
    private static readonly HashSet<string> RowReturningStarts =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH" };

    // A top-level occurrence of any of these means OFFSET/FETCH must NOT be appended:
    //   OFFSET/FETCH — the query already pages itself (a second clause is a syntax error);
    //   TOP          — T-SQL forbids TOP and OFFSET/FETCH in the same query expression;
    //   FOR          — FOR XML / FOR JSON / FOR UPDATE must follow the page clause, not precede it;
    //   INTO         — SELECT … INTO creates a table; limiting it would still create it (with N rows);
    //   OPTION       — the query-hint clause is last, so a suffix after it is invalid.
    private static readonly HashSet<string> BlockingKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "OFFSET", "FETCH", "TOP", "FOR", "INTO", "OPTION" };

    // The subset of the above that already makes an ORDER BY legal inside a derived table, so the
    // OFFSET 0 ROWS repair below must not be applied on top of it.
    private static readonly HashSet<string> AlreadyLegalInsideDerived =
        new(StringComparer.OrdinalIgnoreCase) { "OFFSET", "FETCH", "TOP", "FOR" };

    /// <summary>
    /// A server-side row limit, in whichever of T-SQL's two forms the statement can take:
    /// <list type="bullet">
    ///   <item><b><c>OFFSET … ROWS FETCH NEXT … ROWS ONLY</c></b> when the statement carries a top-level
    ///     <c>ORDER BY</c>. This is the only form that can express an <em>offset</em>, so it is the only
    ///     one that can page.</item>
    ///   <item><b><c>TOP (n)</c></b> for the first page of a statement with no <c>ORDER BY</c> — which
    ///     OFFSET/FETCH is illegal without. Without this, the commonest query of all
    ///     (<c>select * from BigTable</c>) got no server-side limit at all: the server computed and
    ///     streamed the entire result set for the client to read a page of and discard, and the drain is
    ///     not even reflected in the reported query time. Postgres' <c>LIMIT</c> has no such
    ///     restriction, so this is the clause that closes the gap between the two engines.</item>
    /// </list>
    /// <c>TOP</c> cannot express an offset, so page 2 onwards still needs the ORDER BY (or the caller's
    /// <see cref="Wrap"/>). An order is never <em>synthesised</em>: an arbitrary one makes paging silently
    /// non-deterministic, and the same row can then appear on two pages or on none.
    /// </summary>
    public string? TryAppendPage(string sql, int offset, int limit)
    {
        if (string.IsNullOrWhiteSpace(sql) || limit <= 0 || offset < 0) return null;

        // A batch would bind the clause to the wrong statement — only a lone statement qualifies. Split
        // with the T-SQL scanner, so a `GO` or a semicolon inside a [delimited name] is read correctly.
        var statements = TSqlScanner.Split(sql);
        if (statements.Count != 1) return null;
        var tokens = statements[0].Tokens;

        // Never reshape a write. The guard reads T-SQL for real now, so its verdict can be trusted here.
        if (WriteGuard.HasRisk(this, sql)) return null;

        var words = TSqlScanner.TopLevelWords(tokens);
        if (words.Count == 0 || !RowReturningStarts.Contains(words[0])) return null;
        if (words.Any(BlockingKeywords.Contains)) return null;

        if (HasOrderBy(words))
            // Both halves are always written: unlike Postgres' bare `limit`, T-SQL's FETCH is only legal
            // after an OFFSET, so there is no shorter first-page form.
            return $"{StripTrailingSemicolon(sql)}\noffset {offset} rows fetch next {limit} rows only";

        // No ORDER BY. TOP can still cap the *first* page, and only the first: it has no offset.
        if (offset != 0) return null;
        if (TSqlScanner.TopInsertionPoint(tokens) is not { } at) return null;   // not a bare leading SELECT
        return TSqlScanner.Insert(StripTrailingSemicolon(sql), at, $"top ({limit})");
    }

    /// <summary>True when <c>ORDER</c> and <c>BY</c> sit adjacent at the top level — the clause, not a
    /// column that happens to be called <c>order</c>. The pair matters because the OFFSET/FETCH rule is a
    /// positive gate: a false positive emits SQL the server rejects.</summary>
    private static bool HasOrderBy(IReadOnlyList<string> topLevelWords)
    {
        for (var i = 0; i < topLevelWords.Count - 1; i++)
            if (topLevelWords[i] == "ORDER" && topLevelWords[i + 1] == "BY") return true;
        return false;
    }

    /// <summary>
    /// Fallback paging. Two T-SQL rules collide here: the outer query needs an <c>ORDER BY</c> before
    /// OFFSET/FETCH is legal, and the derived table may not carry one of its own unless it also has
    /// TOP/OFFSET/FOR XML. So the outer query orders by the constant <c>(select null)</c> — the
    /// documented way to say "no order, but syntactically ordered" — and the inner query, if it brought
    /// an ORDER BY, is repaired by <see cref="MakeInnerOrderByLegal"/>.
    /// <para>
    /// The trade-off, stated plainly: <c>order by (select null)</c> means the page boundaries are only
    /// as stable as the plan. That is already true of the Postgres wrap (a derived table's order is not
    /// preserved by contract), and it is why <see cref="TryAppendPage"/> is tried first. The
    /// alternative — refusing to page at all — leaves the user unable to scroll a result they can
    /// already see, which is worse than a caveat the engine never promised away in the first place.
    /// </para>
    /// </summary>
    public string? Wrap(string sql, int offset, int limit)
    {
        if (HoistableCte(sql) is { } cte)
            return $"{cte.With}\nselect * from (\n{MakeInnerOrderByLegal(cte.Body)}\n) as _sq"
                   + $" order by (select null) offset {offset} rows fetch next {limit} rows only";

        return CannotSitInDerivedTable(sql)
            ? null
            : $"select * from (\n{MakeInnerOrderByLegal(StripTrailingSemicolon(sql))}\n) as _sq"
              + $" order by (select null) offset {offset} rows fetch next {limit} rows only";
    }

    /// <summary>Total rows of an arbitrary query, with the same inner-ORDER BY repair as
    /// <see cref="Wrap"/>.</summary>
    /// <summary>Split and classify a batch with <see cref="TSqlWriteGuard"/> — T-SQL's own lexical
    /// rules, so a delimited name, a keyword inside a string literal and an @-prefixed variable
    /// cannot trip the guard, while a GO-separated batch splits correctly.</summary>
    /// <summary>Answered from <see cref="TSqlLiterals"/> over the T-SQL scanner. Both were measurably
    /// wrong while the PostgreSQL lexer answered them on this dialect — see that type for what each
    /// cost.</summary>
    public bool InStringLiteral(string sql, int offset) => TSqlLiterals.Contains(sql, offset);

    /// <inheritdoc cref="InStringLiteral"/>
    public string RedactLiterals(string? sql) => TSqlLiterals.RedactLiterals(sql);

    public IReadOnlyList<StatementRisk> DescribeStatements(string sql)
        => TSqlWriteGuard.Describe(sql, RiskyVerbs);

    /// <summary>Split with <see cref="TSqlScanner"/> as well, so the editor sees the same statements the
    /// guard does: <c>GO</c> ends a batch, and a <c>;</c> inside <c>[a;b]</c> or a string does not. Run
    /// executes what this returns, which is why it cannot be left on the PostgreSQL lexer.</summary>
    public IReadOnlyList<StatementSpan> SplitStatements(string sql)
        => StatementSplitter.SplitWithTSqlScanner(sql);

    /// <summary>
    /// The vendored T-SQL grammar, through <see cref="TSqlParseRules"/> — which is what closed the last
    /// two places this dialect was still being read as Postgres. A bracketed source
    /// (<c>from [Order Details] o</c>) now resolves, because <c>SQUARE_BRACKET_ID</c> is a token the
    /// grammar has and the FROM scan can see; and a suggestion is quoted by <see cref="QuoteIfNeeded"/>
    /// above rather than Postgres', so a PascalCase name — T-SQL's own convention — comes back bare
    /// instead of as <c>"Customers"</c>.
    /// </summary>
    public ISqlParseRules ParseRules => TSqlParseRules.Instance;

    public string? CountWrap(string sql)
    {
        if (HoistableCte(sql) is { } cte)
            return $"{cte.With}\nselect count(*) from (\n{MakeInnerOrderByLegal(cte.Body)}\n) as _sq";

        return CannotSitInDerivedTable(sql)
            ? null
            : $"select count(*) from (\n{MakeInnerOrderByLegal(StripTrailingSemicolon(sql))}\n) as _sq";
    }

    /// <summary>
    /// A CTE statement whose preamble can be lifted clear of the derived table, with the body already
    /// stripped of its trailing semicolon — or null, which puts the statement back on the ordinary
    /// refusal path.
    /// <para>
    /// A CTE cannot sit inside <c>from (…)</c> (Msg 156), but it can stay exactly where it is while the
    /// <em>body</em> goes in the derived table, because a statement-level CTE is in scope throughout the
    /// statement, subqueries included. Before this, a CTE query ran unbounded and retired paging: the
    /// user got the whole result set streamed once and no way to scroll it. The boundary comes from the
    /// grammar (<see cref="TSqlCteSplitter"/>) rather than a keyword scan, and anything it will not vouch
    /// for is refused — a mis-cut here is a wrong answer, not a failed query.
    /// </para>
    /// <para>
    /// The body still has to be a legal derived table in its own right: <c>OPTION</c> and
    /// <c>FOR JSON</c>/<c>XML</c> are as illegal after the hoist as before it, so they are re-checked
    /// against the body rather than assumed away with the <c>WITH</c>.
    /// </para>
    /// </summary>
    private static TSqlCteSplit? HoistableCte(string sql)
    {
        if (TSqlCteSplitter.Split(sql) is not { } split) return null;
        var body = StripTrailingSemicolon(split.Body);
        return CannotSitInDerivedTable(body) ? null : split with { Body = body };
    }

    // A derived table is a query *expression*, and T-SQL admits less there than at statement level. Each
    // of these parses fine on its own and is a syntax error the moment it is wrapped:
    //   WITH   — a CTE must lead a statement; inside `from (…)` it is Msg 156;
    //   OPTION — the query-hint clause is statement-level only;
    //   FOR    — FOR JSON / FOR XML produce a stream, not a table, and FOR UPDATE is a cursor clause.
    // Postgres accepts all three in a subquery, which is why this refusal is the T-SQL dialect's alone.
    // Refusing beats emitting: the caller retires paging and says so, where before the first page
    // succeeded and then load-more died on a server error while [Count] silently showed no total.
    //
    // WITH is the one of the three that has a repair rather than only a refusal — the CTEs are hoisted
    // over the derived table instead (see HoistableCte), so this set is reached for a CTE only when the
    // grammar declines to locate the boundary. OPTION and FOR have no such move: they are clauses of the
    // query being wrapped, not a preamble that can step outside it.
    private static readonly HashSet<string> CteStarts =
        new(StringComparer.OrdinalIgnoreCase) { "WITH" };

    private static readonly HashSet<string> IllegalInsideDerived =
        new(StringComparer.OrdinalIgnoreCase) { "OPTION", "FOR" };

    private static bool CannotSitInDerivedTable(string sql)
    {
        var words = TopLevelWordsOf(sql);
        return words.Count > 0
            && (CteStarts.Contains(words[0]) || words.Any(IllegalInsideDerived.Contains));
    }

    /// <summary>
    /// T-SQL rejects <c>ORDER BY</c> in a derived table unless the subquery also has TOP, OFFSET or
    /// FOR XML (Msg 1033). Appending <c>offset 0 rows</c> supplies the missing OFFSET without dropping a
    /// row, which is the documented fix; it is skipped when the query already carries one of the clauses
    /// that legalise the order, since a second OFFSET — or anything at all after FOR XML — is itself a
    /// syntax error.
    /// <para>
    /// Reasoned from the documented rule rather than observed here, and worth knowing which: no box that
    /// has built this branch has a SQL Server on it, so the dialect tests assert the text and nothing has
    /// run it. <c>SqlServerExecutorTests.Paging_a_cte_hoists_the_with_list_and_the_server_accepts_it</c>
    /// and <c>Count_wraps_a_query_that_carries_its_own_order_by</c> are the tests that would settle it;
    /// they skip without a server (§4.2), so they are a standing offer rather than evidence.
    /// </para>
    /// </summary>
    private static string MakeInnerOrderByLegal(string sql)
    {
        var words = TopLevelWordsOf(sql);
        if (!HasOrderBy(words) || words.Any(AlreadyLegalInsideDerived.Contains)) return sql;
        return sql + "\noffset 0 rows";
    }

    /// <summary>The statement's top-level bare words, read with the T-SQL scanner so a delimited name
    /// cannot masquerade as a clause keyword.</summary>
    private static IReadOnlyList<string> TopLevelWordsOf(string sql)
        => TSqlScanner.TopLevelWords(TSqlScanner.Tokenize(sql));

    /// <summary>
    /// OUTPUT sits between the column list and VALUES; a trailing OUTPUT is a syntax error, which is why
    /// <see cref="ISqlDialect.InsertStatement"/> is a whole-statement hook rather than a trailing clause.
    /// <para>
    /// <paramref name="withReturning"/> false is not a nicety: SQL Server rejects <c>OUTPUT</c> with no
    /// <c>INTO</c> target outright when the target table has an enabled trigger (Msg 334), which is an
    /// ordinary shape for an audited table. The generator emits both forms so the executor can fall back
    /// to this one rather than leaving a grid insert impossible on such a table.
    /// </para>
    /// </summary>
    public string InsertStatement(string qualifiedTable, string columnList, string valueList, bool withReturning)
        => withReturning
            ? $"insert into {qualifiedTable} ({columnList}) output inserted.* values ({valueList})"
            : $"insert into {qualifiedTable} ({columnList}) values ({valueList})";

    /// <summary>
    /// True, and the security-relevant part of this class. It says the guard can read a T-SQL batch for
    /// itself: <see cref="DescribeStatements"/> splits and classifies with <see cref="TSqlWriteGuard"/>
    /// over the T-SQL scanner, so a <c>SELECT</c> is reported as a read rather than as "I could not tell".
    /// <para>
    /// It was false while the only lexer here was Postgres', and the cost was concrete: a guarded SQL
    /// Server connection confirmed on <em>every</em> run, reads included, which is the fastest way to
    /// teach someone to click through the guard. Flipping it back is a §1.2 decision, not a tidy-up — it
    /// may only be true for an engine whose statements the guard's own lexer can split and classify.
    /// </para>
    /// </summary>
    public bool HasDialectAwareGuard => true;

    /// <summary>
    /// The Postgres set plus T-SQL's own write verbs. This is the list the confirmation actually runs on
    /// now that <see cref="HasDialectAwareGuard"/> is true — a verb missing from here is a write that
    /// reaches a guarded server unprompted — and it is also what <see cref="TryAppendPage"/> reads to
    /// refuse reshaping a write. Superset, never subset: the guard is not allowed to be narrower for any
    /// dialect (§1.2).
    /// </summary>
    public IReadOnlySet<string> RiskyVerbs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Data writes.
        "INSERT", "UPDATE", "DELETE", "MERGE", "COPY",
        // Schema / object DDL.
        "CREATE", "DROP", "TRUNCATE", "ALTER", "REFRESH",
        // Privilege changes.
        "GRANT", "REVOKE", "DENY",
        // Procedural blocks that can write arbitrarily.
        "CALL", "DO",
        // T-SQL: EXEC/EXECUTE runs anything at all, BULK INSERT loads a file into a table, and
        // BACKUP/RESTORE are server-level operations nobody should reach past a prompt by accident.
        "EXEC", "EXECUTE", "BULK", "BACKUP", "RESTORE",
    };

    private static string StripTrailingSemicolon(string sql)
    {
        var s = sql.TrimEnd();
        return s.EndsWith(';') ? s[..^1] : s;
    }
}
