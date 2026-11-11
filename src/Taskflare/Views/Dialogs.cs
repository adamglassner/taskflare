using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Taskflare.Models;
using Taskflare.Services;

namespace Taskflare.Views;

internal static class Ui
{
    public static Style S(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string brush = "Text", double size = 14, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (weight != null) t.FontWeight = weight.Value;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static Window MakeDialog(Window owner, string title)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Style = S("DialogWindow"),
        };
        ThemeManager.Attach(w);
        return w;
    }
}

/// <summary>New list / edit list: name, colour and icon.</summary>
public static class ListDialog
{
    public static bool Show(Window owner, TaskList existing, out string name, out string color, out string icon)
    {
        var w = Ui.MakeDialog(owner, existing == null ? "New list" : "Edit list");
        string pickedColor = existing?.Color ?? ListColors.All[TaskStore.Lists.Count % ListColors.All.Length];
        string pickedIcon = existing?.Icon ?? "list";

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), Width = 380 };
        root.Children.Add(Ui.Text(existing == null ? "New list" : "Edit list", "Text", 18, FontWeights.SemiBold));
        root.Children.Add(new Border { Height = 14 });
        var box = new TextBox { Style = Ui.S("FieldBox"), Tag = "List name", Text = existing?.Name ?? "", MinHeight = 36 };
        root.Children.Add(box);

        root.Children.Add(new Border { Height = 14 });
        root.Children.Add(Ui.Text("Colour", "TextSecondary", 12.5));
        var colors = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(colors);
        var swatches = new List<Button>();
        var iconButtons = new List<ToggleButton>();
        foreach (var c in ListColors.All)
        {
            var b = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 10, 6),
                Style = Ui.S("SwatchButton"),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)),
                Tag = c,
                ToolTip = c,
            };
            b.Click += (_, _) => { pickedColor = c; Mark(); };
            swatches.Add(b);
            colors.Children.Add(b);
        }

        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(Ui.Text("Icon", "TextSecondary", 12.5));
        var icons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(icons);
        foreach (var (key, glyph) in ListIcons.All)
        {
            var b = new ToggleButton { Content = glyph, Style = Ui.S("IconToggle"), FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), Margin = new Thickness(0, 0, 6, 6), Tag = key };
            b.Click += (_, _) => { pickedIcon = key; Mark(); };
            iconButtons.Add(b);
            icons.Children.Add(b);
        }

        void Mark()
        {
            foreach (var s in swatches)
            {
                bool on = (string)s.Tag == pickedColor;
                s.Content = on ? new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), Foreground = Brushes.White, FontSize = 12 } : null;
            }
            foreach (var b in iconButtons) b.IsChecked = (string)b.Tag == pickedIcon;
        }
        Mark();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = existing == null ? "Create list" : "Save", IsDefault = true, Style = Ui.S("AccentButton") };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) { box.Focus(); return; }
            w.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        w.Content = root;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        bool result = w.ShowDialog() == true;
        name = box.Text.Trim();
        color = pickedColor;
        icon = pickedIcon;
        return result;
    }
}

/// <summary>A themed yes/no message box.</summary>
public static class Confirm
{
    public static bool Ask(Window owner, string title, string message, string yes = "Delete", bool danger = true)
    {
        var w = Ui.MakeDialog(owner, title);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MaxWidth = 420 };
        root.Children.Add(Ui.Text(title, "Text", 17, FontWeights.SemiBold));
        var msg = Ui.Text(message, "TextSecondary", 13.5);
        msg.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(msg);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = yes, IsDefault = true, Style = Ui.S("AccentButton") };
        if (danger)
        {
            ok.SetResourceReference(Control.BackgroundProperty, "Danger");
            ok.SetResourceReference(Control.BorderBrushProperty, "Danger");
            ok.Foreground = Brushes.White;
        }
        ok.Click += (_, _) => w.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        w.Content = root;
        return w.ShowDialog() == true;
    }

    public static void Info(Window owner, string title, string message)
    {
        var w = Ui.MakeDialog(owner, title);
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MaxWidth = 440 };
        root.Children.Add(Ui.Text(title, "Text", 17, FontWeights.SemiBold));
        var msg = Ui.Text(message, "TextSecondary", 13.5);
        msg.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(msg);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, Style = Ui.S("AccentButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        root.Children.Add(ok);
        w.Content = root;
        w.ShowDialog();
    }
}

/// <summary>
/// A small card in the bottom-right corner of the screen, for reminders and "Muse added a task".
/// Several stack upwards. Reminders stay until you act on them; info cards fade after a few seconds.
/// </summary>
public sealed class Toast : Window
{
    private static readonly List<Toast> Open = new();

    public static bool Muted { get; set; }

    public static Toast Pop(string heading, string title, string detail, string glyph, string glyphBrush, IEnumerable<(string Text, Action Act, bool Accent)> actions, int autoCloseSeconds = 0, bool sound = false)
    {
        if (Muted) return null;
        var t = new Toast(heading, title, detail, glyph, glyphBrush, actions, autoCloseSeconds);
        Open.Add(t);
        t.Show();
        Arrange();
        if (sound && SettingsService.Current.PlaySound)
            try { SystemSounds.Asterisk.Play(); } catch { }
        return t;
    }

    public static void CloseAll()
    {
        foreach (var t in Open.ToList()) t.Close();
    }

    private Toast(string heading, string title, string detail, string glyph, string glyphBrush, IEnumerable<(string Text, Action Act, bool Accent)> actions, int autoCloseSeconds)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(10) };
        card.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        card.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.3 };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, glyphBrush);
        g.Children.Add(icon);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        var h = Ui.Text(heading, "TextSecondary", 12);
        body.Children.Add(h);
        var tt = Ui.Text(title, "Text", 15, FontWeights.SemiBold);
        tt.Margin = new Thickness(0, 2, 0, 0);
        body.Children.Add(tt);
        if (!string.IsNullOrEmpty(detail))
        {
            var d = Ui.Text(detail, "TextSecondary", 12.5);
            d.Margin = new Thickness(0, 2, 0, 0);
            body.Children.Add(d);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (text, act, accent) in actions ?? Array.Empty<(string, Action, bool)>())
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), MinWidth = 70, Padding = new Thickness(12, 4, 12, 4), MinHeight = 30, FontSize = 13 };
            if (accent) b.Style = Ui.S("AccentButton");
            b.Click += (_, _) => { act?.Invoke(); Close(); };
            row.Children.Add(b);
        }
        if (row.Children.Count > 0) body.Children.Add(row);
        g.Children.Add(body);

        var close = new Button { Content = "", Style = Ui.S("IconButton"), Width = 26, Height = 26, FontSize = 10, VerticalAlignment = VerticalAlignment.Top };
        close.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        g.Children.Add(close);

        card.Child = g;
        Content = card;
        TextElement.SetForeground(card, (Brush)Application.Current.FindResource("Text"));

        if (autoCloseSeconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoCloseSeconds) };
            timer.Tick += (_, _) => { timer.Stop(); if (!IsMouseOver) Close(); else timer.Start(); };
            timer.Start();
        }
        Closed += (_, _) => { Open.Remove(this); Arrange(); };
        SizeChanged += (_, _) => Arrange();
    }

    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom;
        foreach (var t in Open.ToList())
        {
            var hgt = double.IsNaN(t.ActualHeight) || t.ActualHeight <= 0 ? 140 : t.ActualHeight;
            t.Left = area.Right - t.Width;
            t.Top = bottom - hgt;
            bottom -= hgt - 6;
        }
    }
}
