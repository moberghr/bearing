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
            // A blank line between statements, but only in front of one we actually re-laid-out: forcing a
            // break onto a statement left verbatim would be changing layout we just declined to own.
            if (i > 0 && _plan.IsManaged(statements[i].Start.TokenIndex))
                _plan.Set(statements[i].Start.TokenIndex, Gap.Blank, 0);
        }
        return 0;
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
           || ctx.deletestmt() is not null;

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

    public override int VisitSort_clause(PostgreSQLParser.Sort_clauseContext ctx)
    {
        if (ctx.ORDER() is { } order) Break(order.Symbol, _indent);
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
        var indent = _plan.IndentAt(ctx.Start.TokenIndex) + 1;

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

    private void BreakTerminals(ParserRuleContext ctx, int indent, params int[] types)
    {
        foreach (var child in ctx.children ?? Enumerable.Empty<IParseTree>())
            if (child is ITerminalNode terminal && types.Contains(terminal.Symbol.Type))
                Break(terminal.Symbol, indent);
    }
}
