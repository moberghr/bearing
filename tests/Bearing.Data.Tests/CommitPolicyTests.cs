using System;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The pure half of manual-commit mode (#131): the two idle clocks, the chip's age label, and the sentence
/// the connection dialog shows. Beside <see cref="SessionPolicyTests"/> because it is the same kind of thing
/// — what a per-connection safety setting <i>means</i>, testable without a server (§2.5) — and deliberately
/// a separate class, because unlike those two this setting reaches no server at all.
/// </summary>
public class CommitPolicyTests
{
    private static ConnectionInfo Info(bool manualCommit) => new()
    {
        Id = Guid.NewGuid(),
        Name = "c",
        ProviderId = "postgres",
        ManualCommit = manualCommit,
    };

    [Fact]
    public void Manual_commit_is_off_unless_it_is_asked_for()
    {
        Assert.False(CommitPolicy.IsManualCommit(Info(false)));
        Assert.True(CommitPolicy.IsManualCommit(Info(true)));
    }

    /// <summary>Zero is off, not "immediately" — the reading <see cref="SessionPolicy.TimeoutSeconds"/>
    /// already takes of a clock that has run out before it started.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_threshold_of_zero_or_less_is_off(int minutes)
    {
        Assert.Null(CommitPolicy.Threshold(minutes));
        Assert.False(CommitPolicy.IsStale(TimeSpan.FromDays(1), minutes));
        Assert.False(CommitPolicy.IsAbandoned(TimeSpan.FromDays(1), minutes));
    }

    [Fact]
    public void A_threshold_fires_on_the_boundary_and_not_before()
    {
        Assert.False(CommitPolicy.IsStale(TimeSpan.FromMinutes(4.9), 5));
        Assert.True(CommitPolicy.IsStale(TimeSpan.FromMinutes(5), 5));
        Assert.True(CommitPolicy.IsAbandoned(TimeSpan.FromMinutes(20), 15));
    }

    /// <summary>The chip is a few characters wide, and "0m" reads as stopped rather than as young.</summary>
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(31, "31s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(149, "2m")]
    [InlineData(3600, "60m")]
    public void The_age_label_reads_in_seconds_below_a_minute_and_minutes_above(int seconds, string expected)
        => Assert.Equal(expected, CommitPolicy.AgeLabel(TimeSpan.FromSeconds(seconds)));

    /// <summary>A clock that has somehow gone backwards must not print a negative age at the user.</summary>
    [Fact]
    public void A_negative_age_reads_as_zero_rather_than_as_a_minus_sign()
        => Assert.Equal("0s", CommitPolicy.AgeLabel(TimeSpan.FromSeconds(-3)));

    /// <summary>Absent rather than reassuring, the stance <see cref="SessionPolicy.Advice"/> takes: a
    /// connection with the setting off has nothing to say about it.</summary>
    [Fact]
    public void There_is_no_advice_when_the_setting_is_off()
        => Assert.Equal("", CommitPolicy.Advice(manualCommit: false));

    [Fact]
    public void The_advice_says_what_opens_a_transaction_and_what_does_not()
    {
        var advice = CommitPolicy.Advice(manualCommit: true);

        Assert.Contains("A write opens a transaction", advice);
        Assert.Contains("Reading does not", advice);
        Assert.Contains("holds locks", advice);
    }

    /// <summary>
    /// The honest thing to say about the two settings together, and it is slightly odd: on a read-only
    /// connection nothing ever writes, so nothing ever opens. Saying the ordinary sentence there would
    /// describe a transaction the user will never see.
    /// </summary>
    [Fact]
    public void On_a_read_only_connection_the_advice_says_nothing_will_ever_open()
    {
        var advice = CommitPolicy.Advice(manualCommit: true, readOnly: true);

        Assert.Contains("nothing will open a transaction", advice);
        Assert.DoesNotContain("A write opens a transaction", advice);
    }

    /// <summary>
    /// The cap is the pool's, and it has to leave room for ordinary work — a page fetch, a count, the schema
    /// tree — on the same database. Pinned here because the number is a judgement about
    /// <c>PostgresConnectionString.DefaultMaxPoolSize</c> and would otherwise drift silently if that moved.
    /// </summary>
    [Fact]
    public void The_pool_cap_leaves_most_of_the_pool_for_ordinary_work()
    {
        Assert.True(CommitPolicy.MaxOpenPerPool > 0);
        Assert.True(CommitPolicy.MaxOpenPerPool < Bearing.Data.Postgres.PostgresConnectionString.DefaultMaxPoolSize / 2 + 1);
    }

    /// <summary>The warning has to come before the rollback, or it is a sentence about something already
    /// over. The defaults say so on their own; the app also clamps a hand-edited settings file.</summary>
    [Fact]
    public void The_default_warning_comes_before_the_default_rollback()
        => Assert.True(CommitPolicy.DefaultIdleWarnMinutes < CommitPolicy.DefaultIdleRollbackMinutes);
}
