using System;
using Bearing.Core.Data;
using Bearing.Data.Postgres;
using Bearing.Data.SqlServer;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// Error classification, which is the providers' job now. This is where
/// <c>Bearing.App.Tests.ExecutionAuthFailureTests</c> moved to: it covered
/// <c>ExecutionViewModel.IsAuthFailure</c>, an App-layer static that read Postgres SQLSTATEs as strings and
/// so quietly mislabelled every other engine's codes — SQL Server has no SQLSTATEs at all. The cases it
/// asserted are all still asserted below, against <see cref="PostgresProvider"/>, plus the SQL Server side.
/// <para>
/// Pure: no server, no connection. Both providers classify from a code and a message, so all of it runs on a
/// box with neither engine installed.
/// </para>
/// </summary>
public class ProviderClassificationTests
{
    private static QueryError Error(string? sqlState, string message = "boom")
        => new(message, sqlState, null);

    // ---- PostgreSQL: the cases the old App-layer test covered, verdict for verdict ---------------

    [Theory]
    [InlineData("28P01", DbErrorKind.Authentication)]   // invalid_password
    [InlineData("28000", DbErrorKind.Authentication)]   // invalid_authorization_specification
    [InlineData("28xyz", DbErrorKind.Authentication)]   // any 28-class
    [InlineData("42601", DbErrorKind.SyntaxOrShape)]    // syntax_error
    [InlineData("0A000", DbErrorKind.SyntaxOrShape)]    // feature_not_supported
    [InlineData("57014", DbErrorKind.Canceled)]         // query_canceled
    [InlineData("", DbErrorKind.Unknown)]
    [InlineData(null, DbErrorKind.Unknown)]
    public void Postgres_classifies_by_sqlstate(string? sqlState, DbErrorKind expected)
        => Assert.Equal(expected, new PostgresProvider().Classify(Error(sqlState)));

    [Fact]
    public void Postgres_finds_an_auth_failure_in_an_untyped_exception_chain()
    {
        // The connect path has an exception, not a QueryError, and Npgsql wraps socket / TLS / SASL
        // failures — so the message chain is scanned. This is the heuristic ExecutionViewModel used to
        // carry as LooksLikeAuthFailure; losing it would lose the "offer to re-enter the password" path.
        var provider = new PostgresProvider();
        var wrapped = new InvalidOperationException(
            "Could not connect to 'prod'", new Exception("28P01: password authentication failed for user \"app\""));

        Assert.Equal(DbErrorKind.Authentication, provider.ClassifyException(wrapped));
        Assert.Equal(DbErrorKind.Unknown, provider.ClassifyException(new Exception("connection refused")));
    }

    // ---- SQL Server: numbers, not SQLSTATEs ------------------------------------------------------

    [Theory]
    [InlineData("18456", DbErrorKind.Authentication)]   // login failed for user
    [InlineData("18452", DbErrorKind.Authentication)]   // login from an untrusted domain
    [InlineData("4060", DbErrorKind.Authentication)]    // cannot open the requested database
    [InlineData("0", DbErrorKind.Canceled)]             // "Operation cancelled by user."
    [InlineData("102", DbErrorKind.SyntaxOrShape)]      // incorrect syntax near
    [InlineData("1033", DbErrorKind.SyntaxOrShape)]     // ORDER BY invalid in a subquery
    [InlineData("-2", DbErrorKind.Unknown)]             // timeout: a real failure, and nobody asked for it
    [InlineData("", DbErrorKind.Unknown)]
    [InlineData(null, DbErrorKind.Unknown)]
    public void SqlServer_classifies_by_error_number(string? number, DbErrorKind expected)
        => Assert.Equal(expected, new SqlServerProvider().Classify(Error(number)));

    [Fact]
    public void SqlServer_does_not_read_a_postgres_sqlstate_as_anything()
    {
        // The point of moving this behind the provider: "28P01" is an authentication failure to Postgres and
        // simply not a number to SQL Server. The old shared static said Authentication for both.
        Assert.Equal(DbErrorKind.Unknown, new SqlServerProvider().Classify(Error("28P01")));
        Assert.Equal(DbErrorKind.Unknown, new SqlServerProvider().Classify(Error("57014")));
    }

    [Fact]
    public void SqlServer_finds_a_login_failure_in_an_untyped_exception_chain()
    {
        var provider = new SqlServerProvider();
        var wrapped = new InvalidOperationException(
            "Could not connect to 'sqlprod'", new Exception("Login failed for user 'app'."));

        Assert.Equal(DbErrorKind.Authentication, provider.ClassifyException(wrapped));
        Assert.Equal(DbErrorKind.Unknown, provider.ClassifyException(new Exception("network path not found")));
    }

    // ---- Capability flag -------------------------------------------------------------------------

    [Fact]
    public void Only_sql_server_offers_integrated_authentication()
    {
        // The dialog asks the provider rather than hardcoding "postgres has no integrated auth".
        Assert.True(new SqlServerProvider().SupportsIntegratedAuth);
        Assert.False(new PostgresProvider().SupportsIntegratedAuth);
    }

    [Fact]
    public void Both_engines_can_authenticate_with_an_entra_token()
    {
        // Still a separate flag from the one above, and still not a formality: the two drivers want the
        // token in different places (Npgsql as the password, SqlClient on SqlConnection.AccessToken), so
        // "the engine's cloud supports Entra" and "this factory can honour the credential kind" are
        // different questions. The dropdown reads this one. What the flag being true actually buys on SQL
        // Server is asserted in SqlServerEntraTests.
        Assert.True(new PostgresProvider().SupportsEntraToken);
        Assert.True(new SqlServerProvider().SupportsEntraToken);
    }
}
