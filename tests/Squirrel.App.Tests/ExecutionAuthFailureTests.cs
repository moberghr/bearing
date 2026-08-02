using Squirrel.App.ViewModels;
using Xunit;

namespace Squirrel.App.Tests;

public class ExecutionAuthFailureTests
{
    [Theory]
    [InlineData("28P01", true)]   // invalid_password
    [InlineData("28000", true)]   // invalid_authorization_specification
    [InlineData("28xyz", true)]   // any 28-class
    [InlineData("42601", false)]  // syntax_error
    [InlineData("57014", false)]  // query_canceled
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAuthFailure_matches_only_the_28xxx_class(string? sqlState, bool expected)
        => Assert.Equal(expected, ExecutionViewModel.IsAuthFailure(sqlState));
}
