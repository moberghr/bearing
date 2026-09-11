using System;
using System.Linq;
using Bearing.App.Formatting;
using Bearing.App.Results;
using Bearing.App.ViewModels;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What the grid does with the SQL Server types Postgres has no equivalent of. Two of the four questions
/// resolved to "nothing to do", which is the point of writing them down: a type badge or a special format
/// arm added for its own sake is a claim the rest of the app then has to keep.
/// <list type="bullet">
///   <item><b><c>uniqueidentifier</c></b> — already correct at all three sites (display, SQL literal, edit
///     round-trip) and nothing asserted it, because the display path arrives there by falling through to
///     <c>CellFormat.Display</c>'s <c>IFormattable</c> arm rather than by a <see cref="Guid"/> arm of its
///     own. Pinned below so reordering those arms cannot quietly turn a GUID into whatever
///     <c>ToString</c> the current culture gives.</item>
///   <item><b><c>hierarchyid</c> / <c>geography</c> / <c>geometry</c></b> — declined. Opaque serialized
///     CLR UDTs: the inspector would open on the same bytes one line lower, so the badge would promise
///     something it cannot deliver.</item>
///   <item><b><c>timestamp</c></b> — the one real defect. SQL Server's <c>timestamp</c> is
///     <c>rowversion</c>, an 8-byte counter, and the zone-less-timestamp badge fired on it.</item>
/// </list>
/// </summary>
public class TSqlTypeAffordanceTests
{
    private static ColumnDescriptor Col(string type, Type clr) => new("c", type, clr);

    // ---- The badge that was wrong: SQL Server's `timestamp` is rowversion ------------------------

    /// <summary>
    /// The declared name is not enough on its own, and this is the case that proves it: "timestamp" reads
    /// as a zone-less timestamp and is not one. The badge answers "can I trust this instant"; a rowversion
    /// raises no such question, so answering it is worse than silence.
    /// </summary>
    [Fact]
    public void A_sql_server_rowversion_is_not_badged_as_a_zone_less_timestamp()
    {
        // What SqlClient reports for `rowversion`: the legacy type name, and byte[] values.
        var rowversion = Col("timestamp", typeof(byte[]));

        Assert.False(ColumnKinds.IsTimestampWithoutZone(rowversion));
        // The name-only overload still says yes — deliberately unchanged, since that is the Postgres
        // reading and Postgres is the only engine that reaches it with a bare "timestamp".
        Assert.True(ColumnKinds.IsTimestampWithoutZone(rowversion.DataTypeName));
    }

    /// <summary>The Postgres verdicts the overload must not move: every zone-less timestamp column there
    /// carries <see cref="DateTime"/> values, arrays included.</summary>
    [Theory]
    [InlineData("timestamp without time zone", true)]
    [InlineData("timestamp", true)]
    [InlineData("timestamp with time zone", false)]
    [InlineData("timestamptz", false)]
    [InlineData("date", false)]
    public void A_postgres_timestamp_column_keeps_its_verdict(string type, bool zoneLess)
        => Assert.Equal(zoneLess, ColumnKinds.IsTimestampWithoutZone(Col(type, typeof(DateTime))));

    [Fact]
    public void An_array_of_timestamps_is_still_a_timestamp_column()
    {
        // Npgsql maps timestamp[] to DateTime[], so unwrapping the CLR array matters as much as unwrapping
        // the "[]" suffix does.
        Assert.True(ColumnKinds.IsTimestampWithoutZone(
            Col("timestamp without time zone[]", typeof(DateTime[]))));
        Assert.False(ColumnKinds.IsTimestampWithoutZone(Col("timestamptz[]", typeof(DateTime[]))));
    }

    [Fact]
    public void A_nullable_timestamp_column_is_still_a_timestamp_column()
        => Assert.True(ColumnKinds.IsTimestampWithoutZone(
            Col("timestamp without time zone", typeof(DateTime?))));

    /// <summary>
    /// T-SQL's own zone-less types get no badge, and that is the current behaviour rather than a decision
    /// this batch made: <c>datetime2</c> does not read like a truncated <c>timestamptz</c>, and its
    /// zone-carrying sibling is spelled <c>datetimeoffset</c> — a visibly different type name, unlike
    /// Postgres' pair. Pinned so a change here is a deliberate one.
    /// </summary>
    [Theory]
    [InlineData("datetime2")]
    [InlineData("datetime")]
    [InlineData("smalldatetime")]
    public void The_t_sql_date_types_are_not_badged(string type)
        => Assert.False(ColumnKinds.IsTimestampWithoutZone(Col(type, typeof(DateTime))));

    // ---- Declined: the opaque types are not documents --------------------------------------------

    /// <summary>
    /// The badge and the always-present <c>⤢</c> affordance mean "this value is a document that wants more
    /// room". A serialized UDT is not: the inspector renders text, so it would show the same bytes it
    /// already showed in the cell. "Unusual type" is not the test.
    /// </summary>
    [Theory]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    [InlineData("geometry")]
    [InlineData("uniqueidentifier")]
    [InlineData("sql_variant")]
    public void An_opaque_or_scalar_t_sql_type_is_not_a_document(string type)
    {
        Assert.False(ColumnKinds.IsDocument(type));
        Assert.False(ColumnKinds.IsJson(type));
    }

    [Fact]
    public void Xml_is_still_the_only_t_sql_document_type()
    {
        // The contrast the theory above needs: xml really is one, so "nothing SQL Server declares is a
        // document" is not what is being asserted.
        Assert.True(ColumnKinds.IsDocument("xml"));
    }

    // ---- Declined: uniqueidentifier already works, at all three sites ----------------------------

    private static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void A_guid_displays_in_its_canonical_form()
    {
        // Lower-case, hyphenated, unbraced — what every T-SQL tool prints and what SQL Server accepts back.
        // It reaches CellFormat's IFormattable arm rather than a Guid arm, which is why this is pinned.
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", CellFormat.Display(Id));
    }

    [Fact]
    public void A_guid_renders_as_a_quoted_sql_literal_in_both_dialects()
    {
        // T-SQL has no GUID literal syntax of its own: a quoted string casts to uniqueidentifier, which is
        // also what Postgres does for uuid — so the two agree here, unlike bool and binary.
        Assert.Equal("'0f8fad5b-d9cb-469f-a165-70867728950e'", SqlValue.Literal(SqlLiteralStyle.TSql, Id));
        Assert.Equal(SqlValue.Literal(SqlLiteralStyle.Postgres, Id), SqlValue.Literal(SqlLiteralStyle.TSql, Id));
    }

    /// <summary>
    /// The edit round-trip: the grid writes strings, so a typed GUID has to come back as a
    /// <see cref="Guid"/> and not as text the server has to cast. It also has to be recognised as
    /// <em>unchanged</em> when it is — a coerced value that never equals the typed original it came from
    /// makes every touched cell generate an assignment.
    /// </summary>
    [Fact]
    public void An_edited_guid_cell_is_written_back_as_a_guid()
    {
        var edited = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var rs = OneGuidRow(1, Id);
        var row = rs.Rows[0];

        rs.SetCell(row, 1, edited.ToString());
        var change = Assert.Single(ResultEditModel.BuildPendingChanges(rs, GuidTarget));

        Assert.Equal(ResultEditModel.ChangeKind.Update, change.Kind);
        // The parameter carries a Guid, not the string the grid held.
        Assert.Contains(edited, change.Command.Parameters.Select(p => p.Value));
    }

    [Fact]
    public void Retyping_the_same_guid_generates_no_write()
    {
        var rs = OneGuidRow(1, Id);
        var row = rs.Rows[0];

        // Same value, re-entered in the display form — nothing changed, so nothing may be written.
        rs.SetCell(row, 1, CellFormat.Display(Id));

        Assert.Empty(ResultEditModel.BuildPendingChanges(rs, GuidTarget));
    }

    private static readonly EditTarget GuidTarget = new("dbo", "Widgets",
    [
        new EditableColumn(0, "Id", IsPrimaryKey: true),
        new EditableColumn(1, "Tag", IsPrimaryKey: false),
    ]);

    /// <summary>A (Id int, Tag uniqueidentifier) result with one row, originals captured.</summary>
    private static ResultSetViewModel OneGuidRow(int id, Guid tag)
    {
        var columns = new[]
        {
            new ColumnDescriptor("Id", "int", typeof(int), 1, 1),
            new ColumnDescriptor("Tag", "uniqueidentifier", typeof(Guid), 1, 2),
        };
        var result = new QueryResult(columns, new[] { new object?[] { id, tag } }, 1, TimeSpan.Zero,
            null, null, false);
        var rs = new ResultSetViewModel(result, "select * from dbo.Widgets", pageable: true)
        {
            EditTarget = GuidTarget,
        };
        rs.CaptureOriginals();
        return rs;
    }
}
