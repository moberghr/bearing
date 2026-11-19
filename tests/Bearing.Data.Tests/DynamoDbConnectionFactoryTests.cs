using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.DynamoDBv2.Model;
using Bearing.Core.Data;
using Bearing.Data.DynamoDb;
using Xunit;

namespace Bearing.Data.Tests;

/// <summary>
/// The DynamoDB client factory: which endpoints it accepts, what it signs with, and what a sign-in failure
/// says. Building a client connects to nothing, so almost all of it is pure; the two tests that make a call
/// do it against a listener on this machine's loopback, never the network.
/// </summary>
public class DynamoDbConnectionFactoryTests
{
    private static ConnectionInfo Info(string? endpoint = null, string host = "eu-west-1", string user = "")
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "ddb",
            ProviderId = "dynamodb",
            Host = host,
            Port = 0,
            User = user,
            CredentialKind = CredentialKind.AwsProfile,
            Options = endpoint is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [DynamoDbConnectionFactory.EndpointOption] = endpoint },
        };

    // ---- SC6: the endpoint rule --------------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:8000", "http://localhost:8000/")]
    [InlineData("http://LOCALHOST:8000", "http://localhost:8000/")]
    [InlineData("http://127.0.0.1:8000", "http://127.0.0.1:8000/")]
    [InlineData("http://127.4.5.6:8000", "http://127.4.5.6:8000/")]
    [InlineData("http://[::1]:8000", "http://[::1]:8000/")]
    [InlineData("https://dynamodb.eu-west-1.amazonaws.com", "https://dynamodb.eu-west-1.amazonaws.com/")]
    [InlineData("https://localhost:8000", "https://localhost:8000/")]
    [InlineData(
        "https://vpce-0123456789abcdef-abcd.dynamodb.eu-west-1.vpce.amazonaws.com",
        "https://vpce-0123456789abcdef-abcd.dynamodb.eu-west-1.vpce.amazonaws.com/")]
    [InlineData("  http://localhost:8000  ", "http://localhost:8000/")]
    public async Task An_endpoint_that_is_allowed_builds_a_client_without_connecting(string endpoint, string resolved)
    {
        await using var factory = new DynamoDbConnectionFactory(Info(endpoint));

        Assert.NotNull(factory.Api);
        Assert.Equal(resolved, DynamoDbConnectionFactory.ResolveEndpoint(Info(endpoint))!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_endpoint_is_accepted_and_means_the_regions_own(string? endpoint)
    {
        var info = Info(endpoint ?? "");

        Assert.Null(DynamoDbConnectionFactory.ResolveEndpoint(info));
        await using var factory = new DynamoDbConnectionFactory(info);
        Assert.NotNull(factory.Api);
    }

    [Theory]
    [InlineData("http://dynamo.example.com")]
    [InlineData("http://10.0.0.5:8000")]
    [InlineData("http://192.168.1.10:8000")]
    [InlineData("http://dynamodb.eu-west-1.amazonaws.com")]
    [InlineData("http://localhost.example.com:8000")]
    [InlineData("http://[2001:db8::1]:8000")]
    [InlineData("HTTP://dynamo.example.com")]
    public async Task Http_to_a_host_that_is_not_this_machine_is_refused_at_connect_with_the_sentence(string endpoint)
    {
        await using var factory = new DynamoDbConnectionFactory(Info(endpoint));

        var refused = await Assert.ThrowsAsync<DynamoDbConfigurationException>(
            () => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Contains("http://", refused.Message);
        Assert.Contains("localhost", refused.Message);
        Assert.Contains("https://", refused.Message);
        Assert.Contains(endpoint, refused.Message);
        Assert.Equal(DbErrorKind.Unknown, DynamoDbErrors.Classify(refused));
    }

    [Theory]
    [InlineData("dynamo.example.com")]
    [InlineData("localhost:8000")]
    [InlineData("ftp://localhost")]
    [InlineData("http://")]
    [InlineData("not a url")]
    public async Task An_endpoint_that_is_not_an_http_url_is_refused_at_connect(string endpoint)
    {
        await using var factory = new DynamoDbConnectionFactory(Info(endpoint));

        var refused = await Assert.ThrowsAsync<DynamoDbConfigurationException>(
            () => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Contains("not a valid http:// or https:// URL", refused.Message);
    }

    [Fact]
    public async Task A_refused_setting_keeps_refusing_every_use_and_the_constructor_never_throws()
    {
        await using var factory = new DynamoDbConnectionFactory(Info("http://dynamo.example.com"));

        for (var i = 0; i < 2; i++)
        {
            var refused = Assert.Throws<InvalidOperationException>(() => factory.Api);
            Assert.Contains("http://", refused.Message);
            Assert.IsType<DynamoDbConfigurationException>(refused.InnerException);
        }
    }

    [Fact]
    public async Task An_empty_region_is_refused_at_connect()
    {
        await using var factory = new DynamoDbConnectionFactory(Info(host: "  "));

        var refused = await Assert.ThrowsAsync<DynamoDbConfigurationException>(
            () => factory.TestConnectionAsync(CancellationToken.None));

        Assert.Contains("region", refused.Message);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LocalHost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("128.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("localhost.example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void Loopback_means_localhost_or_a_loopback_address(string host, bool expected)
        => Assert.Equal(expected, DynamoDbConnectionFactory.IsLoopbackHost(host));

    // ---- Placeholder keys for a loopback endpoint ----------------------------------------------------

    [Fact]
    public async Task A_loopback_endpoint_with_no_profile_signs_with_the_placeholder_keys()
    {
        using var server = await FakeDynamoDb.StartAsync();
        await using var factory = new DynamoDbConnectionFactory(Info($"http://127.0.0.1:{server.Port}"));

        var ok = await factory.TestConnectionAsync(WithTimeout());

        Assert.True(ok);
        var request = await server.Request;
        Assert.Contains("DynamoDB_20120810.ListTables", request, StringComparison.Ordinal);
        Assert.Contains(
            $"Credential={DynamoDbConnectionFactory.PlaceholderAccessKey}/", request, StringComparison.Ordinal);
        // The signing region is the connection's, not the endpoint's — a ServiceURL has none of its own.
        Assert.Contains("/eu-west-1/dynamodb/aws4_request", request, StringComparison.Ordinal);
        Assert.Contains("\"Limit\":1", request, StringComparison.Ordinal);
    }

    /// <summary>The placeholder keys are for this machine only. An https endpoint to anywhere else with no
    /// profile signs with the SDK's default chain — the client is built with no credentials of Bearing's — so a
    /// VPC endpoint never receives requests signed with keys that mean nothing.</summary>
    [Theory]
    [InlineData("https://vpce-0123456789abcdef-abcd.dynamodb.eu-west-1.vpce.amazonaws.com")]
    [InlineData("https://dynamodb.eu-west-1.amazonaws.com")]
    [InlineData(null)]
    public async Task A_non_loopback_endpoint_with_no_profile_never_signs_with_the_placeholder_keys(string? endpoint)
    {
        await using var factory = new DynamoDbConnectionFactory(Info(endpoint));

        Assert.Null(ExplicitCredentials(factory));
    }

    [Fact]
    public async Task A_loopback_endpoint_with_no_profile_is_built_with_the_placeholder_keys()
    {
        await using var factory = new DynamoDbConnectionFactory(Info("http://localhost:8000"));

        var credentials = Assert.IsType<Amazon.Runtime.BasicAWSCredentials>(ExplicitCredentials(factory));
        Assert.Equal(DynamoDbConnectionFactory.PlaceholderAccessKey, credentials.GetCredentials().AccessKey);
    }

    /// <summary>A named profile wins over the placeholder even on a loopback endpoint: someone pointing a real
    /// profile at a local endpoint (a proxy, a tunnel) asked for that profile. Seen in the signed request.</summary>
    [Fact]
    public async Task A_loopback_endpoint_with_a_named_profile_signs_with_the_profile()
    {
        var path = WriteProfiles("""
            [local-dev]
            aws_access_key_id = AKIALOCALDEVKEY12345
            aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYLOCALDEV
            """);
        try
        {
            using var server = await FakeDynamoDb.StartAsync();
            await using var factory = new DynamoDbConnectionFactory(
                Info($"http://127.0.0.1:{server.Port}", user: "local-dev"), path);

            Assert.True(await factory.TestConnectionAsync(WithTimeout()));

            var request = await server.Request;
            Assert.Contains("Credential=AKIALOCALDEVKEY12345/", request, StringComparison.Ordinal);
            Assert.DoesNotContain(DynamoDbConnectionFactory.PlaceholderAccessKey, request, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The credentials the factory handed the SDK client, or null when it handed none (the SDK's default chain).
    /// Read by reflection, because neither the factory nor the client exposes them — and the SDK's own property,
    /// not a resolve: nothing here asks the default chain for anything.
    /// </summary>
    private static Amazon.Runtime.AWSCredentials? ExplicitCredentials(DynamoDbConnectionFactory factory)
    {
        const System.Reflection.BindingFlags Any =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        var api = factory.Api;
        var client = api.GetType().GetField("_client", Any)?.GetValue(api)
            ?? throw new InvalidOperationException("The factory's API no longer holds an SDK client in _client.");
        var property = typeof(Amazon.Runtime.AmazonServiceClient).GetProperty("ExplicitAWSCredentials", Any)
            ?? throw new InvalidOperationException("AmazonServiceClient.ExplicitAWSCredentials is gone.");

        return (Amazon.Runtime.AWSCredentials?)property.GetValue(client);
    }

    // ---- SC5 against the real SDK: what a profile that cannot sign in says --------------------------

    private static string WriteProfiles(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bearing-aws-{Guid.NewGuid():N}");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task An_sso_profile_with_no_signed_in_session_is_a_sign_in_failure_naming_the_profile()
    {
        // A start URL nothing has ever signed in to, so the SDK finds no cached token and fails before it
        // would touch the network — the real "No valid SSO Token could be found." path.
        var path = WriteProfiles($"""
            [bearing-test-sso]
            sso_start_url = https://bearing-test-{Guid.NewGuid():N}.awsapps.com/start
            sso_region = eu-west-1
            sso_account_id = 123456789012
            sso_role_name = Dev
            """);
        try
        {
            await using var factory = new DynamoDbConnectionFactory(Info(user: "bearing-test-sso"), path);

            var failure = await Assert.ThrowsAsync<DynamoDbSignInException>(
                () => factory.TestConnectionAsync(WithTimeout()));

            Assert.Equal(DbErrorKind.Authentication, DynamoDbErrors.Classify(failure));
            Assert.Contains("'bearing-test-sso'", failure.Message);
            Assert.Contains("aws sso login --profile bearing-test-sso", failure.Message);
            Assert.DoesNotContain("was not found", failure.Message);
            Assert.NotNull(failure.InnerException);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_profile_the_sdk_does_not_know_is_a_sign_in_failure_that_says_it_was_not_found()
    {
        var path = WriteProfiles("""
            [somebody-else]
            aws_access_key_id = AKIAFAKEKEYID1234567
            aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYFAKESECRET
            """);
        try
        {
            await using var factory = new DynamoDbConnectionFactory(Info(user: "dev"), path);

            var failure = await Assert.ThrowsAsync<DynamoDbSignInException>(
                () => factory.TestConnectionAsync(WithTimeout()));

            Assert.True(failure.ProfileMissing);
            Assert.Equal(DbErrorKind.Authentication, DynamoDbErrors.Classify(failure));
            Assert.Contains("'dev' was not found", failure.Message);
            Assert.Contains("aws sso login --profile dev", failure.Message);
            // The file held a key and a secret; neither is anywhere in what the user is told.
            Assert.DoesNotContain("AKIAFAKEKEYID1234567", failure.Message);
            Assert.DoesNotContain("wJalrXUtnFEMI", failure.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_profile_with_static_keys_builds_a_client_without_connecting()
    {
        var path = WriteProfiles("""
            [static]
            aws_access_key_id = AKIAFAKEKEYID1234567
            aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYFAKESECRET
            """);
        try
        {
            await using var factory = new DynamoDbConnectionFactory(Info(user: "static"), path);

            Assert.NotNull(factory.Api);
            Assert.Equal("static", factory.Profile);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_profile_is_trimmed_and_blank_means_the_default_chain()
    {
        await using var named = new DynamoDbConnectionFactory(Info(user: "  dev  "), new CountingApi());
        await using var blank = new DynamoDbConnectionFactory(Info(user: "   "));

        Assert.Equal("dev", named.Profile);
        Assert.Null(blank.Profile);
    }

    // ---- The per-session DescribeTable cache ----------------------------------------------------------

    [Fact]
    public async Task DescribeTable_is_asked_once_per_table_until_it_is_forgotten()
    {
        var api = new CountingApi();
        await using var factory = new DynamoDbConnectionFactory(Info(), api);

        var first = await factory.DescribeTableAsync("Orders", CancellationToken.None);
        var again = await factory.DescribeTableAsync("Orders", CancellationToken.None);
        await factory.DescribeTableAsync("Customers", CancellationToken.None);

        Assert.Same(first, again);
        Assert.Equal(["Orders", "Customers"], api.Described);

        factory.ForgetTable("Orders");
        await factory.DescribeTableAsync("Orders", CancellationToken.None);
        await factory.DescribeTableAsync("Customers", CancellationToken.None);
        Assert.Equal(["Orders", "Customers", "Orders"], api.Described);

        factory.ForgetTables();
        await factory.DescribeTableAsync("Customers", CancellationToken.None);
        Assert.Equal(4, api.Described.Count);
    }

    [Fact]
    public async Task Table_names_are_case_sensitive_in_the_cache()
    {
        var api = new CountingApi();
        await using var factory = new DynamoDbConnectionFactory(Info(), api);

        await factory.DescribeTableAsync("Orders", CancellationToken.None);
        await factory.DescribeTableAsync("orders", CancellationToken.None);

        Assert.Equal(["Orders", "orders"], api.Described);
    }

    [Fact]
    public async Task The_connection_test_is_ListTables_with_a_limit_of_one()
    {
        var api = new CountingApi();
        await using var factory = new DynamoDbConnectionFactory(Info(), api);

        Assert.True(await factory.TestConnectionAsync(CancellationToken.None));

        var request = Assert.Single(api.Listed);
        Assert.Equal(1, request.Limit);
    }

    [Fact]
    public async Task A_sign_in_failure_from_the_connection_test_is_translated_and_anything_else_is_not()
    {
        var sdk = new Amazon.Runtime.AmazonClientException("No valid SSO Token could be found.");
        await using var signedOut = new DynamoDbConnectionFactory(Info(user: "dev"), new CountingApi { ListFails = sdk });
        var translated = await Assert.ThrowsAsync<DynamoDbSignInException>(
            () => signedOut.TestConnectionAsync(CancellationToken.None));
        Assert.Same(sdk, translated.InnerException);

        var boom = new HttpRequestException("connection refused");
        await using var down = new DynamoDbConnectionFactory(Info(), new CountingApi { ListFails = boom });
        var same = await Assert.ThrowsAsync<HttpRequestException>(() => down.TestConnectionAsync(CancellationToken.None));
        Assert.Same(boom, same);

        await using var canceled = new DynamoDbConnectionFactory(Info(), new CountingApi { ListFails = new OperationCanceledException() });
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled.TestConnectionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disposing_the_factory_disposes_the_client()
    {
        var api = new CountingApi();
        var factory = new DynamoDbConnectionFactory(Info(), api);

        await factory.DisposeAsync();

        Assert.True(api.Disposed);
    }

    private static CancellationToken WithTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>Hand-rolled (§4.1): records what the factory asked of it.</summary>
    private sealed class CountingApi : IDynamoDbApi
    {
        public List<string> Described { get; } = [];
        public List<ListTablesRequest> Listed { get; } = [];
        public Exception? ListFails { get; init; }
        public bool Disposed { get; private set; }

        public Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ListTablesResponse> ListTablesAsync(ListTablesRequest request, CancellationToken ct)
        {
            Listed.Add(request);
            return ListFails is null
                ? Task.FromResult(new ListTablesResponse { TableNames = [] })
                : Task.FromException<ListTablesResponse>(ListFails);
        }

        public Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken ct)
        {
            Described.Add(request.TableName);
            return Task.FromResult(new DescribeTableResponse { Table = new TableDescription { TableName = request.TableName } });
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// A one-request DynamoDB on this machine's loopback: reads the request the SDK signs and answers an
    /// empty table list. Lets a test see what the factory's client actually sends without any network.
    /// </summary>
    private sealed class FakeDynamoDb : IDisposable
    {
        private readonly TcpListener _listener;

        private FakeDynamoDb(TcpListener listener)
        {
            _listener = listener;
            Request = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>The raw text of the request the client made (headers and body).</summary>
        public Task<string> Request { get; }

        public static Task<FakeDynamoDb> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeDynamoDb(listener));
        }

        private async Task<string> ServeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client = await _listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();

            var received = new StringBuilder();
            var buffer = new byte[4096];
            var headersEnd = -1;
            var contentLength = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));

                var text = received.ToString();
                if (headersEnd < 0 && (headersEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                {
                    headersEnd += 4;
                    foreach (var line in text[..headersEnd].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                        }
                    }
                }
                if (headersEnd >= 0 && Encoding.UTF8.GetByteCount(text[headersEnd..]) >= contentLength)
                {
                    break;
                }
            }

            const string body = "{\"TableNames\":[]}";
            var response = "HTTP/1.1 200 OK\r\nContent-Type: application/x-amz-json-1.0\r\n"
                + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return received.ToString();
        }

        public void Dispose() => _listener.Stop();
    }
}
