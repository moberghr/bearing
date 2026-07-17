using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Squirrel.App.Views;

/// <summary>Minimal one-line text prompt; returns the entered string via ShowDialog, or null if cancelled.</summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog() : this("New name", "") { }

    public TextPromptDialog(string prompt, string initial)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.SelectAll(); InputBox.Focus(); };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Accept(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Close(null); }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Accept();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void Accept()
    {
        var text = InputBox.Text?.Trim();
        Close(string.IsNullOrEmpty(text) ? null : text);
    }
}
