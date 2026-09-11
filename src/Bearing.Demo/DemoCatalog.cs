using System;
using System.Collections.Generic;
using System.Linq;
using Bearing.Core.Data;
using Bearing.Core.Schema;

namespace Bearing.Demo;

/// <summary>
/// A small hand-authored catalog and the result sets it can produce, so the UI can be driven against fixed
/// data with no Postgres anywhere (#63).
/// <para>
/// The point of building it at this altitude is that <see cref="ColumnDescriptor.BaseTableId"/> and
/// <see cref="ColumnDescriptor.BaseColumnOrdinal"/> are provider-assigned and provider-neutral: the Postgres
/// provider maps table OID + attnum onto them, and nothing above the provider knows that. So a fixture can
/// <i>declare</i> a column's origin and get real FK navigation, real inline editing and real PK badges out of
/// <c>ResultSetBuilder</c> and the resolvers — the features a fake at this level usually destroys.
/// </para>
/// <para>
/// Deterministic by construction: fixed ids, fixed values, fixed row order, fixed durations. No clocks and no
/// GUIDs, because captures get diffed and assertions count rows.
/// </para>
/// <para>
/// <b>Not where resolution is tested.</b> These fixtures encode our assumptions about what Postgres reports,
/// and asserting the assumptions back would give a green suite over a broken app. Whether a real foreign key,
/// primary key or editable result is recognised belongs in <c>Bearing.Data.Tests</c> against live pagila
/// (§4.2, §4.6). What belongs here is how the UI behaves <i>given</i> a result shape.
/// </para>
/// </summary>
public static class DemoCatalog
{
    public const string Database = "demo";
    public const string Schema = "shop";

    // Table ids: arbitrary but fixed. A provider assigns these; in Postgres they are catalog OIDs.
    public const long StoreId = 1001;
    public const long PaymentId = 1002;
    public const long DocumentId = 1003;
    public const long MetricId = 1004;
    public const long ReceiptViewId = 1005;

    /// <summary>A value long enough to need the cell inspector rather than the grid.</summary>
    public const string LongText =
        "Chargeback opened by the issuer on 2019-04-02 and closed without liability once the signature matched.";

    // ---- the catalog ----------------------------------------------------------------------------

    public static SchemaSnapshot Snapshot() => new(
        Database,
        schemas: [Schema, "public"],
        tables:
        [
            new TableInfo(StoreId, Schema, "store", RelationKind.Table),
            new TableInfo(PaymentId, Schema, "payment", RelationKind.Table),
            new TableInfo(DocumentId, Schema, "document", RelationKind.Table),
            new TableInfo(MetricId, Schema, "metric", RelationKind.Table),
            new TableInfo(ReceiptViewId, Schema, "receipt", RelationKind.View),
        ],
        columns:
        [
            new ColumnInfo(StoreId, 1, "id", "int4", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(StoreId, 2, "name", "text", NotNull: true, IsPrimaryKey: false),
            new ColumnInfo(StoreId, 3, "active", "bool", NotNull: false, IsPrimaryKey: false),

            new ColumnInfo(PaymentId, 1, "id", "int4", NotNull: true, IsPrimaryKey: true),
            // Nullable on purpose: a NULL in a foreign-key column is #61's exact repro.
            new ColumnInfo(PaymentId, 2, "store_id", "int4", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(PaymentId, 3, "amount", "numeric", NotNull: true, IsPrimaryKey: false),
            new ColumnInfo(PaymentId, 4, "note", "text", NotNull: false, IsPrimaryKey: false),

            new ColumnInfo(DocumentId, 1, "id", "int4", NotNull: true, IsPrimaryKey: true),
            new ColumnInfo(DocumentId, 2, "body", "jsonb", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(DocumentId, 3, "published", "bool", NotNull: true, IsPrimaryKey: false),
            new ColumnInfo(DocumentId, 4, "summary", "text", NotNull: false, IsPrimaryKey: false),

            new ColumnInfo(MetricId, 1, "id", "int4", NotNull: true, IsPrimaryKey: true),
            // A name far wider than its values, next to a value far wider than its name — the two
            // directions the initial column width has to get right (#30, #73).
            new ColumnInfo(MetricId, 2, "cumulative_gross_revenue_eur", "numeric", NotNull: true, IsPrimaryKey: false),
            new ColumnInfo(MetricId, 3, "note", "text", NotNull: false, IsPrimaryKey: false),

            // A view, so its columns have origins but no primary key among them.
            new ColumnInfo(ReceiptViewId, 1, "payment_id", "int4", NotNull: false, IsPrimaryKey: false),
            new ColumnInfo(ReceiptViewId, 2, "store_name", "text", NotNull: false, IsPrimaryKey: false),
        ],
        foreignKeys:
        [
            new ForeignKeyInfo(2001, "payment_store_id_fkey", PaymentId, [2], StoreId, [1]),
        ],
        searchPath: [Schema]);

    /// <summary>
    /// One table's constraints, indexes and triggers (#46). Declared per table, as the catalog reports them
    /// per table, so the schema tree's folders have something to hold and the counts are real.
    /// </summary>
    public static TableDetails DetailsOf(long tableId) => tableId switch
    {
        StoreId => new TableDetails(
            [
                new ConstraintInfo(4001, "store_pkey", ConstraintKind.PrimaryKey, [1], "PRIMARY KEY (id)"),
                new ConstraintInfo(4002, "store_name_key", ConstraintKind.Unique, [2], "UNIQUE (name)"),
                new ConstraintInfo(4003, "store_name_not_blank", ConstraintKind.Check, [2],
                    "CHECK ((btrim(name) <> ''::text))"),
            ],
            [
                new IndexInfo(5001, "store_pkey", IsUnique: true, IsPrimary: true, IsValid: true, [1],
                    "CREATE UNIQUE INDEX store_pkey ON shop.store USING btree (id)",
                    BackedByConstraint: true),
                new IndexInfo(5002, "store_name_key", IsUnique: true, IsPrimary: false, IsValid: true, [2],
                    "CREATE UNIQUE INDEX store_name_key ON shop.store USING btree (name)",
                    // Owned by store_name_key the constraint, so generated DDL must not re-issue it.
                    BackedByConstraint: true),
            ],
            [
                new TriggerInfo(6001, "store_audit", Enabled: true,
                    "CREATE TRIGGER store_audit AFTER INSERT OR UPDATE ON shop.store "
                    + "FOR EACH ROW EXECUTE FUNCTION shop.write_audit()"),
            ],
            // No policies on this one: a table without them must not grow an empty group (#119).
            []),

        PaymentId => new TableDetails(
            [
                new ConstraintInfo(4010, "payment_pkey", ConstraintKind.PrimaryKey, [1], "PRIMARY KEY (id)"),
                new ConstraintInfo(4011, "payment_store_id_fkey", ConstraintKind.ForeignKey, [2],
                    "FOREIGN KEY (store_id) REFERENCES shop.store(id)"),
                new ConstraintInfo(4012, "payment_amount_positive", ConstraintKind.Check, [3],
                    "CHECK ((amount > (0)::numeric))"),
            ],
            [
                new IndexInfo(5010, "payment_pkey", IsUnique: true, IsPrimary: true, IsValid: true, [1],
                    "CREATE UNIQUE INDEX payment_pkey ON shop.payment USING btree (id)",
                    BackedByConstraint: true),
                new IndexInfo(5011, "payment_store_id_idx", IsUnique: false, IsPrimary: false, IsValid: true, [2],
                    "CREATE INDEX payment_store_id_idx ON shop.payment USING btree (store_id)"),
                // An index the planner will not use: what a failed CREATE INDEX CONCURRENTLY leaves behind,
                // and the thing you are hunting when a query is slow despite "having an index".
                new IndexInfo(5012, "payment_note_idx", IsUnique: false, IsPrimary: false, IsValid: false, [4],
                    "CREATE INDEX payment_note_idx ON shop.payment USING btree (note)"),
            ],
            [],
            // The same two policies ObjectKinds() reports for this table, so the per-table folder and the
            // per-database group cannot disagree about what is on shop.payment (#119).
            [.. ObjectKinds().PoliciesOf(PaymentId)]),

        DocumentId => new TableDetails(
            [new ConstraintInfo(4020, "document_pkey", ConstraintKind.PrimaryKey, [1], "PRIMARY KEY (id)")],
            [
                new IndexInfo(5020, "document_pkey", IsUnique: true, IsPrimary: true, IsValid: true, [1],
                    "CREATE UNIQUE INDEX document_pkey ON shop.document USING btree (id)",
                    BackedByConstraint: true),
                // An expression index: no resolvable column ordinals at all, so the row has to fall back to
                // the definition to say anything useful.
                new IndexInfo(5021, "document_channel_idx", IsUnique: false, IsPrimary: false, IsValid: true, [],
                    "CREATE INDEX document_channel_idx ON shop.document USING btree (((body ->> 'channel'::text)))"),
            ],
            [
                new TriggerInfo(6020, "document_touch", Enabled: false,
                    "CREATE TRIGGER document_touch BEFORE UPDATE ON shop.document "
                    + "FOR EACH ROW EXECUTE FUNCTION shop.touch()"),
            ],
            []),

        // A view has no constraints, indexes or triggers of its own — and "none" is an answer the tree has to
        // render as no folders rather than as empty ones.
        _ => TableDetails.Empty,
    };

    /// <summary>
    /// What each relation "costs on disk" (#76). Hand-authored like everything else here, and chosen so the
    /// interesting shapes are visible: payment is mostly indexes, document is mostly toast, metric has never
    /// been analysed, and the view has no storage at all.
    /// </summary>
    public static IReadOnlyList<RelationSize> Sizes() =>
    [
        new RelationSize(StoreId, TotalBytes: 81_920, TableBytes: 40_960, IndexBytes: 40_960,
            ToastBytes: 0, EstimatedRows: 4),
        // Three indexes on forty rows: the shape that makes the total-versus-heap split worth showing.
        new RelationSize(PaymentId, TotalBytes: 1_638_400, TableBytes: 327_680, IndexBytes: 1_310_720,
            ToastBytes: 0, EstimatedRows: 40),
        new RelationSize(DocumentId, TotalBytes: 9_437_184, TableBytes: 1_048_576, IndexBytes: 32_768,
            ToastBytes: 8_388_608, EstimatedRows: 3),
        // Never analysed, so its row count is unknown rather than zero.
        new RelationSize(MetricId, TotalBytes: 24_576, TableBytes: 16_384, IndexBytes: 8_192,
            ToastBytes: 0, EstimatedRows: null),
    ];

    public static IReadOnlyList<DatabaseSize> DatabaseSizes() =>
    [
        new DatabaseSize(Database, 11_182_080),
        // A database the demo user cannot connect to: its size is unknown, not zero (#76).
        new DatabaseSize("postgres", null),
    ];

    public static IReadOnlyList<RoutineInfo> Routines() =>
    [
        new RoutineInfo(3001, Schema, "gross_revenue", RoutineKind.Function, "(from_date date)", "numeric"),
    ];

    /// <summary>
    /// The per-database object kinds (#119): one sequence owned by a column, an enum, a domain, a
    /// composite, two extensions and a policy on <c>payment</c>.
    /// <para>
    /// One of each on purpose — the tree renders a group per kind and a detail line per node, and a fixture
    /// with nothing in a kind would let that kind's rendering rot unnoticed. Fixed ids and values, like the
    /// rest of this catalog (§4.6): captures get diffed and assertions count rows.
    /// </para>
    /// </summary>
    public static DatabaseObjectKinds ObjectKinds() => new()
    {
        Sequences =
        [
            new SequenceInfo(4001, Schema, "payment_id_seq", "bigint",
                LastValue: 8, Increment: 1, MinValue: 1, MaxValue: 9223372036854775807,
                Cycles: false, OwnedBy: $"{Schema}.payment.id"),
            // Never read from, so its last value is null rather than zero — a real state, and one the tree
            // has to render as "not yet used" rather than as a number.
            new SequenceInfo(4002, Schema, "receipt_no_seq", "integer",
                LastValue: null, Increment: 10, MinValue: 1, MaxValue: 2147483647,
                Cycles: true, OwnedBy: null,
                // A comment, so the demo shows what a documented object looks like — and so the row that
                // renders one has a fixture to render.
                Comment: "reset each year by the January close job"),
        ],
        Types =
        [
            new TypeInfo(4101, Schema, "payment_state", TypeKind.Enum, "'pending', 'settled', 'refunded'"),
            new TypeInfo(4102, Schema, "positive_amount", TypeKind.Domain,
                "numeric(10,2) not null CHECK ((VALUE > (0)::numeric))"),
            new TypeInfo(4103, Schema, "address", TypeKind.Composite, "line1 text, city text, postcode text"),
        ],
        Extensions =
        [
            new ExtensionInfo(4201, "pgcrypto", "public", "1.3"),
            new ExtensionInfo(4202, "pg_stat_statements", "public", "1.10"),
        ],
        Publications = [Object(6001, "", "shop_stream", "2 tables · insert, update, delete")],
        Subscriptions = [Object(6002, "", "shop_replica", "enabled · publications: shop_stream")],
        ForeignServers = [Object(6003, "", "legacy_erp", "postgres_fdw · version 15", "read-only mirror")],
        EventTriggers = [Object(6004, "", "audit_ddl", "ddl_command_end · shop.log_ddl()")],
        Collations = [Object(6005, Schema, "case_insensitive", "icu · und-u-ks-level2 · nondeterministic")],
        Casts = [Object(6006, "", "shop.positive_amount → numeric", "implicit · binary-coercible")],
        Operators = [Object(6007, Schema, "&&&", "shop.address shop.address → boolean")],
        OperatorClasses = [Object(6008, Schema, "address_btree", "btree · shop.address · default")],
        TextSearchConfigs = [Object(6009, Schema, "product_search", "parser pg_catalog.default")],
        Policies =
        [
            new PolicyInfo(4301, "payment_own_store", PaymentId, "ALL", Permissive: true,
                Roles: ["app_user"],
                Using: "(store_id = current_setting('app.store_id')::integer)",
                WithCheck: null),
            // Restrictive, and on the same table: the two kinds combine differently (OR versus AND), which
            // is the distinction the row has to carry.
            new PolicyInfo(4302, "payment_no_refunds", PaymentId, "UPDATE", Permissive: false,
                Roles: ["public"],
                Using: null,
                WithCheck: "(amount >= (0)::numeric)"),
        ],
    };

    /// <summary>
    /// The long-tail kinds, one row each (#119 follow-up). Enough to make every group render, and no more:
    /// these are the kinds the demo exists to <em>show the shape of</em>, not to exercise.
    /// </summary>
    private static SchemaObjectInfo Object(long id, string schema, string name, string detail, string? comment = null)
        => new(id, schema, name, detail, comment);

    /// <summary>Tablespaces — cluster-wide, so the server node's list rather than a database's.</summary>
    public static IReadOnlyList<SchemaObjectInfo> Tablespaces() =>
    [
        Object(7001, "", "pg_default", "the data directory"),
        Object(7002, "", "shop_archive", "/mnt/archive", "cold storage for settled payments"),
    ];

    /// <summary>
    /// The server's roles (#120): a superuser, an application login, and a group the login belongs to.
    /// <para>
    /// Three because that is the smallest set that exercises the distinctions on the row — login versus
    /// group, superuser versus not, a connection limit versus unlimited, an expiry versus none, and a
    /// membership. Fixed, like everything else here (§4.6).
    /// </para>
    /// </summary>
    public static IReadOnlyList<RoleInfo> Roles() =>
    [
        new RoleInfo(5001, "postgres", CanLogin: true, IsSuperuser: true, CanCreateDb: true,
            CanCreateRole: true, InheritsPrivileges: true, ConnectionLimit: -1, ValidUntil: null, MemberOf: []),
        new RoleInfo(5002, "shop_app", CanLogin: true, IsSuperuser: false, CanCreateDb: false,
            CanCreateRole: false, InheritsPrivileges: true, ConnectionLimit: 20,
            ValidUntil: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), MemberOf: ["shop_readers"]),
        new RoleInfo(5003, "shop_readers", CanLogin: false, IsSuperuser: false, CanCreateDb: false,
            CanCreateRole: false, InheritsPrivileges: true, ConnectionLimit: -1, ValidUntil: null, MemberOf: []),
    ];

    /// <summary>
    /// The server's sessions as the demo reports them (#101). Five client backends: two of ours (one running
    /// a slow report, one idle), a colleague's session mid-write, an idle-in-transaction one — the state worth
    /// noticing on a real server — and a backend waiting on a lock.
    /// <para>
    /// Durations are stated, not derived from a clock, which is why <see cref="BackendActivity.RunningFor"/>
    /// is a <c>TimeSpan</c>: a fixture that computed them from <c>now()</c> would report a different number on
    /// every run and every screenshot (§4.6).
    /// </para>
    /// </summary>
    public static IReadOnlyList<BackendActivity> Activity() =>
    [
        new BackendActivity(Pid: 4101, BackendStart: DemoConnectedAt, User: "shop_app", Database: Database,
            Application: "bearing", State: "active", WaitEvent: null,
            RunningFor: TimeSpan.FromSeconds(184), IsOurs: true,
            Query: "select store_id, sum(amount) from shop.payment group by store_id order by 2 desc"),
        new BackendActivity(Pid: 4102, BackendStart: DemoConnectedAt, User: "shop_app", Database: Database,
            Application: "bearing", State: "idle", WaitEvent: "Client: ClientRead",
            RunningFor: null, IsOurs: true,
            Query: "select * from shop.store"),
        new BackendActivity(Pid: 4187, BackendStart: DemoConnectedAt, User: "reporting", Database: Database,
            Application: "psql", State: "active", WaitEvent: null,
            RunningFor: TimeSpan.FromSeconds(12), IsOurs: false,
            Query: "update shop.metric set value = value + 1 where id = 3"),
        new BackendActivity(Pid: 4203, BackendStart: DemoConnectedAt, User: "shop_app", Database: Database,
            Application: "etl-nightly", State: "idle in transaction", WaitEvent: "Client: ClientRead",
            RunningFor: TimeSpan.FromMinutes(41), IsOurs: false,
            Query: "insert into shop.document (title) values ($1)"),
        new BackendActivity(Pid: 4219, BackendStart: DemoConnectedAt, User: "reporting", Database: Database,
            Application: "psql", State: "active", WaitEvent: "Lock: transactionid",
            RunningFor: TimeSpan.FromSeconds(63), IsOurs: false,
            Query: "delete from shop.metric where captured_at < now() - interval '90 days'"),
    ];

    /// <summary>When every demo backend connected. One fixed instant rather than five, because nothing in the
    /// panel reads it except row identity — and a pid is reused, which is what it is there to disambiguate.</summary>
    private static readonly DateTimeOffset DemoConnectedAt = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// What a role may do on the demo database (#120). <c>shop_app</c> can read and write payments and read
    /// stores; <c>shop_readers</c> can only read; the superuser is reported as not visible, which is the
    /// third state a grants pane has to render (and the one an empty list would misrepresent).
    /// </summary>
    public static RoleGrants GrantsOf(string roleName) => roleName switch
    {
        "shop_app" => RoleGrants.Of(
        [
            new RoleGrant($"database {Database}", ["CONNECT", "TEMPORARY"]),
            new RoleGrant($"{Schema}.payment", ["DELETE", "INSERT", "SELECT", "UPDATE"]),
            new RoleGrant($"{Schema}.store", ["SELECT"]),
        ]),
        "shop_readers" => RoleGrants.Of(
        [
            new RoleGrant($"database {Database}", ["CONNECT"]),
            new RoleGrant($"{Schema}.store", ["SELECT"]),
        ]),
        "postgres" => RoleGrants.NotVisible,
        _ => RoleGrants.Of([]),
    };

    // ---- result sets ----------------------------------------------------------------------------

    /// <summary>
    /// Payments: an editable result with a primary key whose <c>store_id</c> is a nullable foreign key.
    /// Drives FK navigation, the PK badge, inline editing, and NULL-in-a-foreign-key rendering (#61).
    /// </summary>
    public static QueryResult Payments(int rows = 8)
    {
        var data = new List<object?[]>(rows);
        for (var i = 1; i <= rows; i++)
            data.Add([
                i,
                // Every third payment is unattributed: the NULL has to render dimmed and italic even though
                // the column is a foreign key.
                i % 3 == 0 ? null : (i % 4) + 1,
                decimal.Divide(1250 * i, 100),
                i % 5 == 0 ? null : $"card ****{4000 + i}",
            ]);

        return Grid(data, PaymentId,
            ("id", "int4", typeof(int), 1),
            ("store_id", "int4", typeof(int), 2),
            ("amount", "numeric", typeof(decimal), 3),
            ("note", "text", typeof(string), 4));
    }

    /// <summary>Stores: the referenced side, so FK navigation has somewhere to land.</summary>
    public static QueryResult Stores() => Grid(
        [
            [1, "Vukovar", true],
            [2, "Osijek", true],
            [3, "Zadar", false],
            [4, "Split", null],
        ],
        StoreId,
        ("id", "int4", typeof(int), 1),
        ("name", "text", typeof(string), 2),
        ("active", "bool", typeof(bool), 3));

    /// <summary>A set whose <c>jsonb</c>, <c>bool</c> and over-long text each render differently from a plain
    /// string — the inspect affordance and the type-based cell treatments.</summary>
    public static QueryResult Documents() => Grid(
        [
            [1, "{\"channel\": \"web\", \"retries\": 0}", true, LongText],
            [2, "{\"channel\": \"till\", \"retries\": 2}", false, "Settled."],
            [3, null, true, null],
        ],
        DocumentId,
        ("id", "int4", typeof(int), 1),
        ("body", "jsonb", typeof(string), 2),
        ("published", "bool", typeof(bool), 3),
        ("summary", "text", typeof(string), 4));

    /// <summary>A long column name over narrow values beside a short one over wide values — the width
    /// arithmetic in both directions (#30, #73).</summary>
    public static QueryResult Metrics() => Grid(
        [
            [1, 8m, "q1"],
            [2, 110122m, "a note rather wider than its column name"],
            [3, 4m, "q3"],
        ],
        MetricId,
        ("id", "int4", typeof(int), 1),
        ("cumulative_gross_revenue_eur", "numeric", typeof(decimal), 2),
        ("note", "text", typeof(string), 3));

    /// <summary>A result off a view: real column origins, but no primary key among them.</summary>
    public static QueryResult ReceiptView() => Grid(
        [
            [1, "Vukovar"],
            [2, "Osijek"],
        ],
        ReceiptViewId,
        ("payment_id", "int4", typeof(int), 1),
        ("store_name", "text", typeof(string), 2));

    /// <summary>Computed columns with no catalog origin — an aggregate, so there is nothing to edit and
    /// nowhere to navigate.</summary>
    public static QueryResult Aggregate() => new(
        [
            new ColumnDescriptor("store_id", "int4", typeof(int)),
            new ColumnDescriptor("payments", "int8", typeof(long)),
            new ColumnDescriptor("total", "numeric", typeof(decimal)),
        ],
        [
            [1, 3L, 78.75m],
            [2, 2L, 43.75m],
            [4, 2L, 56.25m],
        ],
        3, Duration, null, null, false);

    /// <summary>A failed statement. Renders as an error rather than a grid.</summary>
    public static QueryResult Failure() => new(
        [], [], 0, Duration, null,
        new QueryError("relation \"shop.paymnet\" does not exist", "42P01", 15), false);

    /// <summary>A rows-affected message. Renders as a line of text rather than a grid.</summary>
    public static QueryResult Affected(int rows = 3) => new(
        [], [], rows, Duration, $"UPDATE {rows}", null, false);

    /// <summary>An empty grid: columns but no rows, which is not the same thing as a message.</summary>
    public static QueryResult NoRows() => Grid([], StoreId,
        ("id", "int4", typeof(int), 1),
        ("name", "text", typeof(string), 2),
        ("active", "bool", typeof(bool), 3));

    /// <summary>
    /// A whole run: sets of very different sizes plus the two that are not grids at all. What the stacked and
    /// tabbed result views have to lay out.
    /// </summary>
    public static IReadOnlyList<QueryResult> Run() =>
        [Stores(), Payments(40), Affected(), Documents(), Failure()];

    // ---- building ------------------------------------------------------------------------------

    /// <summary>A fixed duration, so a capture of a meta row is identical run to run.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(12);

    /// <summary>
    /// A grid result whose every column declares its origin in <paramref name="tableId"/> — which is what
    /// makes the resolvers treat it as the real thing.
    /// </summary>
    private static QueryResult Grid(
        IReadOnlyList<object?[]> rows,
        long tableId,
        params (string Name, string DataType, Type Clr, int Ordinal)[] columns)
        => new(
            columns.Select(c => new ColumnDescriptor(c.Name, c.DataType, c.Clr, tableId, c.Ordinal)).ToList(),
            rows, rows.Count, Duration, null, null, false);
}
