using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Schema;
using Bearing.Sessions;
using Bearing.Sql;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The verdict half of DynamoDB's full-scan question (SC13): <see cref="ScanGuard"/> joins what the text pins
/// (<see cref="PartiQlDialect"/>'s <c>ScanTargets</c>, read here through the real dialect) with the partition key
/// a session loaded. The fixture is shaped the way <c>DynamoDbMetadataReader</c> builds it: partition key ordinal
/// 1, sort key ordinal 2 (both flagged), index-only keys after, and the primary key listed among the indexes as
/// <c>PRIMARY</c>.
/// </summary>
public class ScanGuardTests
{
    private const long OrdersId = 1;
    private const long BrokenId = 2;

    /// <summary>Orders: partition key customerId, sort key orderDate; a GSI byStatus (status, orderDate), an LSI
    /// byRegion (customerId, region), and a GSI that happens to be named PRIMARY (status).</summary>
    private static readonly SchemaSnapshot Snapshot = new(
        "eu-west-1",
        [""],
        [
            new TableInfo(OrdersId, "", "Orders", RelationKind.Table),
            // Listed, but its DescribeTable failed: a table with no columns, so no key to compare against.
            new TableInfo(BrokenId, "", "Broken", RelationKind.Table),
        ],
        [
            new ColumnInfo(OrdersId, 1, "customerId", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 2, "orderDate", "S", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(OrdersId, 3, "status", "S", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(OrdersId, 4, "region", "S", NotNull: false, IsPrimaryKey: false),
        ],
        [],
        searchPath: [""]);

    private static readonly TableDetails OrdersDetails = new(
        [],
        [
            new IndexInfo(10, "PRIMARY", IsUnique: true, IsPrimary: true, IsValid: true, [1, 2], "primary key"),
            new IndexInfo(11, "byStatus", IsUnique: false, IsPrimary: false, IsValid: true, [3, 2], "gsi"),
            new IndexInfo(12, "byRegion", IsUnique: false, IsPrimary: false, IsValid: true, [1, 4], "lsi"),
            new IndexInfo(13, "PRIMARY", IsUnique: false, IsPrimary: false, IsValid: true, [3], "gsi named PRIMARY"),
        ],
        [],
        []);

    private static TableDetails? Details(TableInfo table) => table.Id == OrdersId ? OrdersDetails : null;

    private static ScanVerdict One(string sql, Func<TableInfo, long?>? itemCounts = null)
    {
        var targets = PartiQlDialect.Instance.ScanTargets(sql);
        Assert.NotNull(targets);

        return ScanGuard.Classify(Assert.Single(targets), Snapshot, Details, itemCounts);
    }

    [Theory]
    // The partition key pinned with = or IN, alone, beside other conditions, or in every OR branch.
    [InlineData("select * from \"Orders\" where customerId = 'a'", ScanVerdictKind.Keyed)]
    [InlineData("select * from \"Orders\" where \"customerId\" IN ['a', 'b']", ScanVerdictKind.Keyed)]
    [InlineData("select * from \"Orders\" where customerId = 'a' and status = 'open'", ScanVerdictKind.Keyed)]
    [InlineData("select * from \"Orders\" where customerId = 'a' or customerId = 'b'", ScanVerdictKind.Keyed)]
    [InlineData("select * from Orders where 'a' = customerId", ScanVerdictKind.Keyed)]
    // AWS's own example of a scan: the key in one OR branch only.
    [InlineData("select * from \"Orders\" where customerId = 'a' or status = 'open'", ScanVerdictKind.FullScan)]
    [InlineData("select * from \"Orders\"", ScanVerdictKind.FullScan)]
    [InlineData("select * from \"Orders\" where customerId > 'a'", ScanVerdictKind.FullScan)]
    // The sort key alone is not a key condition.
    [InlineData("select * from \"Orders\" where orderDate = '2026-01-01'", ScanVerdictKind.FullScan)]
    // Attribute names are case-sensitive: CustomerId is some other attribute.
    [InlineData("select * from \"Orders\" where CustomerId = 'a'", ScanVerdictKind.FullScan)]
    // An index is read through its own partition key, not the table's.
    [InlineData("select * from \"Orders\".\"byStatus\" where status = 'open'", ScanVerdictKind.Keyed)]
    [InlineData("select * from \"Orders\".\"byStatus\" where customerId = 'a'", ScanVerdictKind.FullScan)]
    // A local secondary index shares the table's partition key.
    [InlineData("select * from \"Orders\".\"byRegion\" where customerId = 'a'", ScanVerdictKind.Keyed)]
    [InlineData("select * from \"Orders\".\"byRegion\" where region = 'eu'", ScanVerdictKind.FullScan)]
    // A secondary index named PRIMARY is that index, never the table's primary key.
    [InlineData("select * from \"Orders\".\"PRIMARY\" where customerId = 'a'", ScanVerdictKind.FullScan)]
    [InlineData("select * from \"Orders\".\"PRIMARY\" where status = 'open'", ScanVerdictKind.Keyed)]
    public void A_select_is_keyed_only_when_its_targets_partition_key_is_pinned(string sql, ScanVerdictKind expected)
    {
        var verdict = One(sql);

        Assert.Equal(expected, verdict.Kind);
        Assert.Null(verdict.Uncertainty);
        Assert.NotNull(verdict.PartitionKey);
    }

    [Theory]
    [InlineData("select * from \"Missing\" where id = 1", ScanUncertainty.UnknownTable)]
    // Table names are case-sensitive too: orders is not Orders, so it is not looked up as Orders.
    [InlineData("select * from orders where customerId = 'a'", ScanUncertainty.UnknownTable)]
    [InlineData("select * from \"Orders\".\"nope\" where customerId = 'a'", ScanUncertainty.UnknownIndex)]
    [InlineData("select * from \"Broken\" where id = 1", ScanUncertainty.KeysNotLoaded)]
    // A subquery is beyond the lexical reduction.
    [InlineData("select * from \"Orders\" where customerId = (select 1 from x)", ScanUncertainty.Unreadable)]
    public void What_cannot_be_checked_is_undetermined_never_a_scan(string sql, ScanUncertainty why)
    {
        var verdict = One(sql);

        Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
        Assert.Equal(why, verdict.Uncertainty);
        Assert.Null(verdict.PartitionKey);
    }

    [Fact]
    public void No_table_list_is_undetermined()
    {
        var target = Assert.Single(PartiQlDialect.Instance.ScanTargets("select * from \"Orders\"")!);

        var verdict = ScanGuard.Classify(target, snapshot: null, Details);

        Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
        Assert.Equal(ScanUncertainty.TablesNotLoaded, verdict.Uncertainty);
    }

    [Fact]
    public void An_index_whose_details_did_not_arrive_is_undetermined()
    {
        // The snapshot alone holds no index keys; when the caller's budget ran out before the details landed —
        // or it never asked — the verdict must not fall back to the table's key.
        var target = Assert.Single(
            PartiQlDialect.Instance.ScanTargets("select * from \"Orders\".\"byStatus\" where customerId = 'a'")!);

        Assert.Equal(ScanUncertainty.KeysNotLoaded, ScanGuard.Classify(target, Snapshot, _ => null).Uncertainty);
        Assert.Equal(ScanUncertainty.KeysNotLoaded, ScanGuard.Classify(target, Snapshot).Uncertainty);
        Assert.Equal(
            ScanUncertainty.KeysNotLoaded,
            ScanGuard.Classify(target, Snapshot, _ => TableDetails.Empty).Uncertainty);
    }

    [Fact]
    public void A_target_the_dialect_could_not_read_is_undetermined_even_with_its_table_named()
    {
        var target = new ScanTarget("select …", "Orders", null, ["customerId"], Readable: false);

        var verdict = ScanGuard.Classify(target, Snapshot, Details);

        Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
        Assert.Equal(ScanUncertainty.Unreadable, verdict.Uncertainty);
    }

    [Fact]
    public void A_batch_gets_one_verdict_per_select_in_order()
    {
        var targets = PartiQlDialect.Instance.ScanTargets(
            "select * from \"Orders\" where customerId = 'a';\nselect * from \"Orders\";\nselect * from \"Missing\"")!;

        var verdicts = ScanGuard.Classify(targets, Snapshot, Details);

        Assert.Equal(
            new[] { ScanVerdictKind.Keyed, ScanVerdictKind.FullScan, ScanVerdictKind.Undetermined },
            verdicts.Select(v => v.Kind));
    }

    // ---- which statements are classified (TargetsFor) ----------------------------------------------------

    /// <summary>ARCH1's fail-closed padding: a read the write guard counts but the reduction skipped becomes one
    /// unreadable target, so the batch is "could not check" — never run unclassified. The keyed read beside it
    /// keeps its own verdict.</summary>
    [Fact]
    public void A_read_the_reduction_skipped_is_padded_as_unreadable()
    {
        const string sql = "select * from \"Orders\" where customerId = 'a'; select * from \"Orders\"";
        Assert.Single(SkippingPartiQlDialect.Instance.ScanTargets(sql)!);

        var targets = ScanGuard.TargetsFor(SkippingPartiQlDialect.Instance, sql)!;

        Assert.Equal([true, false], targets.Select(t => t.Readable));
        Assert.Equal(
            [ScanVerdictKind.Keyed, ScanVerdictKind.Undetermined],
            ScanGuard.Classify(targets, Snapshot, Details).Select(v => v.Kind));
    }

    [Fact]
    public void A_batch_the_reduction_followed_whole_is_not_padded()
    {
        var targets = ScanGuard.TargetsFor(
            PartiQlDialect.Instance,
            "select * from \"Orders\" where customerId = 'a'; delete from \"Orders\" where customerId = 'a'")!;

        Assert.True(Assert.Single(targets).Readable);
    }

    [Fact]
    public void An_unreadable_batch_is_not_padded_twice()
    {
        var targets = ScanGuard.TargetsFor(PartiQlDialect.Instance, "select * from \"Orders\"; $$ select 1 $$")!;

        Assert.Single(targets, t => !t.Readable);
    }

    [Fact]
    public void An_engine_with_no_scan_question_has_no_targets()
        => Assert.Null(ScanGuard.TargetsFor(PostgresDialect.Instance, "select * from orders"));

    [Fact]
    public void The_item_count_is_carried_when_the_caller_has_one()
    {
        Assert.Equal(1234567, One("select * from \"Orders\"", _ => 1234567).ApproximateItemCount);
        Assert.Null(One("select * from \"Orders\"").ApproximateItemCount);
    }

    // ---- sentences ----------------------------------------------------------------------------------------

    [Fact]
    public void A_scan_names_the_table_its_partition_key_and_the_item_count()
    {
        var text = ScanGuard.Describe(One("select * from \"Orders\" where status = 'open'", _ => 1234567));

        Assert.Equal(
            "This SELECT would read the whole of \"Orders\" — the WHERE has no = or IN on its partition key "
            + "\"customerId\". DynamoDB last reported about 1,234,567 items in \"Orders\".",
            text);
    }

    /// <summary>SF7: DynamoDB refreshes its item count about every six hours, so 0 is "not reported", never
    /// "about 0 items" — a table filled since would be named as empty.</summary>
    [Fact]
    public void An_item_count_of_zero_is_not_reported_as_a_fact()
    {
        var verdict = One("select * from \"Orders\"", _ => 0);

        Assert.Null(verdict.ApproximateItemCount);
        Assert.DoesNotContain("about", ScanGuard.Describe(verdict)!);
    }

    [Fact]
    public void A_scan_without_a_known_item_count_does_not_invent_one()
    {
        var text = ScanGuard.Describe(One("select * from \"Orders\""))!;

        Assert.DoesNotContain("about", text);
        Assert.DoesNotContain("items", text);
    }

    [Fact]
    public void An_index_scan_names_the_index_the_table_and_the_indexs_key()
    {
        var text = ScanGuard.Describe(One("select * from \"Orders\".\"byStatus\" where customerId = 'a'"))!;

        Assert.Contains("index \"byStatus\" of \"Orders\"", text);
        Assert.Contains("\"status\"", text);
    }

    [Fact]
    public void The_confirmation_says_how_rows_load_and_what_the_check_is_not()
    {
        var text = ScanGuard.ConfirmationText([One("select * from \"Orders\"")])!;

        Assert.Contains("a page at a time", text);
        Assert.Contains("stops mistakes", text);
        Assert.Contains("not cost control", text);
        Assert.Contains("not a guarantee", text);
        Assert.Contains("IAM policy", text);
        Assert.Contains("dynamodb:FullTableScan", text);
    }

    [Fact]
    public void A_keyed_batch_has_nothing_to_confirm_or_refuse()
    {
        var keyed = One("select * from \"Orders\" where customerId = 'a'");

        Assert.Null(ScanGuard.Describe(keyed));
        Assert.Null(ScanGuard.ConfirmationText([keyed]));
        Assert.Null(ScanGuard.CliRefusal("prod", keyed));
    }

    [Fact]
    public void The_confirmation_lists_only_the_statements_that_are_not_keyed()
    {
        var text = ScanGuard.ConfirmationText(
            ScanGuard.Classify(
                PartiQlDialect.Instance.ScanTargets(
                    "select * from \"Orders\" where customerId = 'a'; select * from \"Missing\"")!,
                Snapshot,
                Details))!;

        Assert.Contains("\"Missing\"", text);
        Assert.DoesNotContain("would read the whole", text);
    }

    [Theory]
    [InlineData("select * from \"Missing\" where id = 1", "\"Missing\"")]
    [InlineData("select * from \"Orders\".\"nope\" where customerId = 'a'", "\"nope\"")]
    [InlineData("select * from \"Broken\" where id = 1", "\"Broken\"")]
    [InlineData("select * from \"Orders\" where customerId = (select 1 from x)", "could not be followed")]
    public void An_undetermined_verdict_says_bearing_could_not_check_and_never_that_it_scans(string sql, string named)
    {
        var verdict = One(sql);

        foreach (var text in new[] { ScanGuard.Describe(verdict)!, ScanGuard.CliRefusal("prod", verdict)! })
        {
            Assert.Contains("could not check", text);
            Assert.Contains(named, text);
            Assert.DoesNotContain("would read the whole", text);
            Assert.DoesNotContain("full scan", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_cli_refuses_a_scan_in_the_specs_words()
    {
        var refusal = ScanGuard.CliRefusal("prod", One("select * from \"Orders\" where status = 'open'"));

        Assert.Equal(
            "'prod': this SELECT would read the whole of \"Orders\" — the WHERE has no = or IN on its partition "
            + "key \"customerId\". The bearing command runs keyed reads only; add a partition-key condition, or "
            + "run it in Bearing.",
            refusal);
    }

    [Fact]
    public void The_cli_refusal_for_an_index_scan_addresses_the_index_as_the_statement_does()
    {
        var refusal = ScanGuard.CliRefusal("prod", One("select * from \"Orders\".\"byStatus\""))!;

        Assert.Contains("the whole of \"Orders\".\"byStatus\"", refusal);
        Assert.Contains("partition key \"status\"", refusal);
    }

    [Fact]
    public void The_cli_refusal_for_an_unchecked_select_names_the_connection_and_runs_nothing()
    {
        var refusal = ScanGuard.CliRefusal("prod", One("select * from \"Missing\""))!;

        Assert.StartsWith("'prod': Bearing could not check", refusal);
        Assert.Contains("keyed reads only", refusal);
    }

    // ---- an unfiltered scan's first page ----------------------------------------------------------

    /// <summary>A scan with no WHERE at all returns every item it evaluates, so its first page reads about one
    /// page of items: it is still a scan (and still described as one), but the App does not ask before it.</summary>
    [Theory]
    [InlineData("select * from \"Orders\"")]
    [InlineData("select customerId, status from \"Orders\" order by customerId")]
    [InlineData("select * from \"Orders\".\"byStatus\"")]
    public void A_scan_with_no_where_is_unfiltered_and_runs_unasked(string sql)
    {
        var verdict = One(sql);

        Assert.Equal(ScanVerdictKind.FullScan, verdict.Kind);
        Assert.True(verdict.Unfiltered);
        Assert.False(ScanGuard.AsksBeforeRun(verdict));
        Assert.Contains("would read the whole of", ScanGuard.Describe(verdict));
    }

    /// <summary>A WHERE that does not pin the key may evaluate many requests' worth of items to fill one page, so
    /// it still asks.</summary>
    [Theory]
    [InlineData("select * from \"Orders\" where status = 'open'")]
    [InlineData("select * from \"Orders\" where customerId > 'a'")]
    [InlineData("select * from \"Orders\".\"byStatus\" where customerId = 'a'")]
    public void A_scan_with_a_where_is_filtered_and_asks(string sql)
    {
        var verdict = One(sql);

        Assert.Equal(ScanVerdictKind.FullScan, verdict.Kind);
        Assert.False(verdict.Unfiltered);
        Assert.True(ScanGuard.AsksBeforeRun(verdict));
    }

    /// <summary>Only a FullScan verdict is ever unfiltered: a missing fact still asks, even with no WHERE.</summary>
    [Fact]
    public void An_undetermined_read_with_no_where_is_not_unfiltered_and_asks()
    {
        var unknown = One("select * from \"Missing\"");
        var broken = One("select * from \"Broken\"");
        var noTables = ScanGuard.Classify(
            Assert.Single(PartiQlDialect.Instance.ScanTargets("select * from \"Orders\"")!), snapshot: null);
        var unreadable = ScanGuard.Classify(
            new ScanTarget("select * from \"Orders\"", "Orders", null, [], Readable: false) { HasWhere = false },
            Snapshot,
            Details);

        foreach (var verdict in new[] { unknown, broken, noTables, unreadable })
        {
            Assert.Equal(ScanVerdictKind.Undetermined, verdict.Kind);
            Assert.False(verdict.Unfiltered);
            Assert.True(ScanGuard.AsksBeforeRun(verdict));
        }
    }

    [Fact]
    public void A_keyed_read_is_never_unfiltered_and_never_asks()
    {
        var keyed = One("select * from \"Orders\" where customerId = 'a'");

        Assert.False(keyed.Unfiltered);
        Assert.False(ScanGuard.AsksBeforeRun(keyed));
    }

    /// <summary>The bearing command is unchanged: an unfiltered scan is refused in the same words as any scan.</summary>
    [Fact]
    public void The_cli_still_refuses_an_unfiltered_scan()
    {
        var refusal = ScanGuard.CliRefusal("prod", One("select * from \"Orders\""));

        Assert.Equal(
            "'prod': this SELECT would read the whole of \"Orders\" — the WHERE has no = or IN on its partition "
            + "key \"customerId\". The bearing command runs keyed reads only; add a partition-key condition, or "
            + "run it in Bearing.",
            refusal);
    }

    [Fact]
    public void Callers_share_a_four_second_budget()
        => Assert.Equal(TimeSpan.FromSeconds(4), ScanGuard.Budget);

    [Fact]
    public void A_table_is_looked_up_by_its_exact_name()
    {
        Assert.Equal(OrdersId, ScanGuard.TableOf(new ScanTarget("", "Orders", null, [], true), Snapshot)?.Id);
        Assert.Null(ScanGuard.TableOf(new ScanTarget("", "orders", null, [], true), Snapshot));
        Assert.Null(ScanGuard.TableOf(new ScanTarget("", null, null, [], false), Snapshot));
        Assert.Null(ScanGuard.TableOf(new ScanTarget("", "Orders", null, [], true), null));
    }
}

/// <summary>
/// PartiQL in every respect but one: <see cref="ScanTargets"/> drops the last <c>SELECT</c>'s target, the way a
/// reduction that failed to follow a statement would. The shipping dialect's split and the guard's agree, so this
/// is the only way to reach the fail-closed padding in <see cref="ScanGuard.TargetsFor"/> — and it has to stay
/// reached: it is what keeps a skipped read from running unasked (ARCH1).
/// </summary>
internal sealed class SkippingPartiQlDialect : ISqlDialect
{
    private static readonly ISqlDialect P = PartiQlDialect.Instance;

    public static readonly ISqlDialect Instance = new SkippingPartiQlDialect();

    public IReadOnlyList<ScanTarget>? ScanTargets(string sql)
        => P.ScanTargets(sql) is { Count: > 0 } targets ? targets.Take(targets.Count - 1).ToList() : P.ScanTargets(sql);

    public string Id => P.Id;
    public bool HasDialectAwareGuard => P.HasDialectAwareGuard;
    public IReadOnlySet<string> RiskyVerbs => P.RiskyVerbs;
    public IReadOnlySet<string> ExternalReadVerbs => P.ExternalReadVerbs;
    public bool SupportsExplainPlan => P.SupportsExplainPlan;
    public IReadOnlySet<string> ExternalDeniedFunctions => P.ExternalDeniedFunctions;
    public IReadOnlySet<string> ExternalDeniedRelations => P.ExternalDeniedRelations;
    public (IReadOnlySet<string> Called, IReadOnlySet<string> Mentioned) ExternalNameScan(string statement)
        => P.ExternalNameScan(statement);
    public IReadOnlyList<StatementRisk> DescribeStatements(string sql) => P.DescribeStatements(sql);
    public IReadOnlyList<StatementSpan> SplitStatements(string sql) => P.SplitStatements(sql);
    public string? TransactionControl(IReadOnlyList<string> words) => P.TransactionControl(words);
    public ISqlParseRules ParseRules => P.ParseRules;
    public bool InStringLiteral(string sql, int offset) => P.InStringLiteral(sql, offset);
    public IReadOnlyList<SqlVariableRef> FindVariables(string sql) => P.FindVariables(sql);
    public IReadOnlyList<ScriptDirective> FindDirectives(string sql) => P.FindDirectives(sql);
    public string RedactLiterals(string? sql) => P.RedactLiterals(sql);
    public string Quote(string i) => P.Quote(i);
    public string QuoteIfNeeded(string i) => P.QuoteIfNeeded(i);
    public bool NeedsQuoting(string i) => P.NeedsQuoting(i);
    public string Unquote(string i) => P.Unquote(i);
    public string? TryAppendPage(string s, int o, int l) => P.TryAppendPage(s, o, l);
    public string? Wrap(string s, int o, int l) => P.Wrap(s, o, l);
    public string? CountWrap(string s) => P.CountWrap(s);
    public string InsertStatement(string t, string c, string v, bool r) => P.InsertStatement(t, c, v, r);
    public string UpdateStatement(string t, string s, string w, IReadOnlyList<string>? r) => P.UpdateStatement(t, s, w, r);
    public string? TryEditExpression(string? text) => P.TryEditExpression(text);
    public IReadOnlyCollection<string> EditExpressions => P.EditExpressions;
    public IReadOnlyList<string> OfferedEditExpressions => P.OfferedEditExpressions;
}
