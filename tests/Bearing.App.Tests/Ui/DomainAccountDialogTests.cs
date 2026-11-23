using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Bearing.App.Connections;
using Bearing.App.Views;
using Bearing.Core.Data;
using Bearing.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The connection dialog's "Domain account (NTLM)" credential kind, as wired: offered on SQL Server only,
/// shows the password box and its own hint, saves the kind and the password, and flags a user name with no
/// domain. Reads items, <c>IsVisible</c> flags and the built record; how the hints look needs eyeball QA
/// (§0.2).
/// </summary>
[Collection(UiTestCollection.Name)]
public class DomainAccountDialogTests
{
    private const string Domain = "Domain account (NTLM)";

    private readonly UiTestSession _ui;

    public DomainAccountDialogTests(UiTestSession ui) => _ui = ui;

    private static void Choose(ConnectionDialog dialog, string kind)
    {
        var items = ConnectionEditorProbe.Items(dialog, "CredentialKindBox").ToList();
        ConnectionEditorProbe.Combo(dialog, "CredentialKindBox").SelectedIndex = items.IndexOf(kind);
    }

    [Fact]
    public Task The_kind_is_offered_on_sql_server_only() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Dialog();

        Assert.DoesNotContain(Domain, ConnectionEditorProbe.Items(dialog, "CredentialKindBox"));

        ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex = 1;   // Microsoft SQL Server
        var onSqlServer = ConnectionEditorProbe.Items(dialog, "CredentialKindBox");
        Assert.Contains(Domain, onSqlServer);
        // The default for a new connection is the first entry; an engine-specific kind must not take it.
        Assert.Equal("Stored password", onSqlServer[0]);

        ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex = 0;
        Assert.DoesNotContain(Domain, ConnectionEditorProbe.Items(dialog, "CredentialKindBox"));
    });

    [Fact]
    public Task Choosing_it_shows_the_password_box_and_its_hint_and_saves_both() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Dialog();
        ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex = 1;

        Choose(dialog, Domain);

        Assert.True(dialog.FindControl<TextBlock>("DomainHint")!.IsVisible);
        Assert.False(dialog.FindControl<TextBlock>("IntegratedHint")!.IsVisible);
        Assert.True(dialog.FindControl<TextBox>("PasswordBox")!.IsVisible);
        Assert.Equal(CredentialKind.DomainPassword, dialog.BuildConnection().CredentialKind);
        Assert.True(CredentialKindOptions.KeepsAStoredPassword(CredentialKind.DomainPassword));
    });

    [Fact]
    public void A_user_with_no_domain_is_flagged_only_for_this_kind()
    {
        var sqlServer = new ProviderRegistry().All.Single(p => p.SupportsDomainPassword);
        var info = new ConnectionInfo
        {
            Id = Guid.NewGuid(), Name = "n", ProviderId = sqlServer.Id,
            Host = "db.example", Port = 1433, User = "ana",
        };
        var model = ConnectionFieldModel.For(sqlServer, info);

        Assert.Contains(model.Validate(CredentialKind.DomainPassword), p => p.Contains(@"DOMAIN\user"));
        Assert.DoesNotContain(model.Validate(CredentialKind.StoredPassword), p => p.Contains("DOMAIN"));

        var withDomain = ConnectionFieldModel.For(sqlServer, info with { User = @"corp\ana" });
        Assert.DoesNotContain(withDomain.Validate(CredentialKind.DomainPassword), p => p.Contains("DOMAIN"));
    }
}
