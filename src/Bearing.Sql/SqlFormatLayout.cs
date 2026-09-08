using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace Bearing.Sql;

/// <summary>
/// Walks the parse tree and decides, for each token, whether a line starts in front of it and at what depth.
/// It writes nothing — the whole result is a <see cref="SqlFormatPlan"/>, which is what lets the layout
/// rules be tested separately from the rendering.
/// <para>
/// Only SELECT / INSERT / UPDATE / DELETE are claimed. Everything else — DDL, function bodies, EXPLAIN,
/// COPY — keeps every byte of its original whitespace. That is the deliberate shape of this pass: having no
/// rules for a statement must mean leaving it alone, never flattening it onto one line.
/// </para>
/// </summary>
internal sealed class SqlFormatLayout : PostgreSQLParserBaseVisitor<int>
{
    private readonly SqlFormatPlan _plan;
    private readonly IList<IToken> _tokens;
    private int _indent;

    private SqlFormatLayout(SqlFormatPlan plan, IList<IToken> tokens)
    {
        _plan = plan;
        _tokens = tokens;
    }

    public static SqlFormatPlan Build(PostgreSQLParser.RootContext root, IList<IToken> tokens)
    {
        var plan = new SqlFormatPlan(tokens.Count);
        new SqlFormatLayout(plan, tokens).Visit(root);

        // Punctuation that can never merge with what precedes it, so it is safe to pull tight. A sweep
        // rather than a rule, because commas appear in dozens of list rules and every one of them would
        // otherwise have to remember.
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!plan.IsManaged(i)) continue;
            if (tokens[i].Type is PostgreSQLLexer.COMMA or PostgreSQLLexer.SEMI)
                plan.Set(i, Gap.None, 0);
        }
        return plan;
    }

    // ---- statements ------------------------------------------------------------------------------

    public override int VisitStmtmulti(PostgreSQLParser.StmtmultiContext ctx)
    {
        var statements = ctx.stmt();
        for (var i = 0; i < statements.Length; i++)
        {
            Visit(statements[i]);

            // Everything below is for a statement we actually re-laid-out. Forcing structure onto one left
            // verbatim would be changing layout we just declined to own.
            if (!_plan.IsManaged(statements[i].Start.TokenIndex)) continue;

            // The statement's semicolon lives *outside* its context — stmtmulti is `stmt? (SEMI stmt?)*` —
            // so the managed sweep never reaches it and `select 1 ;` kept the space.
            if (SemicolonAfter(statements[i].Stop.TokenIndex) is >= 0 and var semi)
                _plan.Set(semi, Gap.None, 0);

            // A blank line between statements, opened in front of the statement's *leading comment* where
            // it has one. Put on the statement's first token instead, the gap lands under the comment and
            // glues it to the statement above — which is the one statement it does not describe.
            if (i > 0)
                _plan.Set(LeadingCommentOr(statements[i], statements[i - 1]), Gap.Blank, 0);
        }
        return 0;
    }

    /// <summary>The index of the semicolon closing a statement, or -1 when it has none (the last statement
    /// of a batch often does not).</summary>
    private int SemicolonAfter(int stopIndex)
    {
        for (var i = stopIndex + 1; i < _tokens.Count; i++)
        {
            if (_tokens[i].Type is PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline
                or PostgreSQLLexer.LineComment or PostgreSQLLexer.BlockComment) continue;
            return _tokens[i].Type == PostgreSQLLexer.SEMI ? i : -1;
        }
        return -1;
    }

    /// <summary>Where a statement really begins for the reader: its leading comment when it has one,
    /// otherwise its first token. A comment sitting between the previous semicolon and this statement
    /// introduces this one.</summary>
    private int LeadingCommentOr(ParserRuleContext statement, ParserRuleContext previous)
    {
        var start = statement.Start.TokenIndex;
        for (var i = start - 1; i > previous.Stop.TokenIndex; i--)
        {
            var type = _tokens[i].Type;
            if (type is PostgreSQLLexer.LineComment or PostgreSQLLexer.BlockComment) start = i;
            else if (type is not (PostgreSQLLexer.Whitespace or PostgreSQLLexer.Newline or PostgreSQLLexer.SEMI))
                break;
        }
        return start;
    }

    public override int VisitStmt(PostgreSQLParser.StmtContext ctx)
    {
        if (!Claimed(ctx)) return 0;   // not ours: no recursion, so nothing inside it is touched either

        for (var i = ctx.Start.TokenIndex; i <= ctx.Stop.TokenIndex; i++) _plan.Manage(i);
        _plan.Set(ctx.Start.TokenIndex, Gap.Line, _indent);
        return VisitChildren(ctx);
    }

    private static bool Claimed(PostgreSQLParser.StmtContext ctx)
        => ctx.selectstmt() is not null
           || ctx.insertstmt() is not null
           || ctx.updatestmt() is not null
           || ctx.deletestmt() is not null
           || ctx.createstmt() is not null
           || ctx.createfunctionstmt() is not null;

    // ---- SELECT ----------------------------------------------------------------------------------

    public override int VisitSimple_select_pramary(PostgreSQLParser.Simple_select_pramaryContext ctx)
    {
        if (ctx.SELECT() is { } select) Break(select.Symbol, _indent);

        // One target per line, a level in — the select list is the thing a formatter exists to make
        // scannable, so it breaks even for a single item.
        var targets = ctx.target_list_()?.target_list() ?? ctx.target_list();
        if (targets is not null)
            foreach (var target in targets.target_el())
                Break(target.Start, _indent + 1);

        if (ctx.from_clause()?.FROM() is { } from) Break(from.Symbol, _indent);
        if (ctx.group_clause()?.GROUP_P() is { } group) Break(group.Symbol, _indent);
        if (ctx.having_clause()?.HAVING() is { } having) Break(having.Symbol, _indent);
        if (ctx.window_clause()?.WINDOW() is { } window) Break(window.Symbol, _indent);

        return VisitChildren(ctx);
    }

    /// <summary>A single table stays on the FROM line; a comma list breaks, one per line.</summary>
    public override int VisitFrom_list(PostgreSQLParser.From_listContext ctx)
    {
        var refs = ctx.table_ref();
        if (refs.Length > 1)
            foreach (var table in refs)
                Break(table.Start, _indent + 1);
        return VisitChildren(ctx);
    }

    /// <summary>
    /// Each join in a chain starts a line a level in; its ON / USING stays on that line.
    /// <para>
    /// There is no "have we passed the first table yet" guard, and there must not be: the left-hand side of
    /// a chain is a <c>relation_expr</c> spliced straight into this rule's children rather than a nested
    /// <c>table_ref</c>, so a guard keyed on seeing one skipped the <b>first</b> join of every chain and
    /// broke only the second onwards. A table_ref can never begin with a join keyword, so none is needed —
    /// <paramref name="broken"/> alone keeps one break per join, reset by the table each join brings in.
    /// </para>
    /// </summary>
    public override int VisitTable_ref(PostgreSQLParser.Table_refContext ctx)
    {
        var broken = false;
        foreach (var child in ctx.children ?? Enumerable.Empty<IParseTree>())
        {
            if (child is PostgreSQLParser.Table_refContext)
            {
                broken = false;   // the join's right-hand side: the next join keyword starts a new line
                continue;
            }
            if (broken) continue;

            var start = child switch
            {
                PostgreSQLParser.Join_typeContext join => join.Start,
                ITerminalNode { Symbol.Type: PostgreSQLLexer.CROSS } t => t.Symbol,
                ITerminalNode { Symbol.Type: PostgreSQLLexer.NATURAL } t => t.Symbol,
                ITerminalNode { Symbol.Type: PostgreSQLLexer.JOIN } t => t.Symbol,
                _ => null,
            };
            if (start is null) continue;

            Break(start, _indent + 1);
            broken = true;
        }
        return VisitChildren(ctx);
    }

    public override int VisitSelect_clause(PostgreSQLParser.Select_clauseContext ctx)
    {
        BreakTerminals(ctx, _indent, PostgreSQLLexer.UNION, PostgreSQLLexer.EXCEPT);
        return VisitChildren(ctx);
    }

    public override int VisitSimple_select_intersect(PostgreSQLParser.Simple_select_intersectContext ctx)
    {
        BreakTerminals(ctx, _indent, PostgreSQLLexer.INTERSECT);
        return VisitChildren(ctx);
    }

    /// <summary>A parenthesised select: its body is a level in, and the closing paren comes back out to sit
    /// under whatever opened it.</summary>
    public override int VisitSelect_with_parens(PostgreSQLParser.Select_with_parensContext ctx)
    {
        _indent++;
        var result = VisitChildren(ctx);
        _indent--;
        if (ctx.CLOSE_PAREN() is { } close) Break(close.Symbol, _indent);
        return result;
    }

    /// <summary>
    /// The query's own ORDER BY, taken from <c>select_no_parens</c> rather than from <c>sort_clause</c>.
    /// <para>
    /// This distinction is the whole rule: <c>sort_clause</c> also appears inside an aggregate's argument
    /// list (<c>array_agg(x ORDER BY y)</c>), inside <c>WITHIN GROUP</c>, and inside a window spec
    /// (<c>OVER (PARTITION BY a ORDER BY b)</c>). None of those is a <c>select_with_parens</c>, so none of
    /// them bumps <see cref="_indent"/> — breaking on the rule itself pulled the ORDER BY of a windowed
    /// aggregate out to column zero, mid-function-call. Those sorts stay inline; only the statement's does.
    /// </para>
    /// </summary>
    public override int VisitSelect_no_parens(PostgreSQLParser.Select_no_parensContext ctx)
    {
        if (ctx.sort_clause_()?.sort_clause()?.ORDER() is { } order) Break(order.Symbol, _indent);
        return VisitChildren(ctx);
    }

    public override int VisitSelect_limit(PostgreSQLParser.Select_limitContext ctx)
    {
        Break(ctx.Start, _indent);
        return VisitChildren(ctx);
    }

    // ---- WITH ------------------------------------------------------------------------------------

    public override int VisitWith_clause(PostgreSQLParser.With_clauseContext ctx)
    {
        if (ctx.WITH() is { } with) Break(with.Symbol, _indent);

        // The first CTE rides on the WITH line; later ones start at the same level, under it.
        var ctes = ctx.cte_list()?.common_table_expr() ?? [];
        for (var i = 1; i < ctes.Length; i++) Break(ctes[i].Start, _indent);

        return VisitChildren(ctx);
    }

    public override int VisitCommon_table_expr(PostgreSQLParser.Common_table_exprContext ctx)
    {
        _indent++;
        var result = VisitChildren(ctx);
        _indent--;
        if (ctx.CLOSE_PAREN() is { } close) Break(close.Symbol, _indent);
        return result;
    }

    // ---- WHERE -----------------------------------------------------------------------------------

    public override int VisitWhere_clause(PostgreSQLParser.Where_clauseContext ctx)
    {
        if (ctx.WHERE() is { } where) Break(where.Symbol, _indent);
        if (ctx.a_expr() is { } expr) BreakConjunctions(expr, _indent + 1);
        return VisitChildren(ctx);
    }

    public override int VisitWhere_or_current_clause(PostgreSQLParser.Where_or_current_clauseContext ctx)
    {
        Break(ctx.Start, _indent);
        if (ctx.a_expr() is { } expr) BreakConjunctions(expr, _indent + 1);
        return VisitChildren(ctx);
    }

    /// <summary>
    /// Break before each AND / OR joining the clause's own top-level terms, so a long predicate reads as a
    /// list. Depth is counted over the token range rather than taken from the tree: <c>a_expr</c> is deeply
    /// left-recursive, so "is this AND a child of the outermost expression" is a far harder question of the
    /// tree than "is this AND outside every bracket" is of the tokens — and the two agree.
    /// <para>
    /// <c>BETWEEN x AND y</c> is the exception, and not a cosmetic one: its AND is part of the operator, not
    /// a conjunction, so breaking there splits one comparison across two lines and reads as though a term
    /// went missing. Each unmatched BETWEEN claims the next AND at its own depth.
    /// </para>
    /// </summary>
    private void BreakConjunctions(ParserRuleContext expr, int indent)
    {
        var depth = 0;
        var betweensAwaitingTheirAnd = 0;

        for (var i = expr.Start.TokenIndex; i <= expr.Stop.TokenIndex; i++)
        {
            switch (_tokens[i].Type)
            {
                case PostgreSQLLexer.OPEN_PAREN: depth++; break;
                case PostgreSQLLexer.CLOSE_PAREN: depth--; break;

                case PostgreSQLLexer.BETWEEN:
                    if (depth == 0) betweensAwaitingTheirAnd++;
                    break;

                case PostgreSQLLexer.AND:
                    if (depth != 0) break;
                    if (betweensAwaitingTheirAnd > 0) betweensAwaitingTheirAnd--;   // this AND belongs to it
                    else _plan.Set(i, Gap.Line, indent);
                    break;

                // OR can never be a BETWEEN's partner, so it always starts a line.
                case PostgreSQLLexer.OR:
                    if (depth == 0) _plan.Set(i, Gap.Line, indent);
                    break;
            }
        }
    }

    /// <summary>
    /// A CASE expression puts each WHEN, the ELSE and the END on their own lines. Long CASEs in a select
    /// list are among the least readable things SQL produces on one line, and unlike the clause spine this
    /// nests wherever the expression does — so the indent comes from the token's own planned level rather
    /// than from <see cref="_indent"/>, which tracks query nesting and not expression nesting.
    /// </summary>
    public override int VisitCase_expr(PostgreSQLParser.Case_exprContext ctx)
    {
        var indent = LineIndentOf(ctx.Start.TokenIndex) + 1;

        foreach (var when in ctx.when_clause_list()?.when_clause() ?? [])
            Break(when.Start, indent);
        if (ctx.case_default() is { } elseClause) Break(elseClause.Start, indent);
        if (ctx.END_P() is { } end) Break(end.Symbol, indent - 1);

        return VisitChildren(ctx);
    }

    // ---- INSERT / UPDATE / DELETE ----------------------------------------------------------------

    public override int VisitInsertstmt(PostgreSQLParser.InsertstmtContext ctx)
    {
        if (ctx.on_conflict_() is { } conflict) Break(conflict.Start, _indent);
        if (ctx.returning_clause() is { } returning) Break(returning.Start, _indent);
        return VisitChildren(ctx);
    }

    public override int VisitValues_clause(PostgreSQLParser.Values_clauseContext ctx)
    {
        if (ctx.VALUES() is { } values) Break(values.Symbol, _indent);

        // Several tuples read as a list; a single one rides on the VALUES line.
        var opens = ctx.OPEN_PAREN();
        if (opens.Length > 1)
            foreach (var open in opens)
                Break(open.Symbol, _indent + 1);

        return VisitChildren(ctx);
    }

    public override int VisitUpdatestmt(PostgreSQLParser.UpdatestmtContext ctx)
    {
        if (ctx.SET() is { } set) Break(set.Symbol, _indent);
        if (ctx.returning_clause() is { } returning) Break(returning.Start, _indent);
        return VisitChildren(ctx);
    }

    public override int VisitSet_clause_list(PostgreSQLParser.Set_clause_listContext ctx)
    {
        var clauses = ctx.set_clause();
        if (clauses.Length > 1)
            foreach (var clause in clauses)
                Break(clause.Start, _indent + 1);
        return VisitChildren(ctx);
    }

    public override int VisitDeletestmt(PostgreSQLParser.DeletestmtContext ctx)
    {
        if (ctx.using_clause() is { } usingClause) Break(usingClause.Start, _indent);
        if (ctx.returning_clause() is { } returning) Break(returning.Start, _indent);
        return VisitChildren(ctx);
    }

    // ---- DDL -------------------------------------------------------------------------------------

    /// <summary>
    /// A CREATE TABLE's element list: one column or constraint per line, closing paren back at the
    /// statement's level. The same "several read as a list, one rides along" rule the FROM list uses.
    /// </summary>
    public override int VisitCreatestmt(PostgreSQLParser.CreatestmtContext ctx)
    {
        if (ctx.opttableelementlist()?.tableelementlist()?.tableelement() is { Length: > 0 } elements)
        {
            foreach (var element in elements) Break(element.Start, _indent + 1);
            if (ctx.CLOSE_PAREN() is { } close) Break(close.Symbol, _indent);
        }
        return VisitChildren(ctx);
    }

    /// <summary>
    /// A CREATE FUNCTION / PROCEDURE's <b>envelope</b> — RETURNS and each option (AS, LANGUAGE, STABLE,
    /// SECURITY DEFINER …) on its own line, and its parameters one per line once there is more than one.
    /// <para>
    /// The body is deliberately not touched, and could not be: <c>$$ … $$</c> is a single lexer token, which
    /// is exactly what makes it survive byte for byte. It is also not necessarily SQL — <c>LANGUAGE plv8</c>
    /// is JavaScript and <c>plpython3u</c> is Python — so laying it out as SQL would corrupt it.
    /// </para>
    /// </summary>
    public override int VisitCreatefunctionstmt(PostgreSQLParser.CreatefunctionstmtContext ctx)
    {
        if (ctx.func_args_with_defaults()?.func_args_with_defaults_list()?.func_arg_with_default()
            is { Length: > 1 } args)
        {
            foreach (var arg in args) Break(arg.Start, _indent + 1);
            if (ctx.func_args_with_defaults()!.CLOSE_PAREN() is { } close) Break(close.Symbol, _indent);
        }

        if (ctx.RETURNS() is { } returns) Break(returns.Symbol, _indent);
        foreach (var option in ctx.createfunc_opt_list()?.createfunc_opt_item() ?? [])
            Break(option.Start, _indent);

        return VisitChildren(ctx);
    }

    // ---- keywords standing in for identifiers ----------------------------------------------------

    // A keyword reached through one of these rules is a column, alias, type or function name rather than a
    // keyword, so its case is the user's. See SqlFormatPlan.KeepsCase.
    public override int VisitUnreserved_keyword(PostgreSQLParser.Unreserved_keywordContext ctx) => KeepCase(ctx);
    public override int VisitCol_name_keyword(PostgreSQLParser.Col_name_keywordContext ctx) => KeepCase(ctx);
    public override int VisitType_func_name_keyword(PostgreSQLParser.Type_func_name_keywordContext ctx) => KeepCase(ctx);

    private int KeepCase(ParserRuleContext ctx)
    {
        for (var i = ctx.Start.TokenIndex; i <= ctx.Stop.TokenIndex; i++) _plan.KeepCase(i);
        return VisitChildren(ctx);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private void Break(IToken token, int indent) => _plan.Set(token.TokenIndex, Gap.Line, indent);

    /// <summary>
    /// The indent of the line a token will land on — the level of the nearest break planned at or before it.
    /// <para>
    /// Needed because <c>SqlFormatPlan.IndentAt</c> is only meaningful on a token that <i>starts</i> a line;
    /// everywhere else it reads zero. An expression like CASE nests from wherever its line begins, which may
    /// be a select-list item at one level or a WHERE at another, and reading the raw indent off its own
    /// first token reported zero and outdented the whole construct to column zero.
    /// </para>
    /// <para>
    /// Safe to read mid-walk because breaks are planned outside-in: by the time an expression is visited,
    /// the clause that put it on a line has already recorded its own.
    /// </para>
    /// </summary>
    private int LineIndentOf(int tokenIndex)
    {
        for (var i = tokenIndex; i >= 0; i--)
            if (_plan.GapBefore(i) is Gap.Line or Gap.Blank)
                return _plan.IndentAt(i);
        return _indent;
    }

    private void BreakTerminals(ParserRuleContext ctx, int indent, params int[] types)
    {
        foreach (var child in ctx.children ?? Enumerable.Empty<IParseTree>())
            if (child is ITerminalNode terminal && types.Contains(terminal.Symbol.Type))
                Break(terminal.Symbol, indent);
    }
}
