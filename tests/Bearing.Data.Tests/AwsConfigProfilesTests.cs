using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The DynamoDB editor's AWS-profile and region suggestions (§1.9c): the profiles and their regions are read from
/// the shared <b>config</b> file only — never the credentials file — and nothing else in it is kept. Every file
/// here is a temp file; nothing reads the real <c>~/.aws</c>.
/// </summary>
public sealed class AwsConfigProfilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bearing-aws-config-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
    }

    private const string Sample = """
        # a comment
        ; another comment
        [default]
        region = eu-west-1
        output = json

        [profile dev]
        sso_session = corp
        sso_account_id = 111122223333
        region=us-east-2   # inline note

        [sso-session corp]
        sso_region = eu-central-1
        sso_start_url = https://example.awsapps.com/start

        [profile ci]
        role_arn = arn:aws:iam::111122223333:role/ci
        credential_process = /usr/bin/fetch-creds
        s3 =
          region = should-not-be-read
        aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY

        [services corp]
        dynamodb =
          endpoint_url = http://localhost:8000

        [profilebroken]
        region = nope
        this line has no equals sign
        """;

    [Fact]
    public void Default_and_named_profiles_are_read_with_their_regions_and_other_sections_are_not()
    {
        var profiles = AwsConfigProfiles.Parse(Sample);

        Assert.Equal(
            [new AwsConfigProfile("default", "eu-west-1"), new AwsConfigProfile("dev", "us-east-2"),
                new AwsConfigProfile("ci", null)],
            profiles);
    }

    [Fact]
    public void Nothing_but_profile_names_and_regions_is_kept()
    {
        var profiles = AwsConfigProfiles.Parse(Sample);

        // The result is records of two strings; none of the other keys' values is among them.
        var kept = profiles.SelectMany(p => new[] { p.Name, p.Region }).OfType<string>().ToList();
        Assert.Equal(["default", "eu-west-1", "dev", "us-east-2", "ci"], kept);
        Assert.DoesNotContain(kept, v => v.Contains("wJalrXUtnFEMI", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, v => v.Contains("arn:", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, v => v.Contains("should-not-be-read", StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_named_twice_is_listed_once_with_the_later_region()
    {
        var profiles = AwsConfigProfiles.Parse("[default]\nregion = eu-west-1\n[profile default]\nregion = us-west-2\n");

        Assert.Equal([new AwsConfigProfile("default", "us-west-2")], profiles);
    }

    [Fact]
    public void Windows_line_endings_and_empty_text_are_read()
    {
        Assert.Equal([new AwsConfigProfile("a", "eu-north-1")],
            AwsConfigProfiles.Parse("[profile a]\r\nregion = eu-north-1\r\n"));
        Assert.Empty(AwsConfigProfiles.Parse(""));
    }

    [Fact]
    public void A_missing_file_or_directory_is_an_empty_list()
    {
        Assert.Empty(AwsConfigProfiles.Read(Path.Combine(_dir, "nope", "config")));
        Assert.Empty(AwsConfigProfiles.Read(Path.Combine(_dir, "config")));
        Assert.Empty(AwsConfigProfiles.Read(null));
        // A directory where the file should be is unreadable as a file.
        Assert.Empty(AwsConfigProfiles.Read(_dir));
    }

    [Fact]
    public void A_file_larger_than_the_bound_is_not_read()
    {
        var path = Path.Combine(_dir, "config");
        File.WriteAllText(path, "[profile big]\nregion = eu-west-1\n" + new string('#', (int)AwsConfigProfiles.MaxBytes));

        Assert.Empty(AwsConfigProfiles.Read(path));
    }

    [Fact]
    public void The_file_is_read_from_the_path_given()
    {
        var path = Path.Combine(_dir, "config");
        File.WriteAllText(path, Sample);

        Assert.Equal(["default", "dev", "ci"], AwsConfigProfiles.Read(path).Select(p => p.Name));
    }

    [Fact]
    public void The_config_path_is_aws_config_file_when_set_and_home_aws_config_otherwise()
    {
        var set = Path.Combine(_dir, "elsewhere");
        Assert.Equal(set, AwsConfigProfiles.ConfigPath(Env(("AWS_CONFIG_FILE", set)), "/home/x"));
        Assert.Equal(Path.Combine("/home/x", ".aws", "config"), AwsConfigProfiles.ConfigPath(Env(), "/home/x"));
        Assert.Equal(Path.Combine("/home/x", ".aws", "config"),
            AwsConfigProfiles.ConfigPath(Env(("AWS_CONFIG_FILE", "  ")), "/home/x"));
        Assert.Null(AwsConfigProfiles.ConfigPath(Env(), null));
    }

    /// <summary>
    /// The boundary: a home holding only a credentials file (with profiles in it) yields nothing, and the path is
    /// never the credentials file even when <c>AWS_SHARED_CREDENTIALS_FILE</c> is set — the path builder never
    /// asks for it.
    /// </summary>
    [Fact]
    public void The_credentials_file_is_never_the_source()
    {
        var aws = Directory.CreateDirectory(Path.Combine(_dir, ".aws")).FullName;
        var credentials = Path.Combine(aws, "credentials");
        File.WriteAllText(credentials,
            "[default]\naws_access_key_id = AKIAIOSFODNN7EXAMPLE\naws_secret_access_key = secret\nregion = eu-west-1\n"
            + "[leaked]\nregion = us-east-1\n");
        var asked = new List<string>();
        string? Lookup(string name)
        {
            asked.Add(name);
            return name == "AWS_SHARED_CREDENTIALS_FILE" ? credentials : null;
        }

        var path = AwsConfigProfiles.ConfigPath(Lookup, _dir);

        Assert.Equal(Path.Combine(aws, "config"), path);
        Assert.Equal(["AWS_CONFIG_FILE"], asked);
        Assert.Empty(AwsConfigProfiles.Read(path));
    }

    [Fact]
    public void The_region_list_holds_eu_west_1_with_its_name_and_no_unknown_or_s3_only_entry()
    {
        // Resolve a region the SDK has no entry for first: it registers it as "Unknown" in its own table.
        _ = Amazon.RegionEndpoint.GetBySystemName("bearing-test-1");

        Assert.Contains(new AwsRegion("eu-west-1", "Europe (Ireland)"), AwsRegions.All);
        Assert.Contains(AwsRegions.All, r => r.Code == "us-east-1");
        Assert.DoesNotContain(AwsRegions.All, r => r.Code.EndsWith("-regional", StringComparison.Ordinal));
        Assert.DoesNotContain(AwsRegions.All, r => r.Name.Contains("Unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(AwsRegions.All, r => r.Code == "bearing-test-1");
        Assert.Equal(AwsRegions.All.Count, AwsRegions.All.Select(r => r.Code).Distinct().Count());
    }

    [Fact]
    public void The_provider_suggests_region_codes_named_and_profiles_carrying_their_region_for_the_region_field()
    {
        var provider = new DynamoDbProvider(() =>
            [new AwsConfigProfile("dev", "eu-west-1"), new AwsConfigProfile("ci", null)]);
        var region = provider.ConnectionFields.Single(f => f.Key == "Host");
        var profile = provider.ConnectionFields.Single(f => f.Key == "User");
        var endpoint = provider.ConnectionFields.Single(f => f.Key == DynamoDbConnectionFactory.EndpointOption);

        var ireland = Assert.Single(region.Suggestions!(), s => s.Value == "eu-west-1");
        Assert.Equal("Europe (Ireland)", ireland.Description);
        Assert.Null(ireland.Fills);

        var profiles = profile.Suggestions!();
        Assert.Equal(["dev", "ci"], profiles.Select(s => s.Value));
        Assert.Equal("eu-west-1", profiles[0].Fills!["Host"]);
        Assert.Null(profiles[1].Fills);

        Assert.Null(endpoint.Suggestions);
        Assert.Equal("Leave blank for AWS; set only for DynamoDB Local or a VPC/FIPS endpoint.", endpoint.Hint);
        Assert.All(provider.ConnectionFields, f => Assert.Equal(ConnectionFieldKind.Text, f.Kind));
    }

    [Fact]
    public void The_profile_source_is_asked_each_time_the_editor_asks_not_when_the_provider_is_built()
    {
        var calls = 0;
        var provider = new DynamoDbProvider(() =>
        {
            calls++;
            return [];
        });
        Assert.Equal(0, calls);

        Assert.Empty(provider.ConnectionFields.Single(f => f.Key == "User").Suggestions!());
        Assert.Equal(1, calls);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
        => name => values.FirstOrDefault(v => v.Name == name).Value;
}
