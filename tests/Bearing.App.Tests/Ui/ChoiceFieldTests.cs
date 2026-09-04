using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Bearing.App.Views;
using Bearing.Core.Data;
using Bearing.Core.Schema;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The connection editor renders a <see cref="ConnectionFieldKind.Choice"/> field as a dropdown, and one
/// without candidates as a text box.
/// <para>
/// Which values the dropdown offers is <c>ConnectionFieldState.Candidates</c>' decision and is unit-tested
/// there (<c>ConnectionFieldModelTests</c>); what only a realized control tree can answer is <b>which
/// control got built</b>. That was the whole defect: the kind existed, no candidate list did, and the
/// dialog rendered a text box — so a Choice field let the user type a value the provider never offered.
/// </para>
/// <para>
/// Against a hand-rolled provider, because <b>no shipped engine declares a Choice field</b>: sslmode and
/// SQL Server's Encrypt/TrustServerCertificate all became the typed <see cref="ConnectionInfo.Tls"/>
/// (#23). Adding one to a real provider to make this testable would put a second source of truth next to
/// the typed field that owns it (§4.1 — hand-rolled fakes, no mocking library).
/// </para>
/// <para>
/// Nothing is asserted about how any of it <em>looks</em> (§0.2/§4.3): these read the control's type, its
/// items and its selected index. The dialog still needs eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ChoiceFieldTests
{
    private readonly UiTestSession _ui;

    public ChoiceFieldTests(UiTestSession ui) => _ui = ui;

    [Fact]
    public Task A_choice_field_with_candidates_is_a_dropdown() => _ui.Run(() =>
    {
        var dialog = Dialog();

        var combo = ConnectionEditorProbe.Editor<ComboBox>(dialog, "mode");

        Assert.Equal(new[] { "fast", "safe", "paranoid" }, combo.ItemsSource!.Cast<string>());
        // The declared default is selected, not merely placeholdered: a dropdown showing nothing while the
        // model holds "fast" is the same lie the text box told.
        Assert.Equal(0, combo.SelectedIndex);
        Assert.Equal("fast", combo.SelectedItem);
    });

    [Fact]
    public Task A_choice_field_with_no_candidates_is_still_a_text_box() => _ui.Run(() =>
    {
        var dialog = Dialog();

        // The fallback is kept on purpose — an empty dropdown is a control the user cannot answer with.
        var box = ConnectionEditorProbe.Editor<TextBox>(dialog, "flavour");

        Assert.Equal("", box.Text);
    });

    /// <summary>
    /// A selection has to reach <see cref="ConnectionInfo"/>, not merely the combo box — a handler that was
    /// never attached would leave the field at its default and look fine on screen.
    /// <para>
    /// Read back through the dialog's own Test button, which is the only production path that hands out a
    /// built connection without closing the window. Its handler is <c>async void</c>, but the delegate is
    /// invoked before the first await, so what is asserted here is the synchronous half (§4.5).
    /// </para>
    /// </summary>
    [Fact]
    public Task Choosing_a_value_reaches_the_built_connection() => _ui.Run(() =>
    {
        ConnectionInfo? built = null;
        var dialog = Dialog(test: info => built = info);
        var combo = ConnectionEditorProbe.Editor<ComboBox>(dialog, "mode");

        combo.SelectedIndex = 2;
        dialog.FindControl<Button>("TestButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(built);
        Assert.Equal("paranoid", built!.Options["mode"]);
        // And the candidate-less Choice field still round-trips as text rather than being dropped.
        Assert.DoesNotContain("flavour", built.Options.Keys);
    });

    /// <summary>An edited connection carrying a value the provider does not declare selects that value —
    /// the dropdown has to be able to show what is actually saved, or opening the editor would silently
    /// change it on the next save.</summary>
    [Fact]
    public Task An_undeclared_saved_value_is_selected_rather_than_dropped() => _ui.Run(() =>
    {
        var existing = new ConnectionInfo
        {
            Id = Guid.NewGuid(),
            Name = "c",
            ProviderId = ChoiceProvider.ProviderId,
            Host = "localhost",
            Port = 1234,
            Database = "d",
            User = "u",
            Options = new Dictionary<string, string> { ["mode"] = "reckless" },
        };

        var dialog = Dialog(existing);
        var combo = ConnectionEditorProbe.Editor<ComboBox>(dialog, "mode");

        Assert.Equal("reckless", combo.SelectedItem);
        Assert.Equal("reckless", combo.ItemsSource!.Cast<string>().First());
    });

    // ---- Harness ---------------------------------------------------------------------------------

    private static ConnectionDialog Dialog(
        ConnectionInfo? existing = null, Action<ConnectionInfo>? test = null)
        => new(existing, null,
            (info, _, _) => { test?.Invoke(info); return Task.FromResult(false); },
            providers: new SingleProviderRegistry(new ChoiceProvider()));

    /// <summary>A registry of exactly one engine, so the dialog's picker has nothing else to select and no
    /// real provider's field list is under test.</summary>
    private sealed class SingleProviderRegistry(IDbProvider provider) : IProviderRegistry
    {
        public IDbProvider Get(string providerId) => provider;
        public IReadOnlyCollection<IDbProvider> All { get; } = new[] { provider };
    }

    /// <summary>Declares both shapes of Choice field: one with candidates, one without.</summary>
    private sealed class ChoiceProvider : IDbProvider
    {
        public const string ProviderId = "choiceui";

        public string Id => ProviderId;
        public string DisplayName => "Choice UI";
        public bool SupportsIntegratedAuth => false;
        public bool SupportsEntraToken => false;
        public DbErrorKind Classify(QueryError error) => DbErrorKind.Unknown;
        public DbErrorKind ClassifyException(Exception exception) => DbErrorKind.Unknown;

        public IReadOnlyList<ConnectionField> ConnectionFields { get; } = new[]
        {
            new ConnectionField("Host", "Host", ConnectionFieldKind.Text, Required: true, Default: "localhost"),
            new ConnectionField("Port", "Port", ConnectionFieldKind.Number, Required: true, Default: "1234"),
            new ConnectionField("Database", "Database", ConnectionFieldKind.Text, Required: true),
            new ConnectionField("User", "User", ConnectionFieldKind.Text, Required: true),
            new ConnectionField("mode", "Mode", ConnectionFieldKind.Choice, Required: false, Default: "fast",
                Choices: new[] { "fast", "safe", "paranoid" }),
            new ConnectionField("flavour", "Flavour", ConnectionFieldKind.Choice, Required: false),
        };

        public IDbConnectionFactory CreateConnectionFactory(ConnectionInfo info, string? password)
            => throw new NotSupportedException("declares fields only");
        public IMetadataReader CreateMetadataReader(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
        public IQueryExecutor CreateQueryExecutor(IDbConnectionFactory factory)
            => throw new NotSupportedException("declares fields only");
    }
}
