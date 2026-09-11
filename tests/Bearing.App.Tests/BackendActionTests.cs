using System;
using Bearing.App.Services;
using Bearing.Core.Data;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// What the backend confirmation says, and what a read-only connection refuses (#101). All derived, so the
/// wording is pinned without a window — the division <c>WriteConfirmation</c> already draws with its dialog.
/// </summary>
public class BackendActionTests
{
    private static ConnectionInfo Conn(bool readOnly = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "prod-eu",
        ProviderId = "postgres",
        Environment = "Production",
        ReadOnly = readOnly,
    };

    private static BackendActivity Backend(bool ours = false, string? query = "delete from payment") => new(
        Pid: 4242,
        BackendStart: DateTimeOffset.UnixEpoch,
        User: "etl",
        Database: "warehouse",
        Application: ours ? "bearing" : "etl-nightly",
        State: "active",
        WaitEvent: null,
        RunningFor: TimeSpan.FromMinutes(3),
        Query: query,
        IsOurs: ours);

    // ---- what the confirmation says -------------------------------------------------------------

    [Fact]
    public void The_prompt_names_who_the_backend_belongs_to_not_just_its_number()
    {
        // This is the line that answers "am I about to kill the right one". A pid is a number with nothing
        // in it to recognise.
        var request = new BackendAction(BackendActionKind.Terminate, Conn(), Backend());

        Assert.Contains("4242", request.Heading);
        Assert.Contains("etl", request.Target);
        Assert.Contains("warehouse", request.Target);
        Assert.Contains("etl-nightly", request.Target);
        Assert.Equal("delete from payment", request.Query);
    }

    [Fact]
    public void A_backend_Bearing_opened_says_so()
    {
        Assert.Contains("opened by Bearing",
            new BackendAction(BackendActionKind.Cancel, Conn(), Backend(ours: true)).Target);
        Assert.DoesNotContain("opened by Bearing",
            new BackendAction(BackendActionKind.Cancel, Conn(), Backend()).Target);
    }

    [Fact]
    public void The_two_actions_do_not_describe_themselves_the_same_way()
    {
        // The whole decision is the difference between them, so the summary is where it has to be visible.
        var cancel = new BackendAction(BackendActionKind.Cancel, Conn(), Backend());
        var terminate = new BackendAction(BackendActionKind.Terminate, Conn(), Backend());

        Assert.NotEqual(cancel.Summary, terminate.Summary);
        Assert.NotEqual(cancel.ConfirmLabel, terminate.ConfirmLabel);
        Assert.Contains("session stays open", cancel.Summary);
        Assert.Contains("disconnected", terminate.Summary);
        Assert.Contains("rolled back", terminate.Summary);
    }

    [Fact]
    public void An_idle_backend_reports_no_elapsed_statement_rather_than_zero()
    {
        // §1.7's shape: an absence is typed. A session running nothing has no elapsed statement, which is not
        // a statement that has run for no time.
        var idle = Backend() with { RunningFor = null, State = "idle" };

        Assert.Null(new BackendAction(BackendActionKind.Cancel, Conn(), idle).Running);
        Assert.NotNull(new BackendAction(BackendActionKind.Cancel, Conn(), Backend()).Running);
    }

    [Fact]
    public void A_backend_the_role_could_not_read_the_query_of_shows_none()
    {
        var hidden = Backend(query: null);
        Assert.Null(new BackendAction(BackendActionKind.Cancel, Conn(), hidden).Query);
    }

    [Fact]
    public void A_server_that_declined_is_reported_as_gone_rather_than_as_a_failure()
    {
        // By far the commonest reason for a false: the backend finished on its own between the poll that
        // listed it and the click. That is the system working, not an error.
        var request = new BackendAction(BackendActionKind.Terminate, Conn(), Backend());

        Assert.Contains("no longer there", request.RefusedText);
        Assert.NotEqual(request.DoneText, request.RefusedText);
    }

    [Theory]
    [InlineData(0.4, "0.4 s")]
    [InlineData(9.9, "9.9 s")]
    [InlineData(42, "42 s")]
    [InlineData(184, "3 min 4 s")]
    [InlineData(4320, "1 h 12 min")]
    public void An_elapsed_time_reads_at_the_precision_it_is_glanced_at(double seconds, string expected)
        => Assert.Equal(expected, BackendAction.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_negative_elapsed_time_does_not_render_as_one()
    {
        // now() - query_start can come back marginally negative across a clock adjustment on the server.
        Assert.Equal("0.0 s", BackendAction.FormatElapsed(TimeSpan.FromMilliseconds(-5)));
    }

    // ---- what read-only refuses -----------------------------------------------------------------

    [Fact]
    public void Terminate_is_refused_on_a_read_only_connection()
    {
        var refused = WriteRefusal.ReasonForTerminate(Conn(readOnly: true));

        Assert.NotNull(refused);
        Assert.Contains("prod-eu", refused);
        Assert.Contains("read-only", refused);
        // And it says what still works, because that is the thing the user came to the panel to do.
        Assert.Contains("Cancelling", refused);
    }

    [Fact]
    public void Terminate_is_allowed_on_an_ordinary_connection()
        => Assert.Null(WriteRefusal.ReasonForTerminate(Conn()));

    [Fact]
    public void Nothing_refuses_a_cancel()
    {
        // The asymmetry #101 was decided on, asserted so a later tidy-up that "makes read-only consistent"
        // has to argue with a test rather than with a comment.
        Assert.Null(WriteRefusal.Reason(Conn(readOnly: true), []));
        Assert.Null(WriteRefusal.ReasonForEdits(Conn()));
    }
}
