using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bearing.Core.Data;
using Bearing.Sessions;
using static Bearing.App.Controls.Tokens;

namespace Bearing.App.Views;

/// <summary>What Browse Azure picked: a server, and one of its databases or null for "leave Database as it is".
/// Null from <c>ShowDialog</c> means cancelled.</summary>
public sealed record AzurePick(AzureServer Server, string? Database);

/// <summary>
/// Lists the Azure servers of one engine that the user's <c>az login</c> can see, then the databases on the one
/// picked, so the connection editor's Host and Database can be chosen rather than typed. Visuals and wiring only:
/// what is listed, and how, is <see cref="IAzureCatalog"/>'s (§2.3).
/// </summary>
public sealed class AzureBrowseDialog : Window
{
    private readonly IAzureCatalog _catalog;
    private readonly string _providerId;
    private readonly CancellationTokenSource _closing = new();

    private readonly TextBox _filter;
    private readonly TextBlock _serverStatus;
    private readonly ListBox _servers;
    private readonly TextBlock _databaseStatus;
    private readonly ListBox _databases;
    private readonly Button _use;

    /// <summary>What az answered, or null while it hasn't (still asking, or it failed — the status line then
    /// says which, and the filter must not overwrite that with "none", §1.1).</summary>
    private AzureServerList? _listing;

    /// <summary>Set while the filter rebuilds the server rows: clearing and re-selecting raise SelectionChanged,
    /// which must not cancel and restart the database read for a server that is still the one picked.</summary>
    private bool _rebuilding;

    /// <summary>The database read for the server picked last; replaced on every pick so a slow answer for a server
    /// no longer selected is dropped rather than shown under the wrong one.</summary>
    private CancellationTokenSource? _databaseRead;

    /// <summary>When the last server was picked (<see cref="Environment.TickCount64"/>), or null before the first.
    /// A pick hard on the heels of another — ↑/↓ held in the filter — waits <see cref="PickSettle"/> before asking
    /// az, so stepping through thirty servers starts one az for the one landed on, not thirty. A lone pick asks at
    /// once.</summary>
    private long? _lastPickAt;

    private static readonly TimeSpan PickSettle = TimeSpan.FromMilliseconds(250);

    /// <summary>Each server's row, built once per listing: the filter re-adds these rather than building thousands
    /// of controls on every keystroke in a large tenant.</summary>
    private readonly Dictionary<AzureServer, ListBoxItem> _rows = new();

    public AzureBrowseDialog(IAzureCatalog catalog, string providerId, string engineName)
    {
        _catalog = catalog;
        _providerId = providerId;

        Title = $"Browse Azure — {engineName}";
        Width = 640;
        Height = 560;
        MinWidth = 420;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _filter = new TextBox { Name = "FilterBox", PlaceholderText = "Filter by name, host, resource group, subscription…" };
        _filter.TextChanged += (_, _) => ApplyFilter();
        // The filter drives the list it filters, as the command palette's search box does
        // (FilterableListOverlay): ↑/↓ move the pick while typing carries on, and Enter is the dialog's Use.
        _filter.AddHandler(KeyDownEvent, OnFilterKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _serverStatus = Status("Asking az for servers…", "ServerStatus");
        _servers = new ListBox { Name = "ServerList" };
        _servers.SelectionChanged += (_, _) => OnServerPicked();

        _databaseStatus = Status("Pick a server to see its databases.", "DatabaseStatus");
        _databases = new ListBox { Name = "DatabaseList" };
        _databases.SelectionChanged += (_, _) => UpdateUse();
        _databases.DoubleTapped += (_, _) =>
        {
            if (PickedDatabase() is not null)
            {
                Accept();
            }
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        _use = new Button { Name = "UseButton", Content = "Use", IsDefault = true, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        _use.Click += (_, _) => Accept();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_use);

        // Servers take the room; databases get a fixed share below them, since a server rarely has many.
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,180,Auto"), Margin = new Thickness(16) };
        Place(body, Heading("Servers"), 0);
        Place(body, _filter, 1);
        Place(body, _serverStatus, 2);
        Place(body, Framed(_servers), 3);
        Place(body, Heading("Databases"), 4);
        Place(body, _databaseStatus, 5);
        Place(body, Framed(_databases), 6);
        Place(body, buttons, 7);
        Content = body;

        Opened += async (_, _) =>
        {
            _filter.Focus();
            try
            {
                _listing = await _catalog.ListServersAsync(_providerId, _closing.Token);
                ApplyFilter();
            }
            catch (OperationCanceledException) { /* closed while az was still answering */ }
            catch (Exception ex)
            {
                _serverStatus.Text = "✗ " + SafeErrorText.Of(ex);
            }
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _databaseRead?.Dispose();
        };
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        var step = e.Key switch { Key.Down => 1, Key.Up => -1, _ => 0 };
        if (step == 0 || _servers.ItemCount == 0)
        {
            return;
        }
        _servers.SelectedIndex = Math.Clamp(_servers.SelectedIndex + step, 0, _servers.ItemCount - 1);
        _servers.ScrollIntoView(_servers.SelectedIndex);
        e.Handled = true;
    }

    private void ApplyFilter()
    {
        if (_listing is not { } listing)
        {
            return;
        }
        var all = listing.Servers;
        var shown = all.Where(s => s.Matches(_filter.Text)).ToList();
        // Rows are built here, one ListBoxItem per server with the server on its Tag, rather than through an
        // item template: a swapped ItemsSource under a FuncDataTemplate hung the layout pass headlessly, and
        // the rows are static text anyway.
        var selected = PickedServer();
        _rebuilding = true;
        try
        {
            _servers.Items.Clear();
            foreach (var server in shown)
            {
                if (!_rows.TryGetValue(server, out var item))
                {
                    item = _rows[server] = new ListBoxItem { Content = ServerRow(server), Tag = server };
                }
                _servers.Items.Add(item);
            }
            if (selected is not null && shown.IndexOf(selected) is var index and >= 0)
            {
                _servers.SelectedIndex = index;
            }
        }
        finally
        {
            _rebuilding = false;
        }
        // The pick survived the filter: its databases, and the one chosen among them, stay. It was filtered
        // away: that is a pick change like any other.
        if (selected is not null && PickedServer() is null)
        {
            OnServerPicked();
        }
        _serverStatus.Text = ServerStatusText(listing, shown.Count);
    }

    /// <summary>Says which absence it is — az found nothing in this tenant, or the filter hid what it found —
    /// and says when the list is only the first part of what exists (§1.1).</summary>
    internal static string ServerStatusText(AzureServerList listing, int shown)
    {
        var tenant = listing.Tenant is { } name ? $"the {name} tenant" : "the tenant az is signed into";
        var all = listing.Servers.Count;
        var hostless = listing.WithoutHost;
        if (all == 0 && hostless == 0)
        {
            return $"az found no servers of this engine in {tenant}. Other tenants aren't searched — "
                   + "`az login --tenant <tenant>` to look in another.";
        }
        if (all == 0)
        {
            return $"az found {Servers(hostless)} of this engine in {tenant}, but none reports a host name to connect to.";
        }
        var count = shown == all
            ? $"{Servers(all)} in {tenant}"
            : $"{shown} of {all} servers in {tenant} match";
        if (hostless > 0)
        {
            count += $" ({hostless} more report{(hostless == 1 ? "s" : "")} no host name and {(hostless == 1 ? "isn't" : "aren't")} listed)";
        }
        return listing.Truncated ? count + " — only the first part of a longer list was read." : count;

        static string Servers(int n) => $"{n} server{(n == 1 ? "" : "s")}";
    }

    private async void OnServerPicked()
    {
        if (_rebuilding)
        {
            return;
        }
        UpdateUse();
        _databaseRead?.Cancel();
        _databaseRead?.Dispose();
        _databaseRead = null;
        _databases.Items.Clear();
        if (PickedServer() is not { } server)
        {
            _databaseStatus.Text = "Pick a server to see its databases.";
            return;
        }

        var read = _databaseRead = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _databaseStatus.Text = $"Asking az for the databases on {server.Name}…";
        var now = Environment.TickCount64;
        var rapid = now - _lastPickAt < (long)PickSettle.TotalMilliseconds;
        _lastPickAt = now;
        try
        {
            if (rapid)
            {
                // Throws when the next pick replaces this one, which the catch below already treats as moved on.
                await Task.Delay(PickSettle, read.Token);
            }
            var listed = await _catalog.ListDatabasesAsync(_providerId, server, read.Token);
            if (read.IsCancellationRequested)
            {
                return;
            }
            foreach (var name in listed.Names)
            {
                _databases.Items.Add(new ListBoxItem { Content = name, Tag = name });
            }
            _databaseStatus.Text = listed.Names.Count == 0
                ? "No databases listed. Use keeps the Database field as it is."
                : "Pick one, or Use without one to keep the Database field as it is.";
            if (listed.Truncated)
            {
                _databaseStatus.Text += " Azure has more than these; type a name not listed into the Database field.";
            }
        }
        catch (OperationCanceledException) { /* another server was picked, or the window closed */ }
        catch (Exception ex)
        {
            if (!read.IsCancellationRequested)
            {
                _databaseStatus.Text = "✗ " + SafeErrorText.Of(ex);
            }
        }
    }

    private AzureServer? PickedServer() => (_servers.SelectedItem as ListBoxItem)?.Tag as AzureServer;

    private string? PickedDatabase() => (_databases.SelectedItem as ListBoxItem)?.Tag as string;

    private void UpdateUse() => _use.IsEnabled = PickedServer() is not null;

    private void Accept()
    {
        if (PickedServer() is { } server)
        {
            Close(new AzurePick(server, PickedDatabase()));
        }
    }

    private static Control ServerRow(AzureServer s)
    {
        var row = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = s.Name, FontWeight = FontWeight.SemiBold });
        // Only a state worth stopping for: a stopped server is listed but won't answer a connect.
        if (s.State is { } state && !string.Equals(state, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            title.Children.Add(new TextBlock { Text = state, FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
        }
        // A server with public access off is listed (it is real, and reachable from a network it allows), but a
        // connect from anywhere else fails — say so here, before connecting. Only what Azure reported: how it fails
        // (a name that won't resolve, a refusal from the server) differs by engine and setup, so no cause is named.
        if (s.IsPrivateOnly)
        {
            title.Children.Add(new TextBlock
            {
                Text = "private network only",
                FontSize = 11,
                Foreground = Res("Warn.Amber"),
                VerticalAlignment = VerticalAlignment.Center,
                [ToolTip.TipProperty] = "Azure reports public network access as disabled for this server: it accepts "
                                      + "connections only over private networking (its virtual network or a private "
                                      + "endpoint), so connect from a network that reaches it.",
            });
        }
        row.Children.Add(title);
        row.Children.Add(new TextBlock
        {
            Text = s.Host,
            FontSize = 11,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var where = string.Join(" · ", new[] { s.Subscription, s.ResourceGroup, s.Location, s.AdminLogin is { } admin ? $"admin {admin}" : null }
            .Where(p => !string.IsNullOrEmpty(p)));
        row.Children.Add(new TextBlock
        {
            Text = where,
            FontSize = 11,
            Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return row;
    }

    private static void Place(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static Border Framed(Control list) => new()
    {
        BorderBrush = Res("Border.Control"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Margin = new Thickness(0, 4, 0, 0),
        Child = list,
    };

    private static TextBlock Status(string text, string name) => new()
    {
        Name = name,
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.Bold,
        Opacity = 0.6,
        Margin = new Thickness(0, 8, 0, 4),
    };
}
