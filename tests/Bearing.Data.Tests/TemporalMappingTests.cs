using Bearing.Core.Data;
using Bearing.Data;
using Bearing.Data.Postgres;
using Bearing.Testing;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// What Npgsql actually hands back for each temporal type (#77). The display and edit logic branches on
/// <c>DateTimeKind</c>, so these mappings are load-bearing — and they are a driver's choice, not ours: a
/// major-version change that made <c>timestamptz</c> arrive as <c>Unspecified</c> would silently stop every
/// timestamp from showing its zone, and nothing else in the suite would notice.
/// </summary>
public class TemporalMappingTests
{
    [SkippableFact]
    public async Task Postgres_temporal_types_map_the_way_the_display_logic_assumes()
    {
        var provider = new ProviderRegistry().Get(PostgresProvider.ProviderId);
        await using var factory = provider.CreateConnectionFactory(PgTestServer.Info(), PgTestServer.Password);
        await PgTestServer.RequireAsync(factory);

        var results = await provider.CreateQueryExecutor(factory).ExecuteAsync("""
            select '2026-08-26 15:00:00.372958+00'::timestamptz as tstz,
                   '2026-08-26 15:00:00.372958'::timestamp      as ts,
                   '2026-08-26'::date                           as d,
                   '15:00:00'::time                             as t,
                   '15:00:00+03'::timetz                        as ttz,
                   '1 day 3 hours'::interval                    as iv
            """, new QueryOptions(), CancellationToken.None);

        var row = results[0].Rows[0];

        // timestamptz is a real instant, and Kind is what says so — the whole discriminator (#77).
        var tstz = Assert.IsType<DateTime>(row[0]);
        Assert.Equal(DateTimeKind.Utc, tstz.Kind);
        Assert.Equal(new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Utc).AddTicks(3_729_580), tstz);

        // timestamp carries no zone, and arrives saying exactly that.
        var ts = Assert.IsType<DateTime>(row[1]);
        Assert.Equal(DateTimeKind.Unspecified, ts.Kind);

        Assert.IsType<DateOnly>(row[2]);
        Assert.IsType<TimeOnly>(row[3]);

        // timetz keeps its offset because Npgsql maps it to DateTimeOffset — so the offset arm of
        // CellFormat.Display was never dead for this type, only for timestamptz.
        var ttz = Assert.IsType<DateTimeOffset>(row[4]);
        Assert.Equal(TimeSpan.FromHours(3), ttz.Offset);

        Assert.IsType<TimeSpan>(row[5]);
    }
}
