using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bearing.App.Settings;
using Bearing.App.ViewModels;
using Bearing.App.Workspace;
using Bearing.Core.Data;
using Bearing.Core.Workspace;
using Bearing.Persistence;
using Bearing.Sessions;
using Xunit;

namespace Bearing.App.Tests;

/// <summary>
/// A DynamoDB connection through <see cref="ExecutionViewModel"/>, with the registry answering DynamoDB's engine
/// facts (no password, no manual commit): an authentication failure is not retried or prompted for whatever kind
/// the record names (TC1), and a read-only connection refuses a write before anything connects (TC7).
/// </summary>
public class DynamoDbExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bearing-ddb-exec", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }

    private sealed record Harness(
        ExecutionViewModel Exec,
        FakeProvider Provider,
        FakeExecutor Executor,
        FakeCredentialPrompt Prompt,
        FakeSecretStore Secrets,
        List<string> Statuses);

    private Harness Build(ConnectionInfo conn, FakeSecretStore? secrets = null)
    {
        Directory.CreateDirectory(_root);
        var executor = new FakeExecutor();
        var provider = new FakeProvider
        {
            Id = "dynamodb",
            DisplayName = "Amazon DynamoDB",
            SupportsPasswordCredentials = false,
            SupportsManualCommit = false,
            Executor = executor,
        };
        var store = secrets ?? new FakeSecretStore();
        var prompt = new FakeCredentialPrompt("should-never-be-asked");
        var ctx = new WorkspaceContext(
            provider,
            new JsonProjectStore(),
            new JsonSessionStore(),
            new SqliteQueryLog(Path.Combine(_root, "log.sqlite")),
            new FileRecentProjects(Path.Combine(_root, "recent.json")),
            store,
            credentialPrompt: prompt,
            settings: SettingsService.InMemory(new AppSettings { AutosaveMode = AutosaveMode.Off }));
        ctx.Project = new Project { Directory = _root, Manifest = new ProjectManifest { Connections = { conn } } };
        var tab = new EditorTabViewModel("orders") { ConnectionId = conn.Id };
        ctx.Tabs.Add(tab);
        ctx.SelectedTab = tab;
        var statuses = new List<string>();
        ctx.Status = statuses.Add;

        return new Harness(new ExecutionViewModel(ctx, new FakeDialogs()), provider, executor, prompt, store, statuses);
    }

    private static ConnectionInfo Dynamo(CredentialKind kind, bool readOnly = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "orders-prod",
        ProviderId = "dynamodb",
        Host = "eu-west-1",
        Database = "eu-west-1",
        User = "dev",
        CredentialKind = kind,
        ReadOnly = readOnly,
        // Not under test here: keyed reads never ask anyway, and this keeps the scan dialog out of the counts.
        ConfirmFullScans = false,
    };

    // ---- TC1: an authentication failure on an engine with no password -------------------------------

    /// <summary>
    /// The AWS SDK resolves the profile; a retry would sign the same request again. One run, no prompt, and no
    /// "Reauthenticating…" for an authentication that would not happen.
    /// </summary>
    [Fact]
    public async Task An_aws_profile_auth_failure_runs_once_and_never_prompts()
    {
        var h = Build(Dynamo(CredentialKind.AwsProfile));
        h.Executor.FailWith = "The security token included in the request is invalid.";
        h.Executor.FailState = "28000";

        await h.Exec.ExecuteAsync("select * from \"Orders\" where customerId = 'a'");

        Assert.Single(h.Executor.Executed);
        Assert.Equal(0, h.Prompt.Calls);
        Assert.DoesNotContain("Reauthenticating…", h.Statuses);
    }

    /// <summary>
    /// A hand-edited DynamoDB entry with the default StoredPassword kind, on a machine with no keyring — the one
    /// combination the password path retries with a prompt. The provider takes no password, so the resolver
    /// neither reads the store nor asks, and the run is not retried.
    /// </summary>
    [Fact]
    public async Task A_stored_password_kind_on_dynamodb_with_no_keyring_runs_once_and_never_prompts()
    {
        var h = Build(Dynamo(CredentialKind.StoredPassword), new FakeSecretStore { IsSecure = false, CanStore = false });
        h.Executor.FailWith = "The security token included in the request is invalid.";
        h.Executor.FailState = "28000";

        await h.Exec.ExecuteAsync("select * from \"Orders\" where customerId = 'a'");

        Assert.Single(h.Executor.Executed);
        Assert.Equal(0, h.Prompt.Calls);
        Assert.DoesNotContain("Reauthenticating…", h.Statuses);
        Assert.Empty(h.Secrets.Fetched);   // never even read: the provider would ignore what it found
    }

    // ---- TC7: read-only refuses a write before anything connects ------------------------------------

    [Fact]
    public async Task A_delete_on_a_read_only_dynamodb_connection_is_refused_and_nothing_runs()
    {
        var h = Build(Dynamo(CredentialKind.AwsProfile, readOnly: true));

        await h.Exec.ExecuteAsync("delete from \"T\" where pk = 1");

        Assert.Contains(h.Statuses, s => s.StartsWith("orders-prod is read-only — ", StringComparison.Ordinal)
                                         && s.Contains("DELETE", StringComparison.Ordinal)
                                         && s.Contains("not run", StringComparison.Ordinal));
        Assert.Empty(h.Executor.Executed);
        Assert.Equal(0, h.Provider.FactoriesCreated);   // refused before the run would connect
    }
}

/// <summary>
/// The resolver's half of TC1: on an engine that takes no password, a password kind resolves to no secret without
/// the store or the prompt, and <see cref="CredentialResolver.CanRefresh"/> says no for every kind.
/// </summary>
public class NoPasswordEngineCredentialTests
{
    private static ConnectionInfo Conn(CredentialKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Name = "orders",
        ProviderId = "dynamodb",
        Host = "eu-west-1",
        CredentialKind = kind,
    };

    private static bool TakesPassword(ConnectionInfo info) => info.ProviderId != "dynamodb";

    [Theory]
    [InlineData(CredentialKind.StoredPassword)]
    [InlineData(CredentialKind.Prompt)]
    public async Task A_password_kind_resolves_to_nothing_without_the_store_or_a_prompt(CredentialKind kind)
    {
        var secrets = new FakeSecretStore { CanStore = false };
        var info = Conn(kind);
        secrets.Seed(info.Id, "left-over");
        var prompt = new FakeCredentialPrompt("should-never-be-asked");
        var resolver = new CredentialResolver(() => secrets, prompt, new ThrowingEntraTokens(), takesPassword: TakesPassword);

        var credential = await resolver.ResolveAsync(info, forceRefresh: true, CancellationToken.None);

        Assert.Null(credential.Secret);
        Assert.Equal(0, prompt.Calls);
        Assert.Empty(secrets.Fetched);
    }

    [Theory]
    [InlineData(CredentialKind.StoredPassword)]
    [InlineData(CredentialKind.Prompt)]
    [InlineData(CredentialKind.AwsProfile)]
    [InlineData(CredentialKind.EntraToken)]
    public void Nothing_is_refreshable_on_an_engine_with_no_password(CredentialKind kind)
    {
        var resolver = new CredentialResolver(
            () => new FakeSecretStore { CanStore = false }, null, new ThrowingEntraTokens(), takesPassword: TakesPassword);

        Assert.False(resolver.CanRefresh(Conn(kind)));
    }

    /// <summary>The other engines keep the rules they had (the arms moved here from ExecutionViewModel).</summary>
    [Theory]
    [InlineData(CredentialKind.Prompt, true, true)]
    [InlineData(CredentialKind.EntraToken, true, true)]
    [InlineData(CredentialKind.StoredPassword, true, false)]
    [InlineData(CredentialKind.StoredPassword, false, true)]
    [InlineData(CredentialKind.Integrated, false, false)]
    [InlineData(CredentialKind.AwsProfile, false, false)]
    public void A_password_engine_keeps_its_refresh_rules(CredentialKind kind, bool canStore, bool expected)
    {
        var resolver = new CredentialResolver(
            () => new FakeSecretStore { CanStore = canStore }, null, new ThrowingEntraTokens(), takesPassword: TakesPassword);

        Assert.Equal(expected, resolver.CanRefresh(Conn(kind) with { ProviderId = "postgres" }));
    }
}
