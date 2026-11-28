using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BandcampBenrify.Infrastructure;

/// <summary>キューの名前などを 1 行入れてもらう小さなダイアログ。</summary>
public static class NamePrompt
{
    public const int MaxLength = 60;

    /// <summary>入れた名前 (前後の空白は除く)。キャンセルや空のときは null。</summary>
    public static string? Ask(string title, string label, string initial)
    {
        var owner = Application.Current?.MainWindow;
        var box = new TextBox { Text = initial, MaxLength = MaxLength, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Padding = new Thickness(14, 5, 14, 5) };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 88, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title + " - Bandcamp Benrify",
            Content = panel,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = owner,
            FontFamily = new FontFamily("Yu Gothic UI, Segoe UI"),
            FontSize = 13,
        };
        if (Application.Current?.TryFindResource("BgBrush") is Brush bg)
        {
            window.Background = bg;
        }

        if (Application.Current?.TryFindResource("TextBrush") is Brush fg)
        {
            window.Foreground = fg;
        }

        void UpdateOk() => ok.IsEnabled = box.Text.Trim().Length > 0;
        box.TextChanged += (_, _) => UpdateOk();
        UpdateOk();
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            Keyboard.Focus(box);
        };

        return window.ShowDialog() == true && box.Text.Trim() is { Length: > 0 } name ? name : null;
    }
}
