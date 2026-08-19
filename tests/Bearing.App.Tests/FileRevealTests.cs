using System.IO;
using Bearing.App.Services;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// The one testable piece of revealing a file: the GVariant argument handed to the freedesktop
/// <c>ShowItems</c> D-Bus method, which is how the file gets <em>selected</em> on Linux rather than its
/// folder merely opened. Launching a file manager isn't testable here (and would open a window), so whether
/// the reveal lands is eyeball-QA (§4.3) — this pins the argument, whose failure mode is silent: a malformed
/// literal just exits non-zero and falls back to the old open-the-folder behaviour.
/// </summary>
public class FileRevealTests
{
    private static string Abs(params string[] parts) => Path.GetFullPath(Path.Combine(parts));

    [Fact]
    public void The_uri_is_wrapped_as_a_single_element_gvariant_array()
    {
        var arg = FileReveal.ShowItemsUris(Abs("tmp", "proj", "scripts", "a.sql"));

        Assert.StartsWith("[\"file://", arg);
        Assert.EndsWith("a.sql\"]", arg);
    }

    [Fact]
    public void A_space_in_the_path_is_percent_encoded_not_left_to_split_the_argument()
    {
        var arg = FileReveal.ShowItemsUris(Abs("tmp", "my project", "morning check.sql"));

        Assert.Contains("my%20project", arg);
        Assert.Contains("morning%20check.sql", arg);
        Assert.DoesNotContain(" ", arg);
    }

    [Fact]
    public void A_quote_in_the_file_name_cannot_break_out_of_the_literal()
    {
        // The whole reason the literal uses double quotes: this must not close it early.
        var arg = FileReveal.ShowItemsUris(Abs("tmp", "say \"hi\".sql"));

        Assert.Equal(2, arg.Split('"').Length - 1);   // exactly the opening and closing quote
        Assert.Contains("%22", arg);
    }

    [Fact]
    public void An_apostrophe_survives_as_itself_which_is_why_single_quotes_are_not_used()
    {
        var arg = FileReveal.ShowItemsUris(Abs("tmp", "sasa's report.sql"));

        Assert.Contains("'", arg);                    // untouched by URI escaping
        Assert.Equal(2, arg.Split('"').Length - 1);   // still exactly one balanced pair of delimiters
    }
}
