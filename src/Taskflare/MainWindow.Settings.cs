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

/// <summary>The Settings page: appearance, behaviour, AI assistants, your data and About.</summary>
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
        general.Children.Add(Switch("Play a sound when I check off a task", null, s.CompleteSound, v => { s.CompleteSound = v; BuildSettings(); }));
        if (s.CompleteSound)
        {
            var sounds = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var lbl = Ui.Text("Sound", "TextSecondary", 13);
            lbl.Margin = new Thickness(0, 6, 10, 0);
            sounds.Children.Add(lbl);
            foreach (var name in SoundService.Names)
            {
                var rb = new RadioButton { Content = name, GroupName = "sound", IsChecked = s.CompleteSoundName == name, Margin = new Thickness(0, 4, 18, 4) };
                var n = name;
                rb.Checked += (_, _) => { s.CompleteSoundName = n; SettingsService.Save(); SoundService.Play(n); };
                sounds.Children.Add(rb);
            }
            sounds.Children.Add(Btn("Play", "\uE768", () => SoundService.Play(s.CompleteSoundName)));
            general.Children.Add(sounds);
        }
        general.Children.Add(Switch("Play a sound for reminders", null, s.PlaySound, v => s.PlaySound = v));
        general.Children.Add(Switch("Remind me at the due time when a task has one", null, s.RemindAtDueTime, v => s.RemindAtDueTime = v));
        general.Children.Add(Switch("Keep running in the notification area when I close the window",
            "So reminders pop up and AI assistants can still add tasks.", s.KeepRunningInTray, v =>
            {
                s.KeepRunningInTray = v;
                if (v) TrayService.Show(); else TrayService.Hide();
            }));
        general.Children.Add(Switch("Start Taskflare when I sign in to Windows", "It starts quietly in the notification area.",
            s.StartWithWindows, v => { s.StartWithWindows = v; StartupService.Apply(v); }));
        host.Children.Add(Wrap(general));

        // ---------- AI assistants ----------
        host.Children.Add(Section("AI assistants"));
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

    private string _aiPick = "claude";

    /// <summary>One assistant's set-up steps for the AI assistants card.</summary>
    private sealed record AiGuide(string Id, string Name, string Steps, string Note, bool Works = true, bool UsesApi = false);

    private static readonly AiGuide[] AiGuides =
    {
        new("claude", "Claude",
            "In Claude (claude.ai or the Claude desktop app) open Settings › Connectors, choose Add custom connector, name it Taskflare and paste the link below. Leave the OAuth settings empty. " +
            "Once it's added it also works in the Claude phone apps. Then just ask: \"add call the dentist to my Personal list in Taskflare\".",
            "Works on every Claude plan (the Free plan allows one custom connector)."),
        new("chatgpt", "ChatGPT",
            "In ChatGPT on the web, turn on Developer mode (Settings › Apps & Connectors › Advanced settings). Then create a connector, name it Taskflare, paste the link below, " +
            "choose No authentication and confirm you trust it. In a chat, pick Taskflare from the tools menu.",
            "Needs a paid ChatGPT plan with Developer mode. OpenAI limits which plans can add or change things through custom connectors, so check yours."),
        new("gemini", "Gemini",
            "On gemini.google.com open Settings › Connected Apps, choose Add a custom app under Custom apps and paste the link below, then approve the connection. It then shows in the Gemini phone app too.",
            "Google only offers custom apps on the Google AI Ultra plan (Gemini Spark), for personal accounts, in English, with Keep Activity turned on."),
        new("grok", "Grok",
            "Go to grok.com/connectors (or Settings › Connectors in the Grok app), click New Connector, choose Custom, name it Taskflare and paste the link below.",
            "Custom connectors are on Grok's paid plans."),
        new("muse", "Muse",
            "If Muse shows Settings › Connectors › Add custom connector, paste the link below. If it doesn't have that yet, send Muse the setup message (button below) and it can use Taskflare's API, " +
            "or add the link to Muse Code's MCP servers.",
            "Meta hasn't opened custom connectors to everyone in the Muse app yet."),
        new("siri", "Siri",
            "Make an Apple Shortcut on your iPhone, iPad or Mac:\n" +
            "1.  Open Shortcuts, tap + and name the shortcut \"Add to Taskflare\".\n" +
            "2.  Add the action Dictate Text.\n" +
            "3.  Add Get Contents of URL and paste the Siri link below. Tap the arrow: set Method to POST and Request Body to JSON, then add a Text field called text and set it to Dictated Text.\n" +
            "4.  (Optional) Add Show Result so Siri reads back what was added.\n" +
            "Then say \"Hey Siri, add to Taskflare\" and say the task, like \"call mom tomorrow at 5pm\". Dates, times, #lists and p1 work just like in the add box.",
            "Your PC has to be on with Taskflare running and the public link on.", UsesApi: true),
        new("alexa", "Alexa",
            "Alexa can't connect to Taskflare yet. Amazon only lets developers plug apps into Alexa+ through add-ons built with Amazon's developer tools and certified by Amazon, " +
            "so there's no link you can paste. Taskflare will add Alexa if Amazon opens this up.",
            "Until then, use Siri, or ask one of the other assistants.", Works: false),
        new("ai", "Other",
            "Most AI apps that take an \"MCP\" or \"custom connector\" link can use the link below, for example Perplexity, Le Chat, Copilot Studio, Cursor or Claude Code. " +
            "Apps and automations that call web APIs (Zapier, IFTTT, scripts) can use the API link instead; open it in a browser to see how it works.",
            "Use the secret key only with tools you trust."),
    };

    /// <summary>The AI assistants card. Rebuilt when the connector or tunnel status changes.</summary>
    private void RefreshMuseCard()
    {
        if (_museCard == null || SettingsPage.Visibility != Visibility.Visible && _view != "settings") { if (_view == "muse") RefreshView(); return; }
        var c = _museCard;
        c.Children.Clear();
        var s = SettingsService.Current;

        c.Children.Add(Ui.Text("Let your AI assistant add tasks for you: Claude, ChatGPT, Gemini, Grok, Meta's Muse, or Siri through an Apple Shortcut. " +
                               "Turn on the connector, give it a public link, then add Taskflare to your assistant. After that you can say things like " +
                               "\"add eggs and milk to my Shopping list in Taskflare\" or \"what's on my to-do list today?\". Tasks it adds show which assistant added them.",
                               "TextSecondary", 13));
        c.Children.Add(Switch("Turn on the AI connector", null, s.MuseEnabled, async v =>
        {
            s.MuseEnabled = v;
            SettingsService.Save();
            if (v) await StartMuseAsync(); else await StopMuseAsync();
            RefreshMuseCard();
        }, save: false));

        if (!s.MuseEnabled) return;

        // Status
        var status = MuseServer.IsRunning
            ? $"Running on this PC at {MuseServer.LocalBase}" + (MuseServer.RequestCount > 0 ? $" · {MuseServer.RequestCount} request{(MuseServer.RequestCount == 1 ? "" : "s")}, last {MuseServer.LastRequest:t}" : " · waiting for an assistant")
            : MuseServer.Error ?? "Starting…";
        var st = Ui.Text(status, MuseServer.IsRunning ? "Success" : MuseServer.Error != null ? "Danger" : "TextSecondary", 13);
        st.Margin = new Thickness(0, 4, 0, 8);
        c.Children.Add(st);

        // ---- Step 1: public link ----
        c.Children.Add(Step("1", "Give the connector a public link",
            "AI assistants run in the cloud, so they can't reach your PC directly. Taskflare uses Cloudflare's free quick tunnel (cloudflared) " +
            "to give it a private https link. No account is needed. The link changes each time the tunnel starts, so update it in your assistant if it changes."));
        var tunnelRow = new WrapPanel { Margin = new Thickness(28, 6, 0, 0) };
        var exe = TunnelService.Find();
        if (TunnelService.IsDownloading)
            tunnelRow.Children.Add(Ui.Text("Downloading cloudflared…", "TextSecondary", 13));
        else if (exe == null)
            tunnelRow.Children.Add(Btn("Download cloudflared (free, from Cloudflare)", "\uE896", async () =>
            {
                var path = await TunnelService.DownloadAsync();
                if (path != null && MuseServer.IsRunning) TunnelService.Start(s.MusePort);
                RefreshMuseCard();
            }, accent: true));
        else if (TunnelService.IsRunning)
            tunnelRow.Children.Add(Btn("Stop public link", "\uE71A", () => { TunnelService.Stop(); RefreshMuseCard(); }));
        else
            tunnelRow.Children.Add(Btn("Start public link", "\uE768", () => { TunnelService.Start(s.MusePort); RefreshMuseCard(); }, accent: true, enabled: MuseServer.IsRunning));
        c.Children.Add(tunnelRow);
        var ts = Ui.Text(TunnelService.PublicUrl != null ? "Public link: " + TunnelService.PublicUrl : TunnelService.Status, TunnelService.PublicUrl != null ? "Success" : "TextSecondary", 12.5);
        ts.Margin = new Thickness(28, 6, 0, 0);
        if (TunnelService.PublicUrl != null || TunnelService.Status != "Off") c.Children.Add(ts);
        var auto = Switch("Start the public link whenever Taskflare starts", null, s.MuseAutoTunnel, v => s.MuseAutoTunnel = v);
        auto.Margin = new Thickness(28, 6, 0, 6);
        c.Children.Add(auto);

        // ---- Step 2: pick an assistant ----
        c.Children.Add(Step("2", "Connect your assistant", "Pick yours to see how. Each one gets its own link, so Taskflare knows who added what."));
        var picker = new WrapPanel { Margin = new Thickness(28, 8, 0, 0) };
        foreach (var g in AiGuides)
        {
            var tb = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = g.Name,
                IsChecked = g.Id == _aiPick,
                Style = Ui.S("FlatToggle"),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 13.5,
                MinWidth = 64,
            };
            var id = g.Id;
            tb.Click += (_, _) => { _aiPick = id; RefreshMuseCard(); };
            picker.Children.Add(tb);
        }
        c.Children.Add(picker);

        var guide = AiGuides.FirstOrDefault(x => x.Id == _aiPick) ?? AiGuides[0];
        var baseUrl = TunnelService.PublicUrl ?? MuseServer.LocalBase;
        var mcpUrl = $"{baseUrl}/mcp/{s.MuseKey}/{guide.Id}";
        var apiUrl = $"{baseUrl}/api/{s.MuseKey}?via={guide.Id}";
        var siriUrl = $"{baseUrl}/api/{s.MuseKey}/quick-add?via=siri";

        var box = new Border { Margin = new Thickness(28, 4, 0, 0), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        box.SetResourceReference(Border.BackgroundProperty, "PaneBg");
        box.SetResourceReference(Border.BorderBrushProperty, "Divider");
        var inner = new StackPanel();
        box.Child = inner;
        inner.Children.Add(Ui.Text(guide.Steps, "Text", 13));
        var note = Ui.Text(guide.Note, guide.Works ? "TextSecondary" : "Danger", 12.5);
        note.Margin = new Thickness(0, 6, 0, 0);
        inner.Children.Add(note);

        if (guide.Works)
        {
            var link = guide.UsesApi ? siriUrl : mcpUrl;
            var linkBox = new TextBox { Style = Ui.S("FieldBox"), Text = link, IsReadOnly = true, Margin = new Thickness(0, 10, 0, 0), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
            inner.Children.Add(linkBox);
            if (TunnelService.PublicUrl == null)
            {
                var warn = Hint("This link only works on this PC. Start the public link in step 1 so your assistant can reach it.");
                warn.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                warn.Margin = new Thickness(0, 4, 0, 0);
                inner.Children.Add(warn);
            }
            var copyRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            copyRow.Children.Add(Btn(guide.UsesApi ? "Copy Siri link" : "Copy connector link", "\uE8C8", () => CopyText(link), accent: true));
            if (guide.Id is "muse" or "ai")
            {
                copyRow.Children.Add(Btn("Copy setup message", "\uE8BD", () => CopyText(
                    "Please connect to my Taskflare to-do list so you can add and read my tasks. " +
                    $"It's a JSON API at {apiUrl} (open that address to read how it works; the key is already in the link). " +
                    $"If you support MCP connectors, the MCP endpoint is {mcpUrl}. " +
                    "When I ask you to remember, plan or schedule something, add it to Taskflare.")));
                copyRow.Children.Add(Btn("Copy API link", "\uE71B", () => CopyText(apiUrl)));
            }
            inner.Children.Add(copyRow);
        }
        c.Children.Add(box);

        // ---- Step 3: permissions ----
        c.Children.Add(Step("3", "Choose what assistants may do", "They can always add tasks and read them (so they don't add duplicates)."));
        var edits = Switch("Let assistants complete, change and delete tasks", null, s.MuseAllowEdits, v => s.MuseAllowEdits = v);
        edits.Margin = new Thickness(28, 6, 0, 6);
        c.Children.Add(edits);
        var notify = Switch("Show a notification when an assistant adds a task", null, s.MuseNotify, v => s.MuseNotify = v);
        notify.Margin = new Thickness(28, 6, 0, 6);
        c.Children.Add(notify);

        // ---- Advanced ----
        var adv = Ui.Text("Advanced", "Text", 13.5, FontWeights.SemiBold);
        adv.Margin = new Thickness(0, 14, 0, 4);
        c.Children.Add(adv);
        var advRow = new WrapPanel();
        var portLabel = Ui.Text("Port", "TextSecondary", 13);
        portLabel.Margin = new Thickness(0, 7, 8, 0);
        advRow.Children.Add(portLabel);
        var portBox = new TextBox { Style = Ui.S("FieldBox"), Text = s.MusePort.ToString(), Width = 90, Margin = new Thickness(0, 0, 8, 0), Tag = "Port" };
        advRow.Children.Add(portBox);
        advRow.Children.Add(Btn("Apply", "\uE73E", async () =>
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
        advRow.Children.Add(Btn("New secret key", "\uE72C", async () =>
        {
            if (!Confirm.Ask(this, "Make a new secret key?", "Every assistant's link stops working. You'll need to paste the new links into your assistants.", "Make new key", danger: false)) return;
            s.MuseKey = AppSettings.NewKey();
            SettingsService.Save();
            await Task.Yield();
            RefreshMuseCard();
        }));
        c.Children.Add(advRow);
        var keyHint = Hint("The secret key in the links is what lets assistants in, so only paste them into assistants you trust. " +
                           "Tools that send headers can also use the key as a Bearer token.");
        keyHint.Margin = new Thickness(0, 6, 0, 0);
        c.Children.Add(keyHint);
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
