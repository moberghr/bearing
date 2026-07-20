using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Squirrel.App.Views;

/// <summary>
/// Small "About" window: app name, tagline, and the build version (from the assembly's
/// informational version, which is fed by <c>&lt;Version&gt;</c> in Directory.Build.props).
/// One at a time so repeated menu clicks can't stack windows.
/// </summary>
public sealed class AboutDialog : Window
{
    private static bool _open;

    private AboutDialog()
    {
        Title = "About Squirrel";
        Width = 360;
        Height = 200;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = new TextBlock
        {
            Text = "Squirrel",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        };
        var tagline = new TextBlock
        {
            Text = "A fast SQL query tool & script manager.",
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var version = new TextBlock
        {
            Text = $"Version {Version}",
            Opacity = 0.7,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var close = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { name, tagline, version, close },
        };
    }

    /// <summary>The build version, without any "+&lt;git-hash&gt;" build metadata suffix.</summary>
    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info))
                info = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            var value = info ?? "unknown";
            var plus = value.IndexOf('+');
            return plus >= 0 ? value[..plus] : value;
        }
    }

    /// <summary>Show the dialog (owned by <paramref name="owner"/> when available), unless one is already up.</summary>
    public static void Open(Window? owner)
    {
        if (_open) return;
        _open = true;
        var dlg = new AboutDialog();
        dlg.Closed += (_, _) => _open = false;
        if (owner is not null && owner.IsVisible) dlg.Show(owner);
        else dlg.Show();
    }
}
