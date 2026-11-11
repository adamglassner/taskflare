using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Taskflare.Services;
using Taskflare.Views;

namespace Taskflare;

/// <summary>The Settings page: appearance, behaviour, the Muse AI connector, your data and About.</summary>
public partial class MainWindow
{
    private StackPanel _museCard;

    public void ShowSettings()
    {
        CommitDetailText();
        HideDetail();
        _view = "settings";
        NavMyDay.IsChecked = NavImportant.IsChecked = NavPlanned.IsChecked = NavMuse.IsChecked = NavTasks.IsChecked = false;
        foreach (var l in TaskStore.Lists) l.IsActive = false;
        BuildSettings();
        SettingsPage.Visibility = Visibility.Visible;
    }

    private void BuildSettings()
    {
        var host = SettingsHost;
        host.Children.Clear();
        var s = SettingsService.Current;

        var title = Ui.Text("Settings", "Text", 26, FontWeights.Bold);
        title.Margin = new Thickness(0, 0, 0, 14);
        host.Children.Add(title);

        // ---------- Appearance ----------
        host.Children.Add(Section("Appearance"));
        var appearance = Card();
        appearance.Children.Add(Ui.Text("Theme", "Text", 14, FontWeights.SemiBold));
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
        foreach (var (mode, text) in new[] { (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark"), (ThemeMode.System, "Use my Windows setting") })
        {
            var rb = new RadioButton { Content = text, GroupName = "theme", IsChecked = s.Theme == mode, Margin = new Thickness(0, 4, 22, 4) };
            var m = mode;
            rb.Checked += (_, _) => { s.Theme = m; SettingsService.Commit(); };
            themes.Children.Add(rb);
        }
        appearance.Children.Add(themes);
        appearance.Children.Add(Ui.Text("Accent colour", "Text", 14, FontWeights.SemiBold));
        var accents = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var a in ThemeManager.Accents)
        {
            var c = ThemeManager.IsDark ? a.Dark : a.Light;
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), Style = Ui.S("SwatchButton"),
                Background = new SolidColorBrush(c), ToolTip = a.Name,
            };
            if (s.Accent == a.Name)
                b.Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = Brushes.White, FontSize = 12 };
            var name = a.Name;
            b.Click += (_, _) => { s.Accent = name; SettingsService.Commit(); BuildSettings(); };
            accents.Children.Add(b);
        }
        appearance.Children.Add(accents);
        host.Children.Add(Wrap(appearance));

        // ---------- General ----------
        host.Children.Add(Section("General"));
        var general = Card();
        general.Children.Add(Switch("Understand dates, times, #lists and priorities when adding tasks",
            "Type \"Call mom tomorrow 5pm #Personal p1\" and Taskflare fills in the details, like Todoist.",
            s.SmartQuickAdd, v => s.SmartQuickAdd = v));
        general.Children.Add(Switch("Add new tasks to the top", null, s.AddToTop, v => s.AddToTop = v));
        general.Children.Add(Switch("Ask before deleting a task", null, s.ConfirmDelete, v => s.ConfirmDelete = v));
        general.Children.Add(Switch("Play a sound for reminders and finished tasks", null, s.PlaySound, v => s.PlaySound = v));
        general.Children.Add(Switch("Remind me at the due time when a task has one", null, s.RemindAtDueTime, v => s.RemindAtDueTime = v));
        general.Children.Add(Switch("Keep running in the notification area when I close the window",
            "So reminders pop up and Muse can still add tasks.", s.KeepRunningInTray, v =>
            {
                s.KeepRunningInTray = v;
                if (v) TrayService.Show(); else TrayService.Hide();
            }));
        general.Children.Add(Switch("Start Taskflare when I sign in to Windows", "It starts quietly in the notification area.",
            s.StartWithWindows, v => { s.StartWithWindows = v; StartupService.Apply(v); }));
        host.Children.Add(Wrap(general));

        // ---------- Muse ----------
        host.Children.Add(Section("Muse AI"));
        _museCard = Card();
        host.Children.Add(Wrap(_museCard));
        RefreshMuseCard();

        // ---------- Data ----------
        host.Children.Add(Section("Your data"));
        var data = Card();
        var total = TaskStore.Tasks.Count();
        data.Children.Add(Ui.Text($"{total} task{(total == 1 ? "" : "s")} in {TaskStore.Lists.Count + 1} lists. There's no limit — add as many as you like. " +
                                  "Everything is saved on this PC in " + SettingsService.Folder + ", with a backup each day.", "TextSecondary", 13));
        var dataButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 2) };
        dataButtons.Children.Add(Btn("Export…", "", () =>
        {
            var dlg = new SaveFileDialog { FileName = $"Taskflare-{DateTime.Today:yyyy-MM-dd}.json", Filter = "Taskflare backup (*.json)|*.json" };
            if (dlg.ShowDialog(this) == true) TaskStore.Export(dlg.FileName);
        }));
        dataButtons.Children.Add(Btn("Import…", "", () =>
        {
            var dlg = new OpenFileDialog { Filter = "Taskflare backup (*.json)|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var (added, updated) = TaskStore.Import(dlg.FileName);
                Confirm.Info(this, "Import finished", $"{added} added, {updated} updated. Tasks already here were kept unless the file had a newer version.");
                BuildSettings();
            }
            catch (Exception ex)
            {
                Confirm.Info(this, "Couldn't import that file", ex.Message);
            }
        }));
        dataButtons.Children.Add(Btn("Open data folder", "", () =>
        {
            Directory.CreateDirectory(SettingsService.Folder);
            try { Process.Start(new ProcessStartInfo(SettingsService.Folder) { UseShellExecute = true }); } catch { }
        }));
        data.Children.Add(dataButtons);
        data.Children.Add(Hint("Syncing with the Taskflare mobile app is coming later. Every task already has the ids and timestamps it needs to sync."));
        host.Children.Add(Wrap(data));

        // ---------- About ----------
        host.Children.Add(Section("About"));
        var about = Card();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = (ImageSource)new ImageSourceConverter().ConvertFromString("pack://application:,,,/Assets/taskflare-256.png"), Width = 40, Height = 40 });
        var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Ui.Text("Taskflare " + AppInfo.Version, "Text", 16, FontWeights.SemiBold));
        names.Children.Add(Ui.Text("© 2026 Bytehawk Studio · Free and open source (MIT licence)", "TextSecondary", 12.5));
        head.Children.Add(names);
        about.Children.Add(head);
        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        links.Children.Add(Btn("Source code on GitHub", "", () => OpenUrl(AppInfo.RepoUrl)));
        links.Children.Add(Btn("Report a problem", "", () => OpenUrl(AppInfo.RepoUrl + "/issues")));
        about.Children.Add(links);
        var changes = ReadChangelog();
        if (changes != null)
        {
            var h = Ui.Text("What's new", "Text", 14, FontWeights.SemiBold);
            h.Margin = new Thickness(0, 14, 0, 4);
            about.Children.Add(h);
            about.Children.Add(Ui.Text(changes, "TextSecondary", 12.5));
        }
        host.Children.Add(Wrap(about));
    }

    /// <summary>The Muse AI card. Rebuilt when the connector or tunnel status changes.</summary>
    private void RefreshMuseCard()
    {
        if (_museCard == null || SettingsPage.Visibility != Visibility.Visible && _view != "settings") { if (_view == "muse") RefreshView(); return; }
        var c = _museCard;
        c.Children.Clear();
        var s = SettingsService.Current;

        c.Children.Add(Ui.Text("Let Meta's Muse add tasks for you. Turn on the connector, then add Taskflare to Muse as a custom connector. " +
                               "After that you can say things like \"add eggs and milk to my Shopping list in Taskflare\" or \"what's on my to-do list today?\".",
                               "TextSecondary", 13));
        c.Children.Add(Switch("Turn on the Muse connector", null, s.MuseEnabled, async v =>
        {
            s.MuseEnabled = v;
            SettingsService.Save();
            if (v) await StartMuseAsync(); else await StopMuseAsync();
            RefreshMuseCard();
        }, save: false));

        if (s.MuseEnabled)
        {
            // Status
            var status = MuseServer.IsRunning
                ? $"Running on this PC at {MuseServer.LocalBase}" + (MuseServer.RequestCount > 0 ? $" · {MuseServer.RequestCount} request{(MuseServer.RequestCount == 1 ? "" : "s")}, last {MuseServer.LastRequest:t}" : " · waiting for Muse")
                : MuseServer.Error ?? "Starting…";
            var st = Ui.Text(status, MuseServer.IsRunning ? "Success" : MuseServer.Error != null ? "Danger" : "TextSecondary", 13);
            st.Margin = new Thickness(0, 4, 0, 8);
            c.Children.Add(st);

            // Step 1: public link
            c.Children.Add(Step("1", "Give the connector a public link",
                "Muse runs in Meta's cloud, so it can't reach your PC directly. Taskflare uses Cloudflare's free quick tunnel (cloudflared) " +
                "to give it a private https link. No account is needed. The link changes each time the tunnel starts, so paste the new one into Muse if it changes."));
            var tunnelRow = new WrapPanel { Margin = new Thickness(28, 6, 0, 0) };
            var exe = TunnelService.Find();
            if (TunnelService.IsDownloading)
            {
                tunnelRow.Children.Add(Ui.Text("Downloading cloudflared…", "TextSecondary", 13));
            }
            else if (exe == null)
            {
                tunnelRow.Children.Add(Btn("Download cloudflared (free, from Cloudflare)", "", async () =>
                {
                    var path = await TunnelService.DownloadAsync();
                    if (path != null && MuseServer.IsRunning) TunnelService.Start(s.MusePort);
                    RefreshMuseCard();
                }, accent: true));
            }
            else if (TunnelService.IsRunning)
            {
                tunnelRow.Children.Add(Btn("Stop public link", "", () => { TunnelService.Stop(); RefreshMuseCard(); }));
            }
            else
            {
                tunnelRow.Children.Add(Btn("Start public link", "", () => { TunnelService.Start(s.MusePort); RefreshMuseCard(); }, accent: true, enabled: MuseServer.IsRunning));
            }
            c.Children.Add(tunnelRow);
            var ts = Ui.Text(TunnelService.PublicUrl != null ? "Public link: " + TunnelService.PublicUrl : TunnelService.Status, TunnelService.PublicUrl != null ? "Success" : "TextSecondary", 12.5);
            ts.Margin = new Thickness(28, 6, 0, 0);
            if (TunnelService.PublicUrl != null || TunnelService.Status != "Off") c.Children.Add(ts);
            var auto = Switch("Start the public link whenever Taskflare starts", null, s.MuseAutoTunnel, v => s.MuseAutoTunnel = v);
            auto.Margin = new Thickness(28, 6, 0, 6);
            c.Children.Add(auto);

            // Step 2: add to Muse
            c.Children.Add(Step("2", "Add Taskflare to Muse",
                "In Muse, open Settings › Connectors › Add custom connector and paste the connector link. " +
                "If your Muse doesn't have that option, use \"Copy setup message for Muse\" and send it to Muse in a chat instead; Muse can build its own connector from Taskflare's API."));
            var baseUrl = TunnelService.PublicUrl ?? MuseServer.LocalBase;
            var mcpUrl = $"{baseUrl}/mcp/{s.MuseKey}";
            var apiUrl = $"{baseUrl}/api/{s.MuseKey}";
            var linkBox = new TextBox { Style = Ui.S("FieldBox"), Text = mcpUrl, IsReadOnly = true, Margin = new Thickness(28, 8, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
            c.Children.Add(linkBox);
            if (TunnelService.PublicUrl == null)
            {
                var warn = Hint("This link only works on this PC. Start the public link above so Muse can reach it.");
                warn.Margin = new Thickness(28, 4, 0, 0);
                c.Children.Add(warn);
            }
            var copyRow = new WrapPanel { Margin = new Thickness(28, 8, 0, 0) };
            copyRow.Children.Add(Btn("Copy connector link", "", () => CopyText(mcpUrl), accent: true));
            copyRow.Children.Add(Btn("Copy setup message for Muse", "", () => CopyText(
                "Please connect to my Taskflare to-do list so you can add and read my tasks. " +
                $"It's a JSON API at {apiUrl} (open that address to read how it works; the key is already in the link). " +
                $"If you support MCP connectors, the MCP endpoint is {mcpUrl}. " +
                "When I ask you to remember, plan or schedule something, add it to Taskflare.")));
            copyRow.Children.Add(Btn("Copy API link", "", () => CopyText(apiUrl)));
            c.Children.Add(copyRow);

            // Step 3: permissions
            c.Children.Add(Step("3", "Choose what Muse may do", "Muse can always add tasks and read them (so it doesn't add duplicates)."));
            var edits = Switch("Let Muse complete, change and delete tasks", null, s.MuseAllowEdits, v => s.MuseAllowEdits = v);
            edits.Margin = new Thickness(28, 6, 0, 6);
            c.Children.Add(edits);
            var notify = Switch("Show a notification when Muse adds a task", null, s.MuseNotify, v => s.MuseNotify = v);
            notify.Margin = new Thickness(28, 6, 0, 6);
            c.Children.Add(notify);

            // Advanced
            var adv = Ui.Text("Advanced", "Text", 13.5, FontWeights.SemiBold);
            adv.Margin = new Thickness(0, 14, 0, 4);
            c.Children.Add(adv);
            var advRow = new WrapPanel();
            var portBox = new TextBox { Style = Ui.S("FieldBox"), Text = s.MusePort.ToString(), Width = 90, Margin = new Thickness(0, 0, 8, 0), Tag = "Port" };
            advRow.Children.Add(Ui.Text("Port", "TextSecondary", 13));
            ((TextBlock)advRow.Children[0]).Margin = new Thickness(0, 7, 8, 0);
            advRow.Children.Add(portBox);
            advRow.Children.Add(Btn("Apply", "", async () =>
            {
                if (int.TryParse(portBox.Text, out var p) && p >= 1024 && p <= 65535 && p != s.MusePort)
                {
                    s.MusePort = p;
                    SettingsService.Save();
                    bool tunnel = TunnelService.IsRunning;
                    await StartMuseAsync();
                    if (tunnel) TunnelService.Start(p);
                    RefreshMuseCard();
                }
            }));
            advRow.Children.Add(Btn("New secret key", "", async () =>
            {
                if (!Confirm.Ask(this, "Make a new secret key?", "The old connector link stops working. You'll need to paste the new link into Muse.", "Make new key", danger: false)) return;
                s.MuseKey = AppSettings.NewKey();
                SettingsService.Save();
                await Task.Yield();
                RefreshMuseCard();
            }));
            c.Children.Add(advRow);
            var keyHint = Hint("The secret key in the link is what lets Muse in, so only share the link with Muse. " +
                               "Agents that send headers can also use the key as a Bearer token.");
            keyHint.Margin = new Thickness(0, 6, 0, 0);
            c.Children.Add(keyHint);
        }
    }

    // ---------- helpers ----------

    private static TextBlock Section(string text)
    {
        var t = Ui.Text(text, "Text", 16, FontWeights.SemiBold);
        t.Margin = new Thickness(2, 10, 0, 8);
        return t;
    }

    private static StackPanel Card() => new();

    private static Border Wrap(StackPanel content) => new() { Style = Ui.S("SettingsCard"), Child = content };

    private static TextBlock Hint(string text)
    {
        var t = Ui.Text(text, "TextSecondary", 12.5);
        t.Margin = new Thickness(0, 8, 0, 0);
        return t;
    }

    private static FrameworkElement Step(string n, string title, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        var num = new TextBlock { Text = n, FontSize = 11.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        num.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        badge.Child = num;
        g.Children.Add(badge);
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(title, "Text", 14, FontWeights.SemiBold));
        var body = Ui.Text(text, "TextSecondary", 12.5);
        body.Margin = new Thickness(0, 2, 0, 0);
        sp.Children.Add(body);
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        return g;
    }

    private static CheckBox Switch(string text, string hint, bool value, Action<bool> set, bool save = true)
    {
        var content = new StackPanel();
        content.Children.Add(Ui.Text(text, "Text", 14));
        if (hint != null) content.Children.Add(Ui.Text(hint, "TextSecondary", 12.5));
        var cb = new CheckBox { Style = Ui.S("Switch"), Content = content, IsChecked = value };
        cb.Click += (_, _) =>
        {
            set(cb.IsChecked == true);
            if (save) SettingsService.Commit();
        };
        return cb;
    }

    private static Button Btn(string text, string glyph, Action act, bool accent = false, bool enabled = true)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var g = new TextBlock { Text = glyph, Style = Ui.S("Icon"), FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
        sp.Children.Add(g);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Margin = new Thickness(0, 0, 8, 6), IsEnabled = enabled, Padding = new Thickness(12, 5, 14, 5) };
        if (accent) b.Style = Ui.S("AccentButton");
        b.Click += (_, _) => act();
        return b;
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Toast.Pop("Copied", "Copied to the clipboard", text.Length > 90 ? text[..90] + "…" : text, "", "Accent", null, 3);
        }
        catch { }
    }

    private static string ReadChangelog()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int start = lines.FindIndex(l => l.StartsWith("## ", StringComparison.Ordinal));
            if (start < 0) return null;
            int end = lines.FindIndex(start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            if (end < 0) end = lines.Count;
            return string.Join("\n", lines.Skip(start).Take(end - start).Select(l => l.StartsWith("## ") ? l[3..] : l.StartsWith("- ") ? "•  " + l[2..] : l)).Trim();
        }
        catch
        {
            return null;
        }
    }
}
