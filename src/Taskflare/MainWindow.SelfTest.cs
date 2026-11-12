using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Taskflare.Models;
using Taskflare.Services;

namespace Taskflare;

/// <summary>
/// Automated end-to-end test, run by the build with:  Taskflare.exe --self-test out.txt [screenshot-folder]
/// Checks the quick-add parser, the task store (repeats, save and reload), the AI connector over real HTTP
/// (MCP and REST), then drives the window in Light and Dark and saves screenshots of every view.
/// Data goes to a temporary folder, never the user's own.
/// </summary>
public partial class MainWindow
{
    private int _testFails;

    private sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new TestFailure(message);
    }

    private static void AreEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new TestFailure($"{what}: expected \"{expected}\", got \"{actual}\"");
    }

    private async Task Test(string name, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        App.TestName = name;
        string line;
        try
        {
            await body();
            line = $"PASS {name} ({sw.ElapsedMilliseconds} ms)";
        }
        catch (Exception ex)
        {
            _testFails++;
            var msg = ex is TestFailure ? ex.Message : ex.ToString();
            line = $"FAIL {name}: {msg.Replace("\r", " ").Replace("\n", " ")}";
        }
        try { File.AppendAllText(App.SelfTestOutput, line + "\r\n"); } catch { }
    }

    private async Task Settle(int ms = 250)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private async Task Shot(string name)
    {
        if (App.SelfTestShots == null) return;
        await Settle(350);
        Directory.CreateDirectory(App.SelfTestShots);
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("WindowBg"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(App.SelfTestShots, name + ".png"));
        enc.Save(fs);
    }

    private async Task RunSelfTest()
    {
        Width = 1180;
        Height = 760;
        Left = 20;
        Top = 20;
        File.WriteAllText(App.SelfTestOutput, $"Taskflare {AppInfo.Version} self-test, {DateTime.Now:u}\r\n");

        // ---------------- parser ----------------
        var now = new DateTime(2026, 11, 11, 10, 0, 0); // a Wednesday
        var lists = new[] { new TaskList { Name = "Personal" }, new TaskList { Name = "Work" }, new TaskList { Name = "Grocery list" } };

        await Test("Quick add: tomorrow 5pm #list p1", async () =>
        {
            var p = QuickAddParser.Parse("Call mom tomorrow at 5pm #Personal p1", now, lists);
            AreEqual("Call mom", p.Title, "title");
            AreEqual(new DateTime(2026, 11, 12), p.Due, "due");
            AreEqual(new TimeSpan(17, 0, 0), p.Time, "time");
            AreEqual("Personal", p.List?.Name, "list");
            AreEqual(Priority.High, p.Priority, "priority");
            await Task.CompletedTask;
        });
        await Test("Quick add: weekday and !!", async () =>
        {
            var p = QuickAddParser.Parse("Submit report fri !!", now, lists);
            AreEqual("Submit report", p.Title, "title");
            AreEqual(new DateTime(2026, 11, 13), p.Due, "due");
            AreEqual(Priority.Medium, p.Priority, "priority");
            await Task.CompletedTask;
        });
        await Test("Quick add: month day and time", async () =>
        {
            var p = QuickAddParser.Parse("Dentist nov 20 2:30pm", now, lists);
            AreEqual("Dentist", p.Title, "title");
            AreEqual(new DateTime(2026, 11, 20), p.Due, "due");
            AreEqual(new TimeSpan(14, 30, 0), p.Time, "time");
            await Task.CompletedTask;
        });
        await Test("Quick add: every monday", async () =>
        {
            var p = QuickAddParser.Parse("Water plants every monday", now, lists);
            AreEqual("Water plants", p.Title, "title");
            AreEqual(Repeat.Weekly, p.Repeat, "repeat");
            AreEqual(new DateTime(2026, 11, 16), p.Due, "due");
            await Task.CompletedTask;
        });
        await Test("Quick add: tonight, star, label, in N days, list with spaces", async () =>
        {
            var p = QuickAddParser.Parse("Gym tonight *", now, lists);
            AreEqual("Gym", p.Title, "title");
            AreEqual(new TimeSpan(20, 0, 0), p.Time, "tonight time");
            Assert(p.Important, "star should mark important");
            var q = QuickAddParser.Parse("Read @book in 3 days", now, lists);
            AreEqual("Read", q.Title, "title 2");
            AreEqual(new DateTime(2026, 11, 14), q.Due, "in 3 days");
            AreEqual("book", q.Labels.FirstOrDefault(), "label");
            var r = QuickAddParser.Parse("Eggs #Grocery list", now, lists);
            AreEqual("Eggs", r.Title, "title 3");
            AreEqual("Grocery list", r.List?.Name, "list with a space");
            await Task.CompletedTask;
        });
        await Test("Quick add: leaves ordinary text alone", async () =>
        {
            var p = QuickAddParser.Parse("Buy 2 apples and a pear", now, lists);
            AreEqual("Buy 2 apples and a pear", p.Title, "title");
            Assert(p.Due == null && p.Time == null, "no date or time expected");
            var q = QuickAddParser.Parse("Pay rent 12/1", now, lists);
            AreEqual(new DateTime(2026, 12, 1), q.Due, "slash date");
            var r = QuickAddParser.Parse("Meeting at 3", now, lists);
            AreEqual(new TimeSpan(15, 0, 0), r.Time, "at 3 = 3pm");
            var s = QuickAddParser.Parse("Plain", now, lists, smart: false);
            AreEqual("Plain", s.Title, "smart off");
            await Task.CompletedTask;
        });
        await Test("API date and time words", async () =>
        {
            AreEqual(new DateTime(2026, 11, 20), QuickAddParser.ParseDate("2026-11-20", now), "iso");
            AreEqual(new DateTime(2026, 11, 12), QuickAddParser.ParseDate("tomorrow", now), "tomorrow");
            AreEqual(new TimeSpan(17, 30, 0), QuickAddParser.ParseTime("17:30", now), "24h");
            AreEqual(new TimeSpan(17, 0, 0), QuickAddParser.ParseTime("5pm", now), "5pm");
            AreEqual(new DateTime(2026, 11, 12, 9, 0, 0), QuickAddParser.ParseDateTime("tomorrow 9am", now), "datetime");
            await Task.CompletedTask;
        });

        // ---------------- store ----------------
        await Test("Store: add, repeat, delete, save and reload", async () =>
        {
            TaskStore.Reset(new TaskData());
            var work = TaskStore.AddList("Work", "#3B82F6", "work");
            var a = TaskStore.Add(new TaskItem { Title = "Weekly report", ListId = work.Id, Due = DateTime.Today, Repeat = Repeat.Weekly });
            TaskStore.SetCompleted(a, true);
            var next = TaskStore.Tasks.FirstOrDefault(t => t.Title == "Weekly report" && !t.Completed);
            Assert(next != null, "completing a repeating task should add the next one");
            AreEqual(DateTime.Today.AddDays(7), next.Due, "next due");
            AreEqual(work.Id, next.ListId, "next list");
            var b = TaskStore.Add(new TaskItem { Title = "Throwaway" });
            TaskStore.Delete(b);
            Assert(!TaskStore.Tasks.Contains(b), "deleted task hidden");
            for (int i = 0; i < 2000; i++) TaskStore.Data.Tasks.Add(new TaskItem { Title = "Bulk " + i, Order = 10 + i });
            TaskStore.SaveNow();
            TaskStore.Load();
            AreEqual(2003, TaskStore.Data.Tasks.Count, "tasks after reload (incl. the deleted tombstone)");
            Assert(TaskStore.Data.Tasks.Any(t => t.Deleted && t.Title == "Throwaway"), "tombstone kept for sync");
            AreEqual(1, TaskStore.Lists.Count, "lists after reload");
            await Task.CompletedTask;
        });

        // ---------------- AI connector ----------------
        int port = 48000 + Random.Shared.Next(0, 900);
        SettingsService.Current.MuseKey = "test-key-0123456789abcdef";
        await Test("AI connector: MCP over HTTP", async () =>
        {
            TaskStore.Reset(new TaskData());
            await MuseServer.StartAsync(port);
            Assert(MuseServer.IsRunning, "server should start: " + MuseServer.Error);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var mcp = $"http://localhost:{port}/mcp/{SettingsService.Current.MuseKey}";

            string session = null;
            async Task<JsonNode> Rpc(string method, JsonObject p, int id = 1, string url = null)
            {
                var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };
                var req = new HttpRequestMessage(HttpMethod.Post, url ?? mcp) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.Accept.ParseAdd("application/json");
                req.Headers.Accept.ParseAdd("text/event-stream");
                if (session != null && url == null) req.Headers.Add("Mcp-Session-Id", session);
                var resp = await http.SendAsync(req);
                AreEqual(HttpStatusCode.OK, resp.StatusCode, method + " status");
                if (method == "initialize" && resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) session = ids.FirstOrDefault();
                return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            }

            // ChatGPT names itself "openai-mcp"; Taskflare should label its tasks ChatGPT.
            var init = await Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "openai-mcp", ["version"] = "1" } });
            Assert(!string.IsNullOrEmpty(session), "a session id should be returned");
            AreEqual("2025-06-18", init["result"]?["protocolVersion"]?.GetValue<string>(), "protocol version");
            AreEqual("taskflare", init["result"]?["serverInfo"]?["name"]?.GetValue<string>(), "server name");

            var note = new HttpRequestMessage(HttpMethod.Post, mcp) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json") };
            AreEqual(HttpStatusCode.Accepted, (await http.SendAsync(note)).StatusCode, "notification status");

            var tools = await Rpc("tools/list", null, 2);
            AreEqual(9, (tools["result"]?["tools"] as JsonArray)?.Count ?? 0, "tool count");

            var add = await Rpc("tools/call", new JsonObject
            {
                ["name"] = "add_task",
                ["arguments"] = new JsonObject { ["title"] = "Buy milk", ["list"] = "Groceries", ["due_date"] = "tomorrow", ["priority"] = "high", ["steps"] = new JsonArray("Whole", "Oat") },
            }, 3);
            Assert(add["result"]?["isError"]?.GetValue<bool>() == false, "add_task error: " + add.ToJsonString());
            var milk = TaskStore.Tasks.FirstOrDefault(t => t.Title == "Buy milk");
            Assert(milk != null, "task should be in the store");
            Assert(milk.FromAi, "task should be marked as added by an AI");
            AreEqual("chatgpt", milk.Source, "assistant worked out from the client name");
            AreEqual("ChatGPT", milk.AddedByName, "badge name");
            AreEqual("Groceries", milk.ListName, "list created and used");
            AreEqual(DateTime.Today.AddDays(1), milk.Due, "due tomorrow");
            AreEqual(Priority.High, milk.Priority, "priority");
            AreEqual(2, milk.Steps.Count, "steps");

            var qa = await Rpc("tools/call", new JsonObject { ["name"] = "quick_add", ["arguments"] = new JsonObject { ["text"] = "Call the bank friday 10am p2" } }, 4);
            Assert(qa["result"]?["isError"]?.GetValue<bool>() == false, "quick_add failed");
            Assert(TaskStore.Tasks.Any(t => t.Title == "Call the bank" && t.DueTime == new TimeSpan(10, 0, 0)), "quick_add parsed");

            var bulk = await Rpc("tools/call", new JsonObject
            {
                ["name"] = "add_tasks",
                ["arguments"] = new JsonObject { ["list"] = "Groceries", ["tasks"] = new JsonArray(new JsonObject { ["title"] = "Eggs" }, new JsonObject { ["title"] = "Bread" }) },
            }, 5);
            Assert(bulk["result"]?["isError"]?.GetValue<bool>() == false, "add_tasks failed");

            var list = await Rpc("tools/call", new JsonObject { ["name"] = "list_tasks", ["arguments"] = new JsonObject { ["list"] = "Groceries" } }, 6);
            var listText = list["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            AreEqual(3, JsonNode.Parse(listText)?["count"]?.GetValue<int>() ?? -1, "groceries count");

            var done = await Rpc("tools/call", new JsonObject { ["name"] = "complete_task", ["arguments"] = new JsonObject { ["title"] = "Eggs" } }, 7);
            Assert(done["result"]?["isError"]?.GetValue<bool>() == false, "complete_task failed");
            Assert(TaskStore.Tasks.First(t => t.Title == "Eggs").Completed, "Eggs completed");

            SettingsService.Current.MuseAllowEdits = false;
            var blocked = await Rpc("tools/call", new JsonObject { ["name"] = "delete_task", ["arguments"] = new JsonObject { ["id"] = milk.Id } }, 8);
            Assert(blocked["result"]?["isError"]?.GetValue<bool>() == true, "delete should be refused when edits are off");
            Assert(!milk.Deleted, "milk must still exist");
            SettingsService.Current.MuseAllowEdits = true;

            // Each assistant's own link: /mcp/<key>/claude
            var viaClaude = await Rpc("tools/call", new JsonObject { ["name"] = "add_task", ["arguments"] = new JsonObject { ["title"] = "Ask Claude about the trip" } }, 10,
                $"http://localhost:{port}/mcp/{SettingsService.Current.MuseKey}/claude");
            Assert(viaClaude["result"]?["isError"]?.GetValue<bool>() == false, "add via claude link");
            AreEqual("claude", TaskStore.Tasks.First(t => t.Title == "Ask Claude about the trip").Source, "source from the link");

            var unknown = await Rpc("nope/nothing", null, 9);
            AreEqual(-32601, unknown["error"]?["code"]?.GetValue<int>() ?? 0, "unknown method error");
        });

        await Test("AI connector: REST API, Siri and key check", async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var api = $"http://localhost:{port}/api/{SettingsService.Current.MuseKey}";
            var docs = await http.GetAsync(api);
            AreEqual(HttpStatusCode.OK, docs.StatusCode, "docs");
            Assert((await docs.Content.ReadAsStringAsync()).Contains("POST", StringComparison.Ordinal), "docs text");

            var post = await http.PostAsync(api + "/tasks", new StringContent("{\"title\":\"Pick up parcel\",\"add_to_my_day\":true}", Encoding.UTF8, "application/json"));
            AreEqual(HttpStatusCode.Created, post.StatusCode, "POST /tasks");
            var created = JsonNode.Parse(await post.Content.ReadAsStringAsync());
            var id = created?["id"]?.GetValue<string>();
            Assert(TaskStore.Find(id)?.InMyDay == true, "added to My Day");

            var patch = new HttpRequestMessage(HttpMethod.Patch, $"{api}/tasks/{id}") { Content = new StringContent("{\"title\":\"Pick up parcel from post office\",\"important\":true}", Encoding.UTF8, "application/json") };
            AreEqual(HttpStatusCode.OK, (await http.SendAsync(patch)).StatusCode, "PATCH");
            AreEqual("Pick up parcel from post office", TaskStore.Find(id)?.Title, "patched title");

            AreEqual(HttpStatusCode.OK, (await http.PostAsync($"{api}/tasks/{id}/complete", null)).StatusCode, "complete");
            AreEqual(HttpStatusCode.OK, (await http.GetAsync(api + "/tasks?view=completed")).StatusCode, "list completed");
            AreEqual(HttpStatusCode.OK, (await http.GetAsync(api + "/lists")).StatusCode, "lists");
            AreEqual(HttpStatusCode.NotFound, (await http.GetAsync(api + "/tasks/doesnotexist")).StatusCode, "missing task");

            // Siri (Apple Shortcuts) posts dictated text to quick-add
            var siri = await http.PostAsync(api + "/quick-add?via=siri", new StringContent("{\"text\":\"Call mom tomorrow at 5pm\"}", Encoding.UTF8, "application/json"));
            AreEqual(HttpStatusCode.Created, siri.StatusCode, "siri quick-add");
            var mom = TaskStore.Tasks.First(t => t.Title == "Call mom");
            AreEqual("siri", mom.Source, "siri source");
            AreEqual(new TimeSpan(17, 0, 0), mom.DueTime, "siri time");

            AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://localhost:{port}/api/wrong-key/tasks")).StatusCode, "wrong key");
            var bearer = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{port}/api/tasks");
            bearer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SettingsService.Current.MuseKey);
            AreEqual(HttpStatusCode.OK, (await http.SendAsync(bearer)).StatusCode, "bearer key");
            AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{port}/health")).StatusCode, "health");
            await MuseServer.StopAsync();
            Assert(!MuseServer.IsRunning, "server stopped");
        });

        await Test("Checkoff sounds are valid WAV files", async () =>
        {
            foreach (var n in SoundService.Names)
            {
                var wav = SoundService.Wav(n);
                Assert(wav.Length > 2000, n + " too short");
                AreEqual("RIFF", Encoding.ASCII.GetString(wav, 0, 4), n + " header");
                AreEqual("WAVE", Encoding.ASCII.GetString(wav, 8, 4), n + " type");
            }
            AreEqual(true, SettingsService.Current.CompleteSound, "sound on by default");
            await Task.CompletedTask;
        });

        // ---------------- UI ----------------
        await Test("UI: views, detail pane and adding in Light mode", async () =>
        {
            SeedSampleData();
            SettingsService.Current.Theme = ThemeMode.Light;
            SettingsService.Commit();
            ShowView("myday");
            await Shot("01-myday-light");
            AreEqual(6, _rows.OfType<TaskItem>().Count(t => !t.Completed), "open tasks in My Day (5 + 1 overdue)");

            AddBox.Text = "Renew passport next month #Personal p2";
            await Settle();
            Assert(AddChips.Children.Count >= 3, "chips should explain the parsed task");
            await Shot("02-quick-add-chips");
            var added = AddFromBox();
            Assert(added != null && added.Title == "Renew passport" && added.Priority == Priority.Medium && added.InMyDay, "added from the box");

            var report = _rows.OfType<TaskItem>().First(t => t.Title.StartsWith("Prepare", StringComparison.Ordinal));
            TaskView.SelectedItem = report;
            await Settle();
            Assert(DetailPane.Visibility == Visibility.Visible, "detail pane opens");
            AreEqual(report.Title, DetailTitle.Text, "detail title");
            await Shot("03-detail-light");
            HideDetail();

            foreach (var v in new[] { "important", "planned", "muse", "tasks" })
            {
                ShowView(v);
                await Shot("04-" + v + "-light");
            }
            ShowList(TaskStore.Lists.First(l => l.Name == "Work"));
            await Shot("05-list-work-light");
            AreEqual(3, _rows.OfType<TaskItem>().Count(t => !t.Completed), "open tasks in Work");

            SearchBox.Text = "dentist";
            await Settle();
            AreEqual(1, _rows.OfType<TaskItem>().Count(), "search result");
            await Shot("06-search");
            SearchBox.Text = "";
        });

        await Test("UI: Dark mode and Settings", async () =>
        {
            SettingsService.Current.Theme = ThemeMode.Dark;
            SettingsService.Commit();
            ShowView("myday");
            await Shot("10-myday-dark");
            var t = _rows.OfType<TaskItem>().First(x => x.HasDueTime);
            TaskView.SelectedItem = t;
            await Settle();
            await Shot("11-detail-dark");
            HideDetail();
            ShowView("planned");
            await Shot("12-planned-dark");

            SettingsService.Current.MuseEnabled = true;
            await StartMuseAsync();
            ShowSettings();
            await Shot("13-settings-dark");
            var sv = FindChild<System.Windows.Controls.ScrollViewer>(SettingsPage);
            sv?.ScrollToVerticalOffset(820);
            await Shot("14-settings-ai-dark");
            _aiPick = "siri";
            RefreshMuseCard();
            sv = FindChild<System.Windows.Controls.ScrollViewer>(SettingsPage);
            sv?.ScrollToVerticalOffset(1100);
            await Shot("16-settings-siri-dark");
            _aiPick = "claude";
            await StopMuseAsync();

            SettingsService.Current.Theme = ThemeMode.Light;
            SettingsService.Commit();
            ShowSettings();
            sv = FindChild<System.Windows.Controls.ScrollViewer>(SettingsPage);
            sv?.ScrollToVerticalOffset(820);
            await Shot("15-settings-ai-light");
            sv?.ScrollToVerticalOffset(300);
            await Shot("17-settings-general-light");
            ShowView("myday");
        });

        await Test("Reminder pops up", async () =>
        {
            var t = TaskStore.Tasks.First(x => !x.Completed);
            t.Reminder = DateTime.Now.AddSeconds(-5);
            t.ReminderFired = false;
            int before = Application.Current.Windows.OfType<Views.Toast>().Count();
            ReminderService.Check();
            await Settle();
            Assert(t.ReminderFired, "reminder marked as shown");
            Assert(Application.Current.Windows.OfType<Views.Toast>().Count() > before, "toast window shown");
            Views.Toast.CloseAll();
        });

        File.AppendAllText(App.SelfTestOutput, _testFails == 0 ? "ALL PASSED\r\n" : $"{_testFails} FAILED\r\n");
        TaskStore.Flush();
        Environment.Exit(_testFails);
    }

    private static T FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>The tasks from the design mock-up, plus a few more.</summary>
    private void SeedSampleData()
    {
        var d = new TaskData();
        var work = new TaskList { Name = "Work", Color = "#3B82F6", Icon = "work", Order = 1 };
        var personal = new TaskList { Name = "Personal", Color = "#8B5CF6", Icon = "home", Order = 2 };
        var shopping = new TaskList { Name = "Shopping", Color = "#22C55E", Icon = "cart", Order = 3 };
        d.Lists.AddRange(new[] { work, personal, shopping });
        var today = DateTime.Today;
        double o = 1;
        TaskItem T(string title, TaskList l, DateTime? due = null, TimeSpan? time = null, bool imp = false, Priority p = Priority.None, bool myDay = true)
        {
            var t = new TaskItem { Title = title, ListId = l?.Id, Due = due, DueTime = time, Important = imp, Priority = p, MyDay = myDay ? today : null, Order = o++ };
            d.Tasks.Add(t);
            return t;
        }
        T("Prepare quarterly report", work, today, null, true, Priority.High);
        var mail = T("Reply to client email", work, today, new TimeSpan(14, 0, 0), true);
        mail.Reminder = today.AddHours(13).AddMinutes(45);
        mail.ReminderFired = true;
        T("Gym — Evening workout", personal, today, new TimeSpan(18, 0, 0)).Repeat = Repeat.Weekdays;
        var groc = T("Grocery shopping — buy vegetables and milk", shopping, today.AddDays(1));
        groc.Steps.Add(new TaskStep { Title = "Vegetables" });
        groc.Steps.Add(new TaskStep { Title = "Milk", Done = true });
        T("Book dentist appointment", personal, today.AddDays(2), null, false, Priority.Medium).Notes = "Ask about the cleaning too.";
        var c1 = T("Water the plants", personal, today);
        c1.Completed = true; c1.CompletedAt = DateTime.Now.AddHours(-2);
        var c2 = T("Pay electricity bill", null, today);
        c2.Completed = true; c2.CompletedAt = DateTime.Now.AddHours(-1);
        T("Plan team offsite", work, today.AddDays(9), null, false, Priority.Low, myDay: false);
        T("Renew car insurance", null, today.AddDays(-2), null, true, Priority.High, myDay: false);
        var muse = T("Pick up birthday cake for Sam", shopping, today.AddDays(3), new TimeSpan(16, 30, 0), myDay: false);
        muse.Source = "claude";
        var muse2 = T("Call the plumber about the kitchen sink", null, null, null, myDay: false);
        muse2.Source = "muse";
        T("Book flights for the holidays", personal, today.AddDays(5), null, false, Priority.High, myDay: false).Source = "chatgpt";
        TaskStore.Reset(d);
    }
}
