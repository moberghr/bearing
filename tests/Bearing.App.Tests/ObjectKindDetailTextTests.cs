using System.Collections.Generic;
using System.Linq;
using Bearing.App.ViewModels;
using Bearing.Core.Schema;
using Bearing.Demo;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// How #119's rows read. Pure formatting, so the wording is assertable without a tree — and the wording is
/// most of the feature: a sequence's row has to distinguish "not yet used" from a number, and a policy's has
/// to distinguish permissive from restrictive, because those are the two readings that change what the user
/// concludes.
/// </summary>
public class ObjectKindDetailTextTests
{
    private static SequenceInfo Seq(
        long? last = 42, long increment = 1, bool cycles = false, string? owner = null) =>
        new(1, "shop", "thing_id_seq", "bigint", last, increment, 1, 9223372036854775807, cycles, owner);

    private static PolicyInfo Policy(
        string command = "ALL",
        bool permissive = true,
        IReadOnlyList<string>? roles = null,
        string? use = "(store_id = 1)",
        string? check = null) =>
        new(1, "own_store", DemoCatalog.PaymentId, command, permissive, roles ?? ["app_user"], use, check);

    // ---- sequences ------------------------------------------------------------------------------------

    [Fact]
    public void A_sequence_leads_with_its_type_and_where_it_has_got_to()
        => Assert.Equal("bigint · at 42", RelationDetailText.Sequence(Seq()));

    [Fact]
    public void A_sequence_never_read_from_says_so_rather_than_showing_zero()
    {
        // Rendering null as 0 would claim the first id has already been handed out. The same null arrives
        // when the role may not read the sequence, which is why the wording asserts no cause (§1.1).
        Assert.Equal("bigint · not yet used", RelationDetailText.Sequence(Seq(last: null)));
    }

    [Fact]
    public void A_large_position_is_grouped_invariantly()
    {
        // "1.234.567" to one reader and "1,234,567" to another is a row about a different number.
        Assert.Contains("at 1,234,567", RelationDetailText.Sequence(Seq(last: 1_234_567)));
    }

    [Fact]
    public void A_step_of_one_is_not_worth_saying()
    {
        // Every sequence steps by 1 unless told otherwise, so saying it on every row would crowd out what
        // is actually unusual about the unusual ones.
        Assert.DoesNotContain("by 1", RelationDetailText.Sequence(Seq(increment: 1)));
        Assert.Contains("by 10", RelationDetailText.Sequence(Seq(increment: 10)));
    }

    [Fact]
    public void Cycling_and_ownership_are_both_on_the_row()
    {
        var text = RelationDetailText.Sequence(Seq(cycles: true, owner: "shop.thing.id"));
        Assert.Contains("cycles", text);
        // The route from a sequence back to the column it feeds — the question that sends you here.
        Assert.Contains("owned by shop.thing.id", text);
    }

    // ---- types ----------------------------------------------------------------------------------------

    [Fact]
    public void A_type_row_names_its_kind_then_what_it_holds()
    {
        Assert.Equal("enum · 'a', 'b'",
            RelationDetailText.UserType(new TypeInfo(1, "s", "t", TypeKind.Enum, "'a', 'b'")));
        Assert.Equal("domain · numeric CHECK ((VALUE > 0))",
            RelationDetailText.UserType(new TypeInfo(1, "s", "t", TypeKind.Domain, "numeric CHECK ((VALUE > 0))")));
        Assert.Equal("range · timestamptz",
            RelationDetailText.UserType(new TypeInfo(1, "s", "t", TypeKind.Range, "timestamptz")));
    }

    [Fact]
    public void A_type_with_nothing_to_show_is_just_its_kind()
        => Assert.Equal("composite",
            RelationDetailText.UserType(new TypeInfo(1, "s", "t", TypeKind.Composite, "")));

    [Fact]
    public void A_long_enum_is_visibly_cut_rather_than_silently_short()
    {
        // An enum can have fifty labels and a detail line is one line. Cut with an ellipsis, so it does not
        // read as the whole of a shorter type.
        var labels = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"'label{i}'"));
        var text = RelationDetailText.UserType(new TypeInfo(1, "s", "t", TypeKind.Enum, labels));

        Assert.EndsWith("…", text);
        Assert.True(text.Length < 110);
    }

    [Fact]
    public void The_ddl_wraps_the_server_s_own_rendering()
    {
        // The body is never re-derived here — format_type and pg_get_constraintdef already said it.
        Assert.Equal("CREATE TYPE shop.state AS ENUM ('a', 'b');",
            RelationDetailText.TypeDdl(new TypeInfo(1, "shop", "state", TypeKind.Enum, "'a', 'b'")));
        Assert.Equal("CREATE DOMAIN shop.amount AS numeric(10,2);",
            RelationDetailText.TypeDdl(new TypeInfo(1, "shop", "amount", TypeKind.Domain, "numeric(10,2)")));
        Assert.Equal("CREATE TYPE shop.addr AS (line1 text);",
            RelationDetailText.TypeDdl(new TypeInfo(1, "shop", "addr", TypeKind.Composite, "line1 text")));
    }

    // ---- extensions -----------------------------------------------------------------------------------

    [Fact]
    public void An_extension_row_is_its_version_and_schema()
    {
        Assert.Equal("1.3 · in public",
            RelationDetailText.Extension(new ExtensionInfo(1, "pgcrypto", "public", "1.3")));
        // An extension with no schema of its own (plpgsql) says only its version rather than "in ".
        Assert.Equal("1.0", RelationDetailText.Extension(new ExtensionInfo(1, "plpgsql", "", "1.0")));
    }

    // ---- policies -------------------------------------------------------------------------------------

    [Fact]
    public void A_policy_row_leads_with_the_command_and_the_kind()
    {
        Assert.StartsWith("SELECT · permissive", RelationDetailText.Policy(Policy(command: "SELECT")));
        Assert.StartsWith("UPDATE · restrictive",
            RelationDetailText.Policy(Policy(command: "UPDATE", permissive: false)));
    }

    [Fact]
    public void Permissive_is_named_even_though_it_is_the_default()
    {
        // The two combine in opposite directions — permissive policies are OR-ed and restrictive ones
        // AND-ed — so leaving the common case unlabelled would make the row's silence mean something.
        Assert.Contains("permissive", RelationDetailText.Policy(Policy()));
    }

    [Fact]
    public void A_policy_names_the_roles_it_applies_to()
    {
        Assert.Contains("to app_user, reporting",
            RelationDetailText.Policy(Policy(roles: ["app_user", "reporting"])));
        Assert.Contains("to public", RelationDetailText.Policy(Policy(roles: ["public"])));
    }

    [Fact]
    public void A_policy_with_no_using_clause_does_not_claim_one()
    {
        var text = RelationDetailText.Policy(Policy(use: null, check: "(amount > 0)"));
        Assert.DoesNotContain("using", text);
    }

    [Fact]
    public void The_policy_definition_shows_both_expressions_when_both_exist()
    {
        // USING decides which rows are visible and WITH CHECK which may be written, and they are frequently
        // not the same predicate — showing only one would hide half of what the policy does.
        var definition = RelationDetailText.PolicyDefinition(
            Policy(use: "(store_id = 1)", check: "(amount > 0)"));

        Assert.Contains("USING ((store_id = 1))", definition);
        Assert.Contains("WITH CHECK ((amount > 0))", definition);
        Assert.Contains("own_store", definition);
    }

    // ---- columns, rules and the long tail --------------------------------------------------------------

    private static ColumnInfo Col(string name = "id", string type = "int4", bool notNull = false, bool pk = false)
        => new(1, 1, name, type, notNull, pk);

    [Fact]
    public void A_column_with_no_extras_is_its_type_and_constraints()
    {
        Assert.Equal("int4", RelationDetailText.Column(Col(), null));
        Assert.Equal("int4 · not null · PK", RelationDetailText.Column(Col(notNull: true, pk: true), null));
    }

    [Fact]
    public void A_default_sits_between_the_type_and_the_constraints()
    {
        // Read as "what happens if I insert without this column": the type, then what supplies the value,
        // then what would reject it.
        var detail = new ColumnDetail(1, "nextval('s')", ColumnIdentity.None, null, null, null);
        Assert.Equal("int4 · default nextval('s') · not null · PK",
            RelationDetailText.Column(Col(notNull: true, pk: true), detail));
    }

    [Fact]
    public void A_generated_column_never_also_claims_a_default()
    {
        // Both arrive in the same adbin, so the reader reports one or the other — and saying a generated
        // column has a default would describe a value it can never use.
        var detail = new ColumnDetail(1, "(a * 2)", ColumnIdentity.None, "(a * 2)", null, null);
        var text = RelationDetailText.Column(Col(type: "numeric"), detail);

        Assert.Equal("numeric · generated always as (a * 2)", text);
        Assert.DoesNotContain("default", text);
    }

    [Fact]
    public void The_two_kinds_of_identity_are_distinguished()
    {
        // ALWAYS rejects an explicit value without OVERRIDING; BY DEFAULT accepts one. A row that said only
        // "identity" would leave the difference to be discovered by a failed insert.
        Assert.Contains("identity (always)",
            RelationDetailText.Column(Col(), new ColumnDetail(1, null, ColumnIdentity.Always, null, null, null)));
        Assert.Contains("identity (by default)",
            RelationDetailText.Column(Col(), new ColumnDetail(1, null, ColumnIdentity.ByDefault, null, null, null)));
    }

    [Fact]
    public void An_identity_column_outranks_a_default_on_the_row()
    {
        // An identity column has a default too (the sequence), but "identity" is the fact that changes how
        // it is written.
        var detail = new ColumnDetail(1, "nextval('s')", ColumnIdentity.Always, null, null, null);
        var text = RelationDetailText.Column(Col(), detail);

        Assert.Contains("identity (always)", text);
        Assert.DoesNotContain("nextval", text);
    }

    [Fact]
    public void A_long_default_is_visibly_cut()
    {
        var detail = new ColumnDetail(1, new string('x', 200), ColumnIdentity.None, null, null, null);
        var text = RelationDetailText.Column(Col(), detail);

        Assert.EndsWith("…", text);
        Assert.True(text.Length < 80);
    }

    [Fact]
    public void A_column_comment_comes_last()
    {
        // The type and the constraints are what the row is for; the comment is context.
        var detail = new ColumnDetail(1, null, ColumnIdentity.None, null, null, "null for an online sale");
        Assert.Equal("int4 · null for an online sale", RelationDetailText.Column(Col(), detail));
    }

    [Fact]
    public void A_rule_says_the_event_it_rewrites_and_whether_it_replaces_it()
    {
        var rule = new RuleInfo(1, "no_insert",
            "CREATE RULE no_insert AS ON INSERT TO shop.receipt DO INSTEAD NOTHING;");
        var text = RelationDetailText.Rule(rule);

        Assert.Contains("insert to shop.receipt", text);
        // DO INSTEAD means the original statement does not happen at all, which is the whole point of a rule.
        Assert.EndsWith("· instead", text);
    }

    [Fact]
    public void A_rule_that_only_adds_is_not_marked_instead()
        => Assert.DoesNotContain("instead", RelationDetailText.Rule(new RuleInfo(1, "also_log",
            "CREATE RULE also_log AS ON UPDATE TO shop.store DO INSERT INTO shop.audit VALUES (1);")));

    [Fact]
    public void A_rule_definition_it_cannot_parse_falls_back_to_the_text()
    {
        // Better a truncated definition than an empty row: the full text is on the node.
        Assert.NotEqual("", RelationDetailText.Rule(new RuleInfo(1, "odd", "something unexpected")));
    }

    [Fact]
    public void A_long_tail_row_is_its_detail_then_its_comment()
    {
        Assert.Equal("2 tables · insert",
            RelationDetailText.SchemaObject(new SchemaObjectInfo(1, "", "p", "2 tables · insert")));
        Assert.Equal("2 tables · insert · streams to staging",
            RelationDetailText.SchemaObject(
                new SchemaObjectInfo(1, "", "p", "2 tables · insert", "streams to staging")));
        // A kind with nothing but a comment still says something.
        Assert.Equal("streams to staging",
            RelationDetailText.SchemaObject(new SchemaObjectInfo(1, "", "p", "", "streams to staging")));
    }

    [Fact]
    public void A_partition_count_is_singular_where_it_should_be()
    {
        Assert.Equal("1 partition", RelationDetailText.Partitions(1));
        Assert.Equal("12 partitions", RelationDetailText.Partitions(12));
    }

    [Fact]
    public void A_comment_is_appended_rather_than_replacing_what_the_row_said()
    {
        Assert.Equal("table · documented", RelationDetailText.WithComment("table", "documented"));
        Assert.Equal("table", RelationDetailText.WithComment("table", null));
        Assert.Equal("documented", RelationDetailText.WithComment("", "documented"));
    }

    [Fact]
    public void A_policy_title_names_its_table_only_when_it_can()
    {
        var snapshot = DemoCatalog.Snapshot();

        Assert.Equal("own_store on shop.payment", RelationDetailText.PolicyTitle(Policy(), snapshot));
        // Under its own table the name is already implied, and there is no snapshot to resolve with.
        Assert.Equal("own_store", RelationDetailText.PolicyTitle(Policy(), null));
    }
}
