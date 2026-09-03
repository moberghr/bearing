using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Bearing.App.Views;
using Bearing.Core.Data;
using Bearing.Data;
using Xunit;

namespace Bearing.App.Tests.Ui;

/// <summary>
/// The connection editor against the <b>real</b> provider registry: that the engine picker is the registry,
/// that switching engine rebuilds the provider-declared rows and carries what the user typed, that the
/// Credential dropdown gains and loses Windows authentication with the engine, and that the advisory hint
/// tracks both.
/// <para>
/// Every decision behind these is already unit-tested — <c>ConnectionFieldModel.SwitchTo</c>,
/// <c>CredentialKindOptions.For</c>, <c>Validate</c> (§2.5). What no pure helper can answer is whether the
/// dialog is <em>wired</em> to them: the engine switch runs through a XAML <c>SelectionChanged</c> handler
/// into <c>RenderFields</c>, which throws the row controls away and builds new ones, and a handler that was
/// never attached — or a rebuild that mutated nothing — leaves a form that looks right and saves the
/// previous engine's values. That is the layer <c>WiringTests</c> exists for, and this is the same shape for
/// the second engine's arrival.
/// </para>
/// <para>
/// Nothing here claims a visual (§0.2/§4.3): these read a control's type, text, items and
/// <c>IsVisible</c> flag. Whether the rebuilt grid <em>looks</em> right, whether the 90px label column
/// still fits both engines' labels, and whether the amber hint reads well under the fields all still need
/// the user's eyeball QA.
/// </para>
/// </summary>
[Collection(UiTestCollection.Name)]
public class ConnectionEditorTests
{
    private readonly UiTestSession _ui;

    public ConnectionEditorTests(UiTestSession ui) => _ui = ui;

    /// <summary>The engines this build ships, in the registry's own order, which is also the dropdown's
    /// (<c>ProviderRegistry</c>: PostgreSQL first, because it is the engine every existing project file
    /// names).</summary>
    private static readonly string[] Engines = { "PostgreSQL", "Microsoft SQL Server" };

    private const string Integrated = "Windows / integrated authentication";

    // ---- The engine picker -----------------------------------------------------------------------

    [Fact]
    public Task The_engine_picker_is_the_registry() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Dialog();

        // Read as labels rather than as a count: a picker listing the right number of the wrong engines is
        // the failure a count cannot see, and the display name is what the user picks by.
        Assert.Equal(Engines, ConnectionEditorProbe.Items(dialog, "ProviderBox"));
        // A new connection starts on the first registered engine, not on whatever was last edited.
        Assert.Equal(0, ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex);
        Assert.Equal("5432", ConnectionEditorProbe.Editor<TextBox>(dialog, "Port").Text);
    });

    [Fact]
    public Task An_edited_connection_opens_on_its_own_engine() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Dialog(Saved("sqlserver", port: 1433));

        Assert.Equal(1, ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex);
        // And on that engine's own fields — the saved port, not the first provider's default.
        Assert.Equal("1433", ConnectionEditorProbe.Editor<TextBox>(dialog, "Port").Text);
    });

    // ---- The provider-declared rows --------------------------------------------------------------

    /// <summary>
    /// The engine switch replaces the row controls rather than editing them in place, which is the part
    /// that can silently half-work: <c>SwitchTo</c> decides what survives, and a stale <c>TextBox</c> left
    /// on screen over a rebuilt model would show the old engine's values while saving the new engine's.
    /// </summary>
    [Fact]
    public Task Switching_engine_rebuilds_the_rows_and_carries_what_the_user_typed() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Show();
        try
        {
            // A value the user typed, and one left at the previous engine's default — SwitchTo treats the
            // two differently, and this is the only place the difference shows up on a control.
            ConnectionEditorProbe.Type(dialog, "Database", "sales");
            var portBefore = ConnectionEditorProbe.Editor<TextBox>(dialog, "Port");
            Assert.Equal("5432", portBefore.Text);

            ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex = 1;

            var portAfter = ConnectionEditorProbe.Editor<TextBox>(dialog, "Port");
            // A fresh control, so the rebuild really happened; the text alone would also pass if the
            // handler had mutated the old box and left it parented to nothing.
            Assert.NotSame(portBefore, portAfter);
            Assert.Equal("1433", portAfter.Text);
            Assert.Equal("sales", ConnectionEditorProbe.Editor<TextBox>(dialog, "Database").Text);
            // Exactly one editor per key, in row order: a rebuild that appended instead of replacing
            // leaves two boxes with the same name, the second of which nobody reads.
            Assert.Equal(new[] { "Host", "Port", "Database", "User" },
                ConnectionEditorProbe.FieldKeys(dialog));
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    // ---- The Credential dropdown -----------------------------------------------------------------

    /// <summary>
    /// Windows authentication is SQL Server's alone (<c>IDbProvider.SupportsIntegratedAuth</c>), so the
    /// entry has to arrive and leave with the engine. Offering it on Postgres would be a setting that
    /// silently fails to connect; leaving it selected after a switch away would save a kind Npgsql cannot
    /// use.
    /// </summary>
    [Fact]
    public Task Windows_authentication_arrives_and_leaves_with_the_engine() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Dialog();
        var credentials = ConnectionEditorProbe.Combo(dialog, "CredentialKindBox");

        Assert.DoesNotContain(Integrated, ConnectionEditorProbe.Items(dialog, "CredentialKindBox"));

        ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex = 1;
        var onSqlServer = ConnectionEditorProbe.Items(dialog, "CredentialKindBox");
        Assert.Contains(Integrated, onSqlServer);
        // The two universal kinds stay first and in order — the dropdown's default is its first entry, so
        // an engine-specific kind reaching the top would change what a new connection saves.
        Assert.Equal(new[] { "Stored password", "Prompt each time (not saved)" }, onSqlServer.Take(2));

        // Select it, then switch back: the kind cannot survive, so the selection has to land on something
        // this engine does offer rather than dangle at an index that no longer exists.
        credentials.SelectedIndex = onSqlServer.ToList().IndexOf(Integrated);
        Assert.True(dialog.FindControl<TextBlock>("IntegratedHint")!.IsVisible);
        Assert.False(dialog.FindControl<TextBox>("PasswordBox")!.IsVisible);

        ConnectionEditorProbe.Combo(dialog, "ProviderBox").SelectedIndex = 0;

        Assert.DoesNotContain(Integrated, ConnectionEditorProbe.Items(dialog, "CredentialKindBox"));
        Assert.Equal(0, credentials.SelectedIndex);
        Assert.False(dialog.FindControl<TextBlock>("IntegratedHint")!.IsVisible);
        // And the password row is back, because the stored-password kind is the one that has one.
        Assert.True(dialog.FindControl<TextBox>("PasswordBox")!.IsVisible);
    });

    // ---- The advisory hint -----------------------------------------------------------------------

    /// <summary>
    /// The hint is advisory rather than a gate (Save never consults it), so the only thing that makes it
    /// worth anything is that it is <em>live</em>: it names what is missing on the selected engine and
    /// clears as the boxes are filled. A hint computed once at construction would go on naming a field the
    /// user has already typed into.
    /// </summary>
    [Fact]
    public Task The_hint_names_the_empty_required_fields_and_clears_as_they_are_filled() => _ui.Run(() =>
    {
        var dialog = ConnectionEditorProbe.Show();
        try
        {
            var hint = dialog.FindControl<TextBlock>("ValidationHint")!;
            Assert.True(hint.IsVisible);
            Assert.Contains("Database is required.", hint.Text);
            Assert.Contains("User is required.", hint.Text);

            ConnectionEditorProbe.Type(dialog, "Database", "sales");
            Assert.True(hint.IsVisible);
            Assert.DoesNotContain("Database is required.", hint.Text);

            ConnectionEditorProbe.Type(dialog, "User", "app");
            Assert.False(hint.IsVisible);
            Assert.Equal("", hint.Text);

            // The other rule: a Number field that does not parse. Typed over the default rather than
            // appended, so what is asserted is the whole box's content and not "5432fifteen".
            ConnectionEditorProbe.Type(dialog, "Port", "fifteen-thirty-three");
            Assert.True(hint.IsVisible);
            Assert.Contains("Port must be a whole number.", hint.Text);
        }
        finally { ConnectionEditorProbe.Close(dialog); }
    });

    /// <summary>
    /// Choosing Windows authentication has to re-run the hint, not merely swap the password row: the OS
    /// identity <em>is</em> the login, so a demanded User box would make the kind impossible to save on a
    /// connection that has no user name — and the complaint would name a field the dialog is simultaneously
    /// telling the user to ignore.
    /// </summary>
    [Fact]
    public Task Windows_authentication_stops_the_hint_demanding_a_user_name() => _ui.Run(() =>
    {
        // A saved SQL Server connection with no user name — the shape a Windows-authenticated connection
        // has, and the one the hint used to complain about for ever.
        var dialog = ConnectionEditorProbe.Dialog(Saved("sqlserver", port: 1433) with { User = "" });
        var hint = dialog.FindControl<TextBlock>("ValidationHint")!;
        Assert.True(hint.IsVisible);
        Assert.Contains("User is required.", hint.Text);

        var items = ConnectionEditorProbe.Items(dialog, "CredentialKindBox").ToList();
        ConnectionEditorProbe.Combo(dialog, "CredentialKindBox").SelectedIndex = items.IndexOf(Integrated);

        Assert.False(hint.IsVisible);
        // The password row goes with it — integrated authentication resolves no secret at all, so a
        // password box there would collect one that nothing reads (§1.1).
        Assert.False(dialog.FindControl<TextBox>("PasswordBox")!.IsVisible);
    });

    private static ConnectionInfo Saved(string providerId, int port) => new()
    {
        Id = Guid.NewGuid(),
        Name = "saved",
        ProviderId = providerId,
        Host = "db.example.test",
        Port = port,
        Database = "sales",
        User = "app",
    };
}

/// <summary>
/// How a realized <see cref="ConnectionDialog"/> is read and driven from a test. Shared with
/// <see cref="ChoiceFieldTests"/> rather than copied — the <c>{Key}Box</c> lookup is the one thing every
/// test of this dialog needs, and two copies of it would drift the moment the naming convention moved.
/// </summary>
internal static class ConnectionEditorProbe
{
    /// <summary>A dialog over the app's real registry, so the engine list under test is the one this build
    /// composed. The test delegate is never invoked from here: nothing presses Test.</summary>
    public static ConnectionDialog Dialog(ConnectionInfo? existing = null)
        => new(existing, null, (_, _, _) => Task.FromResult(false), providers: new ProviderRegistry());

    /// <summary>
    /// The same dialog, shown and laid out — which only the tests that <see cref="Type"/> need, because
    /// text input has to reach a focused presenter (§4.5). Everything else here is readable off the
    /// constructor's own logical tree.
    /// </summary>
    public static ConnectionDialog Show(ConnectionInfo? existing = null)
    {
        var dialog = Dialog(existing);
        // Room for every row: the fields host is a stack of Auto-height rows, and a window too short to
        // lay them all out is a window whose last box has no presenter to type into.
        dialog.Width = 560;
        dialog.Height = 900;
        dialog.Show();
        Pump(dialog);
        return dialog;
    }

    public static void Close(ConnectionDialog dialog)
    {
        try { dialog.Close(); } catch { /* already closing */ }
    }

    private static void Pump(ConnectionDialog dialog)
    {
        for (var i = 0; i < 2; i++)
        {
            dialog.UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// The editor built for one provider-declared field, found by the <c>{Key}Box</c> name the dialog
    /// stamps on every code-built row (§4.5). The logical tree suffices — the rows are built in the
    /// constructor and in the engine-switch handler, so a reader needs no layout pass and no window.
    /// </summary>
    public static T Editor<T>(ConnectionDialog dialog, string key) where T : Control
    {
        // Single, not First: a rebuild that appended rather than replaced would leave two controls under
        // the same name, and First would happily read the stale one.
        var control = Assert.Single(Named(dialog, key + "Box"));
        // Asserted as a type rather than cast, so a regression to the wrong control reads as "this field
        // is a TextBox" instead of as a null-reference further down.
        return Assert.IsType<T>(control);
    }

    /// <summary>
    /// Type <paramref name="text"/> into a provider-declared field the way the user does: focus it, select
    /// whatever is there, and send real text input, so the dialog's own <c>TextChanged</c> handler runs and
    /// the model is written through the control.
    /// <para>
    /// Assigning <c>TextBox.Text</c> instead does <b>not</b> work, and silently: Avalonia 12.1 raises
    /// <c>TextChanged</c> from the edit path, not from the property, so a programmatic assignment updates
    /// the box and nothing else. A test written that way asserts the box against itself while the model
    /// under it never moves — measured, not assumed (§4.5).
    /// </para>
    /// </summary>
    public static void Type(ConnectionDialog dialog, string key, string text)
    {
        var box = Editor<TextBox>(dialog, key);
        box.Focus();
        // Asserted before typing: without focus the input goes nowhere and every assertion below would be
        // about a box nobody edited.
        Assert.True(box.IsFocused, $"{key}Box never took focus, so nothing was typed into it.");
        box.SelectAll();
        dialog.KeyTextInput(text);
        Pump(dialog);
    }

    /// <summary>The provider-declared field keys currently on the form, in row order. The password is not
    /// among them by design — it belongs to the secret store, so the model excludes it and the dialog's own
    /// box owns it (§1.1).</summary>
    public static IReadOnlyList<string> FieldKeys(ConnectionDialog dialog)
        => dialog.GetLogicalDescendants()
            .OfType<Control>()
            .Where(c => c.Name is { } n && n.EndsWith("Box", StringComparison.Ordinal)
                        && c is TextBox or CheckBox or ComboBox)
            .Select(c => c.Name![..^3])
            // The dialog's own hand-written boxes are not provider fields.
            .Where(key => key is not ("Name" or "Password" or "Env" or "EnvColor" or "ConfirmWrites"
                or "Provider" or "CredentialKind" or "Tls"))
            .ToList();

    public static ComboBox Combo(ConnectionDialog dialog, string name)
        => Assert.IsType<ComboBox>(Assert.Single(Named(dialog, name)));

    /// <summary>A dropdown's labels. The engine and credential pickers hold <see cref="ComboBoxItem"/>s
    /// built in code, so the label is the item's Content rather than the item itself.</summary>
    public static IReadOnlyList<string> Items(ConnectionDialog dialog, string name)
        => Combo(dialog, name).Items.Select(i => i switch
        {
            ComboBoxItem item => item.Content as string ?? "",
            string s => s,
            _ => i?.ToString() ?? "",
        }).ToList();

    private static IReadOnlyList<Control> Named(ConnectionDialog dialog, string name)
        => dialog.GetLogicalDescendants().OfType<Control>().Where(c => c.Name == name).ToList();
}
