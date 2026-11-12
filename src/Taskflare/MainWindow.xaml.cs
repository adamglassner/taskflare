using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Taskflare.Models;
using Taskflare.Services;
using Taskflare.Views;

namespace Taskflare;

public partial class MainWindow : Window
{
    // ---------- view state ----------
    /// <summary>"myday", "important", "planned", "muse", "tasks", "list", "search" or "settings".</summary>
    private string _view = "myday";
    private TaskList _activeList;
    private string _search = "";
    private readonly ObservableCollection<object> _rows = new();
    private readonly Dictionary<string, RowHeader> _headers = new();
    private TaskItem _selected;
    private bool _quitting;
    private bool _refreshing;

    // Options picked with the buttons on the add box, for the next new task
    private DateTime? _pendDue;
    private DateTime? _pendRemind;
    private Repeat _pendRepeat;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        TaskView.ItemsSource = _rows;
        ListsHost.ItemsSource = TaskStore.Lists;

        TaskStore.Changed += OnStoreChanged;
        TaskStore.AiAdded += OnAiAdded;
        ReminderService.ReminderDue += OnReminder;
        ThemeManager.ThemeChanged += () => { if (_selected != null) RenderDetail(); };
        TrayService.OpenRequested += () => Dispatcher.InvokeAsync(BringToFront);
        TrayService.QuickAddRequested += () => Dispatcher.InvokeAsync(() => { BringToFront(); FocusAdd(); });
        TrayService.QuitRequested += () => Dispatcher.InvokeAsync(Quit);
        MuseServer.StatusChanged += () => Dispatcher.InvokeAsync(RefreshMuseCard);
        TunnelService.Changed += () => Dispatcher.InvokeAsync(RefreshMuseCard);

        // Drop a task on My Day, Important or Tasks in the sidebar
        foreach (var nav in new[] { NavMyDay, NavImportant, NavTasks })
        {
            nav.AllowDrop = true;
            nav.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(TaskFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            nav.Drop += SmartNav_Drop;
        }

        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += Window_Closing;
        Closed += (_, _) => Application.Current.Shutdown();
        RestoreWindow();

        var s = SettingsService.Current;
        if (s.LastView.StartsWith("list:", StringComparison.Ordinal) && TaskStore.FindList(s.LastView[5..]) is TaskList last)
            ShowList(last);
        else
            ShowView(s.LastView is "important" or "planned" or "muse" or "tasks" ? s.LastView : "myday");

        if (App.IsSelfTest) Loaded += async (_, _) => await RunSelfTest();
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string view) ShowView(view);
    }

    private void ListNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is TaskList l) ShowList(l);
    }

    public void ShowView(string view)
    {
        _view = view;
        _activeList = null;
        foreach (var l in TaskStore.Lists) l.IsActive = false;
        NavMyDay.IsChecked = view == "myday";
        NavImportant.IsChecked = view == "important";
        NavPlanned.IsChecked = view == "planned";
        NavMuse.IsChecked = view == "muse";
        NavTasks.IsChecked = view == "tasks";
        if (view != "search" && SearchBox.Text.Length > 0)
        {
            _search = "";
            SearchBox.Text = "";
        }
        SettingsPage.Visibility = Visibility.Collapsed;
        if (view is "myday" or "important" or "planned" or "muse" or "tasks")
        {
            SettingsService.Current.LastView = view;
            SettingsService.Save();
        }
        _headers.Clear();
        RefreshView(scrollTop: true);
    }

    public void ShowList(TaskList l)
    {
        _view = "list";
        _activeList = l;
        NavMyDay.IsChecked = NavImportant.IsChecked = NavPlanned.IsChecked = NavMuse.IsChecked = NavTasks.IsChecked = false;
        foreach (var x in TaskStore.Lists) x.IsActive = x == l;
        if (SearchBox.Text.Length > 0) { _search = ""; SearchBox.Text = ""; }
        SettingsPage.Visibility = Visibility.Collapsed;
        SettingsService.Current.LastView = "list:" + l.Id;
        SettingsService.Save();
        _headers.Clear();
        RefreshView(scrollTop: true);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void Help_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.RepoUrl + "#readme");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        if (_search.Length > 0)
        {
            if (_view != "search")
            {
                _view = "search";
                SettingsPage.Visibility = Visibility.Collapsed;
                NavMyDay.IsChecked = NavImportant.IsChecked = NavPlanned.IsChecked = NavMuse.IsChecked = NavTasks.IsChecked = false;
                foreach (var l in TaskStore.Lists) l.IsActive = false;
            }
            RefreshView(scrollTop: true);
        }
        else if (_view == "search")
        {
            ShowView(SettingsService.Current.LastView is "important" or "planned" or "muse" or "tasks" ? SettingsService.Current.LastView : "myday");
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; TaskView.Focus(); }
        else if (e.Key == Key.Down && _rows.OfType<TaskItem>().FirstOrDefault() is TaskItem first)
        {
            TaskView.SelectedItem = first;
            TaskView.Focus();
        }
    }

    // =====================================================================
    // Building the list
    // =====================================================================

    private string SortKey => _view == "list" ? "list:" + _activeList?.Id : _view;

    private SortMode CurrentSort
    {
        get => SettingsService.Current.Sorts.TryGetValue(SortKey, out var m) ? m : SortMode.Manual;
        set
        {
            SettingsService.Current.Sorts[SortKey] = value;
            SettingsService.Save();
        }
    }

    private bool ShowCompleted
    {
        get => !SettingsService.Current.ShowCompleted.TryGetValue(SortKey, out var b) || b;
        set
        {
            SettingsService.Current.ShowCompleted[SortKey] = value;
            SettingsService.Save();
        }
    }

    /// <summary>The tasks in the current view (open and completed).</summary>
    private List<TaskItem> ViewTasks()
    {
        var today = DateTime.Today;
        var all = TaskStore.Tasks;
        return (_view switch
        {
            "myday" => all.Where(t => t.InMyDay || (t.Due.HasValue && t.Due.Value.Date <= today && (!t.Completed || t.CompletedAt?.Date == today))),
            "important" => all.Where(t => t.Important),
            "planned" => all.Where(t => t.Due.HasValue),
            "muse" => all.Where(t => t.FromAi),
            "tasks" => all.Where(t => t.ListId == null),
            "list" => all.Where(t => t.ListId == _activeList?.Id),
            "search" => all.Where(t => Matches(t, _search)),
            _ => Enumerable.Empty<TaskItem>(),
        }).ToList();
    }

    private static bool Matches(TaskItem t, string q) =>
        q.Length > 0 && (t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || (t.Notes ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                         || t.Labels.Any(l => l.Contains(q.TrimStart('@'), StringComparison.OrdinalIgnoreCase))
                         || t.Steps.Any(s => s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)));

    private IEnumerable<TaskItem> Sorted(IEnumerable<TaskItem> items)
    {
        var mode = CurrentSort;
        if (_view == "planned" && mode == SortMode.Manual) mode = SortMode.DueDate;
        if (_view == "muse" && mode == SortMode.Manual) mode = SortMode.Created;
        return mode switch
        {
            SortMode.Importance => items.OrderByDescending(t => t.Important).ThenBy(t => t.Order),
            SortMode.DueDate => items.OrderBy(t => t.DueAt ?? DateTime.MaxValue).ThenBy(t => t.Order),
            SortMode.Priority => items.OrderByDescending(t => t.Priority).ThenBy(t => t.DueAt ?? DateTime.MaxValue).ThenBy(t => t.Order),
            SortMode.Alphabetical => items.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            SortMode.Created => items.OrderByDescending(t => t.CreatedAt),
            _ => items.OrderBy(t => t.Order),
        };
    }

    private RowHeader Header(string key, string text, int count, bool completed = false, bool defaultExpanded = true)
    {
        var full = SortKey + "|" + key;
        if (!_headers.TryGetValue(full, out var h))
        {
            h = new RowHeader { Key = full, IsCompleted = completed, Expanded = defaultExpanded };
            _headers[full] = h;
        }
        h.Count = count;
        h.Text = text;
        return h;
    }

    private void OnStoreChanged()
    {
        if (_refreshing) return;
        RefreshView();
        if (_selected != null)
        {
            if (_selected.Deleted) HideDetail();
            else RenderDetail();
        }
    }

    public void RefreshView(bool scrollTop = false)
    {
        _refreshing = true;
        try
        {
            var today = DateTime.Today;
            var tasks = ViewTasks();
            foreach (var t in tasks)
            {
                t.ShowList = _view is not ("list" or "tasks");
                t.ShowMyDay = _view != "myday";
            }
            var open = Sorted(tasks.Where(t => !t.Completed)).ToList();
            var done = tasks.Where(t => t.Completed).OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt).ToList();
            var rows = new List<object>();

            if (_view == "planned")
            {
                void Group(string key, string text, IEnumerable<TaskItem> items)
                {
                    var list = items.ToList();
                    if (list.Count == 0) return;
                    var h = Header(key, text, list.Count);
                    rows.Add(h);
                    if (h.Expanded) rows.AddRange(list);
                }
                var weekEnd = today.AddDays(7);
                Group("overdue", "Earlier", open.Where(t => t.Due.Value.Date < today));
                Group("today", "Today", open.Where(t => t.Due.Value.Date == today));
                Group("tomorrow", "Tomorrow", open.Where(t => t.Due.Value.Date == today.AddDays(1)));
                Group("week", "This week", open.Where(t => t.Due.Value.Date > today.AddDays(1) && t.Due.Value.Date <= weekEnd));
                Group("later", "Later", open.Where(t => t.Due.Value.Date > weekEnd));
            }
            else rows.AddRange(open);

            if (done.Count > 0 && ShowCompleted)
            {
                var h = Header("completed", "", done.Count, completed: true, defaultExpanded: false);
                h.Text = h.Expanded ? "Completed" : $"+ {done.Count} completed task{(done.Count == 1 ? "" : "s")}";
                h.ShowCount = h.Expanded;
                rows.Add(h);
                if (h.Expanded) rows.AddRange(done);
            }

            Sync(rows);
            UpdateHeader(open.Count, done.Count);
            UpdateCounts();
            if (scrollTop && _rows.Count > 0) TaskView.ScrollIntoView(_rows[0]);
            if (_selected != null && _rows.Contains(_selected) && TaskView.SelectedItem != _selected) TaskView.SelectedItem = _selected;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Updates the visible rows in place, so scrolling and selection survive.</summary>
    private void Sync(List<object> target)
    {
        var wanted = new HashSet<object>(target);
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_rows[i])) _rows.RemoveAt(i);
        for (int i = 0; i < target.Count; i++)
        {
            if (i < _rows.Count && ReferenceEquals(_rows[i], target[i])) continue;
            int at = _rows.IndexOf(target[i]);
            if (at >= 0) _rows.Move(at, i);
            else _rows.Insert(i, target[i]);
        }
        while (_rows.Count > target.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    private void UpdateHeader(int openCount, int doneCount)
    {
        var today = DateTime.Today;
        string title, sub, glyph = null, emptyGlyph, emptyTitle, emptyText;
        bool canAdd = true;
        switch (_view)
        {
            case "myday":
                title = "My Day - " + today.ToString("dddd, MMMM d");
                sub = $"Today · {openCount} task{(openCount == 1 ? "" : "s")}";
                emptyGlyph = ""; emptyTitle = "Focus on your day";
                emptyText = "Add tasks here, or use the lightbulb to pick from what's due. Tasks due today show up here too.";
                break;
            case "important":
                title = "Important"; glyph = "";
                sub = $"{openCount} starred task{(openCount == 1 ? "" : "s")}";
                emptyGlyph = ""; emptyTitle = "Nothing starred yet";
                emptyText = "Star a task to see it here.";
                break;
            case "planned":
                title = "Planned"; glyph = "";
                sub = $"{openCount} task{(openCount == 1 ? "" : "s")} with due dates";
                emptyGlyph = ""; emptyTitle = "No due dates";
                emptyText = "Tasks with due dates show up here, grouped by when they're due.";
                break;
            case "muse":
                title = "Added by AI"; glyph = "";
                sub = MuseServer.IsRunning ? $"{openCount} open · AI connector is on" : $"{openCount} open · AI connector is off (Settings › AI assistants)";
                emptyGlyph = ""; emptyTitle = "Let your AI assistant add tasks";
                emptyText = "Connect Claude, ChatGPT, Gemini, Grok, Muse or Siri in Settings › AI assistants, then ask things like \"add milk to my Shopping list in Taskflare\".";
                canAdd = false;
                break;
            case "tasks":
                title = "Tasks"; glyph = "";
                sub = $"{openCount} task{(openCount == 1 ? "" : "s")}";
                emptyGlyph = ""; emptyTitle = "Your inbox is clear";
                emptyText = "Tasks that aren't in a list land here.";
                break;
            case "list":
                title = _activeList?.Name ?? "List"; glyph = _activeList?.Glyph;
                sub = $"{openCount} task{(openCount == 1 ? "" : "s")}";
                emptyGlyph = _activeList?.Glyph ?? ""; emptyTitle = "Nothing here yet";
                emptyText = "Add a task to this list above.";
                break;
            case "search":
                title = $"Searching for \"{_search}\""; glyph = "";
                sub = $"{openCount + doneCount} result{(openCount + doneCount == 1 ? "" : "s")}";
                emptyGlyph = ""; emptyTitle = "No matches";
                emptyText = "Try a different word. Search looks in titles, notes, steps and labels.";
                canAdd = false;
                break;
            default:
                title = ""; sub = ""; emptyGlyph = ""; emptyTitle = ""; emptyText = "";
                break;
        }
        ViewTitle.Text = title;
        ViewSubtitle.Text = sub;
        ViewGlyph.Text = glyph ?? "";
        ViewGlyph.Visibility = glyph == null ? Visibility.Collapsed : Visibility.Visible;
        if (_view == "list" && _activeList != null) ViewGlyph.Foreground = (Brush)new HexBrush().Convert(_activeList.Color, typeof(Brush), null, null);
        else ViewGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        AddArea.Visibility = canAdd ? Visibility.Visible : Visibility.Collapsed;
        SuggestButton.Visibility = _view == "myday" ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyGlyph.Text = emptyGlyph;
        EmptyTitle.Text = emptyTitle;
        EmptyText.Text = emptyText;
        var sort = CurrentSort;
        SortText.Text = sort == SortMode.Manual ? "Sort" : "Sorted by " + SortName(sort).ToLowerInvariant();
    }

    private void UpdateCounts()
    {
        var today = DateTime.Today;
        var open = TaskStore.Tasks.Where(t => !t.Completed).ToList();
        static string C(int n) => n == 0 ? "" : n.ToString();
        CountMyDay.Text = C(open.Count(t => t.InMyDay || (t.Due.HasValue && t.Due.Value.Date <= today)));
        CountImportant.Text = C(open.Count(t => t.Important));
        CountPlanned.Text = C(open.Count(t => t.Due.HasValue));
        CountMuse.Text = C(open.Count(t => t.FromAi));
        CountTasks.Text = C(open.Count(t => t.ListId == null));
    }

    private static string SortName(SortMode m) => m switch
    {
        SortMode.Importance => "Importance",
        SortMode.DueDate => "Due date",
        SortMode.Priority => "Priority",
        SortMode.Alphabetical => "Alphabetically",
        SortMode.Created => "Creation date",
        _ => "My order",
    };

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RowHeader h)
        {
            h.Expanded = !h.Expanded;
            RefreshView();
        }
    }

    // =====================================================================
    // Adding tasks
    // =====================================================================

    public void FocusAdd()
    {
        if (AddArea.Visibility != Visibility.Visible) ShowView("myday");
        AddBox.Focus();
        Keyboard.Focus(AddBox);
    }

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddFromBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddBox.Text = "";
            ClearPending();
            TaskView.Focus();
        }
    }

    private void AddBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateChips();

    private void AddBox_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (AddBox.IsKeyboardFocused) AddBorder.SetResourceReference(Border.BorderBrushProperty, "Accent");
        else AddBorder.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
    }

    /// <summary>Adds the task typed in the add box. Returns it (or null if the box was empty).</summary>
    public TaskItem AddFromBox()
    {
        var text = AddBox.Text.Trim();
        if (text.Length == 0) return null;
        var p = QuickAddParser.Parse(text, DateTime.Now, TaskStore.Lists, SettingsService.Current.SmartQuickAdd);
        var t = new TaskItem
        {
            Title = p.Title,
            Due = p.Due ?? _pendDue,
            DueTime = p.Time,
            Priority = p.Priority,
            Important = p.Important || _view == "important",
            Repeat = p.Repeat != Repeat.None ? p.Repeat : _pendRepeat,
            Labels = p.Labels,
            Reminder = _pendRemind,
        };
        if (p.List != null) t.ListId = p.List.Id;
        else if (_view == "list") t.ListId = _activeList?.Id;
        if (_view == "myday") t.MyDay = DateTime.Today;
        if (_view == "planned" && t.Due == null) t.Due = DateTime.Today;
        if (t.Repeat != Repeat.None && t.Due == null) t.Due = DateTime.Today;
        AddBox.Text = "";
        ClearPending();
        TaskStore.Add(t);
        if (_rows.Contains(t)) TaskView.ScrollIntoView(t);
        return t;
    }

    private void ClearPending()
    {
        _pendDue = null;
        _pendRemind = null;
        _pendRepeat = Repeat.None;
        UpdateChips();
    }

    private void UpdateChips()
    {
        AddChips.Children.Clear();
        var chips = new List<(string Glyph, string Text)>();
        if (SettingsService.Current.SmartQuickAdd && AddBox.Text.Trim().Length > 0)
        {
            var p = QuickAddParser.Parse(AddBox.Text, DateTime.Now, TaskStore.Lists);
            foreach (var c in p.Chips)
            {
                var g = c.StartsWith("Due", StringComparison.Ordinal) ? ""
                    : c.StartsWith("Repeats", StringComparison.Ordinal) ? ""
                    : c.StartsWith("List", StringComparison.Ordinal) ? ""
                    : c.StartsWith("@", StringComparison.Ordinal) ? ""
                    : c == "Important" ? "" : "";
                chips.Add((g, c));
            }
        }
        if (_pendDue != null) chips.Add(("", "Due " + Dates.Friendly(_pendDue.Value)));
        if (_pendRemind != null) chips.Add(("", "Remind " + Dates.Friendly(_pendRemind.Value) + ", " + Dates.Time(_pendRemind.Value.TimeOfDay)));
        if (_pendRepeat != Repeat.None) chips.Add(("", "Repeats " + _pendRepeat.ToString().ToLowerInvariant()));
        foreach (var (glyph, text) in chips)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var gi = new TextBlock { Text = glyph, Style = Ui.S("Icon"), FontSize = 11, Margin = new Thickness(0, 1, 6, 0) };
            gi.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var tx = new TextBlock { Text = text, FontSize = 12.5 };
            tx.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            sp.Children.Add(gi);
            sp.Children.Add(tx);
            AddChips.Children.Add(new Border { Style = Ui.S("Chip"), Child = sp });
        }
        AddChips.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddDueButton.SetResourceReference(Control.ForegroundProperty, _pendDue != null ? "Accent" : "TextSecondary");
        AddRemindButton.SetResourceReference(Control.ForegroundProperty, _pendRemind != null ? "Accent" : "TextSecondary");
        AddRepeatButton.SetResourceReference(Control.ForegroundProperty, _pendRepeat != Repeat.None ? "Accent" : "TextSecondary");
    }

    private void AddDue_Click(object sender, RoutedEventArgs e) =>
        DatePopup.Open(AddDueButton, _pendDue, false, null, (d, _) => { _pendDue = d; UpdateChips(); AddBox.Focus(); });

    private void AddRemind_Click(object sender, RoutedEventArgs e) =>
        DatePopup.Open(AddRemindButton, _pendRemind?.Date, true, _pendRemind?.TimeOfDay, (d, t) =>
        {
            _pendRemind = d == null ? null : d.Value.Date + (t ?? new TimeSpan(9, 0, 0));
            UpdateChips();
            AddBox.Focus();
        }, "Remind me");

    private void AddRepeat_Click(object sender, RoutedEventArgs e)
    {
        var menu = RepeatMenu(_pendRepeat, r => { _pendRepeat = r; UpdateChips(); AddBox.Focus(); });
        menu.PlacementTarget = AddRepeatButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static ContextMenu RepeatMenu(Repeat current, Action<Repeat> pick)
    {
        var menu = new ContextMenu();
        foreach (var (r, text, glyph) in new[]
        {
            (Repeat.Daily, "Daily", ""), (Repeat.Weekdays, "Weekdays", ""), (Repeat.Weekly, "Weekly", ""),
            (Repeat.Monthly, "Monthly", ""), (Repeat.Yearly, "Yearly", ""),
        })
        {
            var mi = new MenuItem { Header = text, Icon = new TextBlock { Text = glyph }, IsChecked = current == r };
            mi.Click += (_, _) => pick(r);
            menu.Items.Add(mi);
        }
        if (current != Repeat.None)
        {
            menu.Items.Add(new Separator());
            var never = new MenuItem { Header = "Never repeat", Icon = new TextBlock { Text = "" } };
            never.Click += (_, _) => pick(Repeat.None);
            menu.Items.Add(never);
        }
        return menu;
    }

    // =====================================================================
    // Task rows
    // =====================================================================

    private static TaskItem RowTask(object sender) => (sender as FrameworkElement)?.DataContext as TaskItem;

    private void TaskCheck_Click(object sender, RoutedEventArgs e)
    {
        var t = RowTask(sender);
        if (t == null) return;
        Complete(t, !t.Completed);
    }

    public void Complete(TaskItem t, bool done)
    {
        TaskStore.SetCompleted(t, done);
        if (done && !App.IsSelfTest) SoundService.PlayComplete();
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        var t = RowTask(sender);
        if (t == null) return;
        t.Important = !t.Important;
        TaskStore.Update(t);
    }

    private void TaskView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskView.SelectedItem is TaskItem t)
        {
            if (_selected != null) _selected.IsSelected = false;
            ShowDetail(t);
        }
        else if (TaskView.SelectedItem is RowHeader)
        {
            TaskView.SelectedItem = _selected != null && _rows.Contains(_selected) ? _selected : null;
        }
    }

    private void TaskView_KeyDown(object sender, KeyEventArgs e)
    {
        if (TaskView.SelectedItem is not TaskItem t) return;
        if (e.Key == Key.Delete) { DeleteTask(t); e.Handled = true; }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None) { Complete(t, !t.Completed); e.Handled = true; }
        else if (e.Key == Key.Enter) { DetailTitle.Focus(); DetailTitle.CaretIndex = DetailTitle.Text.Length; e.Handled = true; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.N) { FocusAdd(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (ctrl && e.Key == Key.D && _selected != null && !(Keyboard.FocusedElement is TextBox)) { Complete(_selected, !_selected.Completed); e.Handled = true; }
        else if (e.Key == Key.Escape && DetailPane.Visibility == Visibility.Visible && !(Keyboard.FocusedElement is TextBox)) { HideDetail(); e.Handled = true; }
        else if (e.Key == Key.Escape && SettingsPage.Visibility == Visibility.Visible) { ShowView(SettingsService.Current.LastView.StartsWith("list:") ? "myday" : SettingsService.Current.LastView); e.Handled = true; }
    }

    public void DeleteTask(TaskItem t)
    {
        if (SettingsService.Current.ConfirmDelete && !App.IsSelfTest
            && !Confirm.Ask(this, "Delete task?", $"\"{t.Title}\" will be permanently deleted."))
            return;
        if (_selected == t) HideDetail();
        TaskStore.Delete(t);
    }

    // ---------- context menu ----------

    private void TaskView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(TaskView, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (item?.DataContext is not TaskItem t) { e.Handled = true; return; }
        TaskView.SelectedItem = t;
        TaskMenu.Items.Clear();
        foreach (var mi in TaskMenuItems(t)) TaskMenu.Items.Add(mi);
    }

    private IEnumerable<object> TaskMenuItems(TaskItem t)
    {
        MenuItem M(string header, string glyph, Action act)
        {
            var mi = new MenuItem { Header = header, Icon = new TextBlock { Text = glyph } };
            mi.Click += (_, _) => act();
            return mi;
        }
        yield return M(t.InMyDay ? "Remove from My Day" : "Add to My Day", "", () => { t.MyDay = t.InMyDay ? null : DateTime.Today; TaskStore.Update(t); });
        yield return M(t.Important ? "Remove importance" : "Mark as important", t.Important ? "" : "", () => { t.Important = !t.Important; TaskStore.Update(t); });
        yield return M(t.Completed ? "Mark as not completed" : "Mark as completed", "", () => Complete(t, !t.Completed));
        yield return new Separator();
        yield return M("Due today", "", () => { t.Due = DateTime.Today; TaskStore.Update(t); });
        yield return M("Due tomorrow", "", () => { t.Due = DateTime.Today.AddDays(1); TaskStore.Update(t); });
        yield return M("Pick a date…", "", () => DatePopup.Open(TaskView, t.Due, false, null, (d, _) => { t.Due = d; if (d == null) t.DueTime = null; TaskStore.Update(t); }));
        if (t.Due != null) yield return M("Remove due date", "", () => { t.Due = null; t.DueTime = null; TaskStore.Update(t); });
        yield return new Separator();
        var prio = new MenuItem { Header = "Priority", Icon = new TextBlock { Text = "" } };
        foreach (var p in new[] { Priority.High, Priority.Medium, Priority.Low, Priority.None })
        {
            var pi = new MenuItem { Header = p == Priority.None ? "No priority" : p.ToString(), IsChecked = t.Priority == p };
            var pp = p;
            pi.Click += (_, _) => { t.Priority = pp; TaskStore.Update(t); };
            prio.Items.Add(pi);
        }
        yield return prio;
        var move = new MenuItem { Header = "Move task to…", Icon = new TextBlock { Text = "" } };
        var inbox = new MenuItem { Header = "Tasks", IsChecked = t.ListId == null };
        inbox.Click += (_, _) => { t.ListId = null; TaskStore.Update(t); };
        move.Items.Add(inbox);
        foreach (var l in TaskStore.Lists)
        {
            var li = new MenuItem { Header = l.Name, IsChecked = t.ListId == l.Id };
            var ll = l;
            li.Click += (_, _) => { t.ListId = ll.Id; TaskStore.Update(t); };
            move.Items.Add(li);
        }
        yield return move;
        yield return M("Duplicate task", "", () =>
        {
            var copy = t.CloneForRepeat();
            copy.Due = t.Due;
            copy.MyDay = t.MyDay;
            copy.Source = "app";
            TaskStore.Add(copy);
        });
        yield return new Separator();
        var del = M("Delete task", "", () => DeleteTask(t));
        del.SetResourceReference(Control.ForegroundProperty, "Danger");
        yield return del;
    }

    // ---------- header menus ----------

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Bottom };
        foreach (var m in new[] { SortMode.Manual, SortMode.Importance, SortMode.DueDate, SortMode.Priority, SortMode.Alphabetical, SortMode.Created })
        {
            var mi = new MenuItem { Header = SortName(m), IsChecked = CurrentSort == m };
            var mm = m;
            mi.Click += (_, _) => { CurrentSort = mm; RefreshView(); };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        var show = new MenuItem { Header = ShowCompleted ? "Hide completed tasks" : "Show completed tasks", Icon = new TextBlock { Text = "" } };
        show.Click += (_, _) => { ShowCompleted = !ShowCompleted; RefreshView(); };
        menu.Items.Add(show);
        var copy = new MenuItem { Header = "Copy list as text", Icon = new TextBlock { Text = "" } };
        copy.Click += (_, _) =>
        {
            var lines = _rows.OfType<TaskItem>().Select(t => (t.Completed ? "[x] " : "[ ] ") + t.Title + (t.HasDue ? $" ({t.DueText})" : ""));
            try { Clipboard.SetText(ViewTitle.Text + Environment.NewLine + string.Join(Environment.NewLine, lines)); } catch { }
        };
        menu.Items.Add(copy);
        if (_view == "list" && _activeList != null)
        {
            menu.Items.Add(new Separator());
            var edit = new MenuItem { Header = "Edit list…", Icon = new TextBlock { Text = "" } };
            edit.Click += (_, _) => EditList(_activeList);
            menu.Items.Add(edit);
            var del = new MenuItem { Header = "Delete list", Icon = new TextBlock { Text = "" } };
            del.Click += (_, _) => DeleteList(_activeList);
            menu.Items.Add(del);
        }
        if (_view == "myday")
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear finished tasks from My Day", Icon = new TextBlock { Text = "" } };
            clear.Click += (_, _) =>
            {
                foreach (var t in TaskStore.Tasks.Where(t => t.Completed && t.InMyDay).ToList()) { t.MyDay = null; t.Touch(); }
                TaskStore.ScheduleSave();
                RefreshView();
            };
            menu.Items.Add(clear);
        }
        menu.IsOpen = true;
    }

    /// <summary>The lightbulb: suggestions for My Day (overdue, due soon, recently added).</summary>
    private void Suggest_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var open = TaskStore.Tasks.Where(t => !t.Completed && !t.InMyDay).ToList();
        var groups = new List<(string Title, List<TaskItem> Items)>
        {
            ("Overdue", open.Where(t => t.Due.HasValue && t.Due.Value.Date < today).OrderBy(t => t.Due).Take(8).ToList()),
            ("Due soon", open.Where(t => t.Due.HasValue && t.Due.Value.Date > today && t.Due.Value.Date <= today.AddDays(3)).OrderBy(t => t.DueAt).Take(8).ToList()),
            ("Important", open.Where(t => t.Important && !t.Due.HasValue).Take(6).ToList()),
            ("Recently added", open.Where(t => !t.Due.HasValue && !t.Important).OrderByDescending(t => t.CreatedAt).Take(6).ToList()),
        };
        var popup = new Popup { PlacementTarget = SuggestButton, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };
        var border = new Border { Width = 340, MaxHeight = 520, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(0, 4, 12, 12) };
        border.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        border.SetResourceReference(Border.BorderBrushProperty, "MenuBorder");
        border.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "UiFont");
        border.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.25 };
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("Suggestions", "Text", 15, FontWeights.SemiBold));
        bool any = false;
        foreach (var (gTitle, items) in groups)
        {
            if (items.Count == 0) continue;
            any = true;
            var h = Ui.Text(gTitle, "TextSecondary", 12.5, FontWeights.SemiBold);
            h.Margin = new Thickness(0, 12, 0, 4);
            stack.Children.Add(h);
            foreach (var t in items)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var info = new StackPanel();
                info.Children.Add(Ui.Text(t.Title, "Text", 13.5));
                var meta = t.ListName + (t.HasDue ? " · " + t.DueText : "");
                info.Children.Add(Ui.Text(meta, t.IsOverdue ? "Overdue" : "TextSecondary", 12));
                g.Children.Add(info);
                var add = new Button { Content = "", Style = Ui.S("IconButton"), Width = 30, Height = 30, FontSize = 12, ToolTip = "Add to My Day" };
                add.SetResourceReference(Control.ForegroundProperty, "Accent");
                var tt = t;
                add.Click += (_, _) =>
                {
                    tt.MyDay = DateTime.Today;
                    TaskStore.Update(tt);
                    g.Opacity = 0.4;
                    add.IsEnabled = false;
                };
                Grid.SetColumn(add, 1);
                g.Children.Add(add);
                stack.Children.Add(g);
            }
        }
        if (!any)
        {
            var none = Ui.Text("Nothing to suggest right now. Tasks that are overdue, due soon or recently added show up here.", "TextSecondary", 13);
            none.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(none);
        }
        border.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        popup.Child = border;
        popup.IsOpen = true;
        _lastSuggest = popup;
    }

    private Popup _lastSuggest;

    // =====================================================================
    // Lists (sidebar)
    // =====================================================================

    private void NewList_Click(object sender, RoutedEventArgs e)
    {
        if (!ListDialog.Show(this, null, out var name, out var color, out var icon)) return;
        var l = TaskStore.AddList(name, color, icon);
        ShowList(l);
        FocusAdd();
    }

    private void EditList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TaskList l) EditList(l);
    }

    private void DeleteList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TaskList l) DeleteList(l);
    }

    private void EditList(TaskList l)
    {
        if (!ListDialog.Show(this, l, out var name, out var color, out var icon)) return;
        l.Name = name;
        l.Color = color;
        l.Icon = icon;
        TaskStore.UpdateList(l);
        if (_activeList == l) RefreshView();
    }

    private void DeleteList(TaskList l)
    {
        int n = TaskStore.Tasks.Count(t => t.ListId == l.Id);
        if (!App.IsSelfTest && !Confirm.Ask(this, "Delete list?", n == 0
                ? $"\"{l.Name}\" will be permanently deleted."
                : $"\"{l.Name}\" and its {n} task{(n == 1 ? "" : "s")} will be permanently deleted."))
            return;
        bool wasActive = _activeList == l;
        TaskStore.DeleteList(l);
        if (wasActive) ShowView("myday");
    }

    // =====================================================================
    // Drag and drop
    // =====================================================================

    private const string TaskFormat = "taskflare-task";
    private const string ListFormat = "taskflare-list";
    private Point _dragStart;
    private TaskItem _dragTask;
    private TaskList _dragList;

    private void TaskView_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        _dragTask = null;
        var src = e.OriginalSource as DependencyObject;
        if (FindParent<ToggleButton>(src) != null || FindParent<TextBox>(src) != null) return;
        if (ItemsControl.ContainerFromElement(TaskView, src) is ListBoxItem item && item.DataContext is TaskItem t)
        {
            _dragTask = t;
            _dragStart = e.GetPosition(this);
        }
    }

    private void TaskView_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTask == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var t = _dragTask;
        _dragTask = null;
        DragDrop.DoDragDrop(TaskView, new DataObject(TaskFormat, t), DragDropEffects.Move);
    }

    private void TaskView_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TaskFormat) && _view is not ("planned" or "muse" or "search") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TaskView_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TaskFormat) is not TaskItem t || _view is "planned" or "muse" or "search") return;
        var target = (ItemsControl.ContainerFromElement(TaskView, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as TaskItem;
        if (target == t || t.Completed) return;
        if (CurrentSort != SortMode.Manual) CurrentSort = SortMode.Manual;
        var open = _rows.OfType<TaskItem>().Where(x => !x.Completed).ToList();
        // Dropping on the lower half of a row puts it after that row.
        TaskItem before = target;
        if (target != null && ItemsControl.ContainerFromElement(TaskView, e.OriginalSource as DependencyObject) is ListBoxItem li)
        {
            if (e.GetPosition(li).Y > li.ActualHeight / 2)
            {
                int i = open.IndexOf(target);
                before = i >= 0 && i + 1 < open.Count ? open[i + 1] : null;
                if (before == t) before = i + 2 < open.Count ? open[i + 2] : null;
            }
        }
        if (target != null && target.Completed) before = null;
        TaskStore.Reorder(t, before, open);
    }

    private void ListNav_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        _dragList = (sender as FrameworkElement)?.Tag as TaskList;
        _dragStart = e.GetPosition(this);
    }

    private void ListNav_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragList == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var l = _dragList;
        _dragList = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ListFormat, l), DragDropEffects.Move);
    }

    private void ListNav_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TaskFormat) || e.Data.GetDataPresent(ListFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ListNav_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TaskList target) return;
        if (e.Data.GetData(TaskFormat) is TaskItem t)
        {
            t.ListId = target.Id;
            TaskStore.Update(t);
        }
        else if (e.Data.GetData(ListFormat) is TaskList l && l != target)
        {
            TaskStore.MoveList(l, TaskStore.Lists.IndexOf(target));
        }
        e.Handled = true;
    }

    private void SmartNav_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TaskFormat) is not TaskItem t) return;
        if (sender == NavMyDay) t.MyDay = DateTime.Today;
        else if (sender == NavImportant) t.Important = true;
        else if (sender == NavTasks) t.ListId = null;
        TaskStore.Update(t);
        e.Handled = true;
    }

    private static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // =====================================================================
    // Detail pane
    // =====================================================================

    private double _detailWidth = 360;

    private void ShowDetail(TaskItem t)
    {
        CommitDetailText();
        _selected = t;
        t.IsSelected = true;
        if (DetailPane.Visibility != Visibility.Visible)
        {
            DetailCol.Width = new GridLength(_detailWidth);
            DetailCol.MinWidth = 300;
            DetailCol.MaxWidth = 560;
            DetailPane.Visibility = Visibility.Visible;
            DetailSplitter.Visibility = Visibility.Visible;
        }
        RenderDetail(force: true);
    }

    public void HideDetail()
    {
        CommitDetailText();
        if (DetailPane.Visibility == Visibility.Visible && DetailCol.ActualWidth > 0) _detailWidth = DetailCol.ActualWidth;
        if (_selected != null) _selected.IsSelected = false;
        _selected = null;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailSplitter.Visibility = Visibility.Collapsed;
        DetailCol.MinWidth = 0;
        DetailCol.Width = new GridLength(0);
        TaskView.SelectedItem = null;
    }

    private void RenderDetail(bool force = false)
    {
        var t = _selected;
        if (t == null) return;
        DetailCheck.IsChecked = t.Completed;
        DetailStar.IsChecked = t.Important;
        if (force || !DetailTitle.IsKeyboardFocused) DetailTitle.Text = t.Title;
        if (force || !DetailNotes.IsKeyboardFocused) DetailNotes.Text = t.Notes;
        if (force || !DetailLabels.IsKeyboardFocused) DetailLabels.Text = string.Join(", ", t.Labels);
        if (!ReferenceEquals(StepsHost.ItemsSource, t.Steps)) StepsHost.ItemsSource = t.Steps;
        DetailTitle.TextDecorations = t.Completed ? TextDecorations.Strikethrough : null;

        SetRow(DetailMyDay, t.InMyDay ? "Added to My Day" : "Add to My Day", t.InMyDay);
        SetRow(DetailRemind, t.HasReminder ? t.ReminderText : "Remind me", t.HasReminder);
        SetRow(DetailDue, t.HasDue ? "Due " + t.DueText : "Add due date", t.HasDue, t.IsOverdue);
        SetRow(DetailRepeat, t.HasRepeat ? "Repeats " + t.RepeatText.ToLowerInvariant() : "Repeat", t.HasRepeat);
        SetRow(DetailPriority, t.HasPriority ? t.PriorityText + (t.Priority == Priority.High ? "" : " priority") : "Set priority", t.HasPriority,
            t.Priority == Priority.High);
        SetRow(DetailList, "List: " + t.ListName, false);
        DetailCreated.Text = t.Completed && t.CompletedAt.HasValue ? "Completed " + Dates.Friendly(t.CompletedAt.Value).ToLowerInvariantFirst() : t.CreatedText;
    }

    private static void SetRow(Button b, string text, bool on, bool danger = false)
    {
        b.Content = text;
        b.SetResourceReference(Control.ForegroundProperty, danger ? "Overdue" : on ? "Accent" : "TextSecondary");
    }

    private void CommitDetailText()
    {
        var t = _selected;
        if (t == null || t.Deleted) return;
        bool changed = false;
        var title = DetailTitle.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (title.Length > 0 && title != t.Title) { t.Title = title; changed = true; }
        if ((DetailNotes.Text ?? "") != (t.Notes ?? "")) { t.Notes = DetailNotes.Text; changed = true; }
        var labels = DetailLabels.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimStart('@')).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!labels.SequenceEqual(t.Labels)) { t.Labels = labels; t.Raise(nameof(TaskItem.HasLabels), nameof(TaskItem.LabelsText)); changed = true; }
        if (changed) TaskStore.Update(t);
    }

    private void DetailTitle_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitDetailText();
    private void DetailNotes_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitDetailText();
    private void DetailLabels_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitDetailText();

    private void DetailTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitDetailText(); TaskView.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DetailTitle.Text = _selected?.Title ?? ""; TaskView.Focus(); e.Handled = true; }
    }

    private void DetailLabels_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitDetailText(); e.Handled = true; }
    }

    private void DetailCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) Complete(_selected, !_selected.Completed);
    }

    private void DetailStar_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Important = !_selected.Important;
        TaskStore.Update(_selected);
    }

    private void DetailMyDay_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.MyDay = _selected.InMyDay ? null : DateTime.Today;
        TaskStore.Update(_selected);
    }

    private void DetailDue_Click(object sender, RoutedEventArgs e)
    {
        var t = _selected;
        if (t == null) return;
        DatePopup.Open(DetailDue, t.Due, true, t.DueTime, (d, time) =>
        {
            t.Due = d;
            t.DueTime = d == null ? null : time;
            if (d != null && time != null && SettingsService.Current.RemindAtDueTime && t.DueAt > DateTime.Now && t.Reminder == null)
            {
                t.Reminder = t.DueAt;
                t.ReminderFired = false;
            }
            TaskStore.Update(t);
        }, "Due date");
    }

    private void DetailRemind_Click(object sender, RoutedEventArgs e)
    {
        var t = _selected;
        if (t == null) return;
        DatePopup.Open(DetailRemind, t.Reminder?.Date, true, t.Reminder?.TimeOfDay, (d, time) =>
        {
            t.Reminder = d == null ? null : d.Value.Date + (time ?? new TimeSpan(9, 0, 0));
            t.ReminderFired = false;
            TaskStore.Update(t);
        }, "Remind me");
    }

    private void DetailRepeat_Click(object sender, RoutedEventArgs e)
    {
        var t = _selected;
        if (t == null) return;
        var menu = RepeatMenu(t.Repeat, r =>
        {
            t.Repeat = r;
            if (r != Repeat.None && t.Due == null) t.Due = DateTime.Today;
            TaskStore.Update(t);
        });
        menu.PlacementTarget = DetailRepeat;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DetailPriority_Click(object sender, RoutedEventArgs e)
    {
        var t = _selected;
        if (t == null) return;
        var menu = new ContextMenu { PlacementTarget = DetailPriority, Placement = PlacementMode.Bottom };
        foreach (var (p, text, glyph) in new[] { (Priority.High, "High", ""), (Priority.Medium, "Medium", ""), (Priority.Low, "Low", ""), (Priority.None, "No priority", "") })
        {
            var mi = new MenuItem { Header = text, IsChecked = t.Priority == p, Icon = new TextBlock { Text = glyph } };
            var pp = p;
            mi.Click += (_, _) => { t.Priority = pp; TaskStore.Update(t); };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void DetailList_Click(object sender, RoutedEventArgs e)
    {
        var t = _selected;
        if (t == null) return;
        var menu = new ContextMenu { PlacementTarget = DetailList, Placement = PlacementMode.Bottom };
        var inbox = new MenuItem { Header = "Tasks", IsChecked = t.ListId == null, Icon = new TextBlock { Text = "" } };
        inbox.Click += (_, _) => { t.ListId = null; TaskStore.Update(t); };
        menu.Items.Add(inbox);
        foreach (var l in TaskStore.Lists)
        {
            var icon = new TextBlock { Text = l.Glyph, Foreground = (Brush)new HexBrush().Convert(l.Color, typeof(Brush), null, null) };
            var mi = new MenuItem { Header = l.Name, IsChecked = t.ListId == l.Id, Icon = icon };
            var ll = l;
            mi.Click += (_, _) => { t.ListId = ll.Id; TaskStore.Update(t); };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) => HideDetail();

    private void DetailDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) DeleteTask(_selected);
    }

    // ---------- steps ----------

    private void NewStep_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _selected == null) return;
        var text = NewStepBox.Text.Trim();
        if (text.Length == 0) return;
        _selected.Steps.Add(new TaskStep { Title = text });
        _selected.StepsChanged();
        NewStepBox.Text = "";
        TaskStore.Update(_selected);
        e.Handled = true;
    }

    private void StepCheck_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskStep s || _selected == null) return;
        s.Done = !s.Done;
        _selected.StepsChanged();
        TaskStore.Update(_selected);
    }

    private void StepText_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TaskStep s || _selected == null) return;
        var text = tb.Text.Trim();
        if (text.Length == 0) { tb.Text = s.Title; return; }
        if (text == s.Title) return;
        s.Title = text;
        TaskStore.Update(_selected);
    }

    private void StepText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { NewStepBox.Focus(); e.Handled = true; }
    }

    private void StepDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskStep s || _selected == null) return;
        _selected.Steps.Remove(s);
        _selected.StepsChanged();
        TaskStore.Update(_selected);
    }

    // =====================================================================
    // Reminders, Muse and the window
    // =====================================================================

    private void OnReminder(TaskItem t)
    {
        var detail = t.HasDue ? "Due " + t.DueText.ToLowerInvariantFirst() + " · " + t.ListName : t.ListName;
        Toast.Pop("Reminder", t.Title, detail, "", "Accent", new (string, Action, bool)[]
        {
            ("Complete", () => Complete(t, true), true),
            ("Snooze 10 min", () => ReminderService.Snooze(t, TimeSpan.FromMinutes(10)), false),
            ("Open", () => { BringToFront(); Reveal(t); }, false),
        }, 0, sound: true);
    }

    private void OnAiAdded(TaskItem t)
    {
        if (!SettingsService.Current.MuseNotify || App.IsSelfTest) return;
        var detail = t.ListName + (t.HasDue ? " · due " + t.DueText.ToLowerInvariantFirst() : "");
        Toast.Pop(t.AddedByName + " added a task", t.Title, detail, "", "MuseBadge", new (string, Action, bool)[]
        {
            ("Open", () => { BringToFront(); Reveal(t); }, true),
        }, 8);
    }

    /// <summary>Shows a task: switches to a view that has it and selects it.</summary>
    public void Reveal(TaskItem t)
    {
        if (t == null || t.Deleted) return;
        if (!ViewTasks().Contains(t))
        {
            var l = TaskStore.FindList(t.ListId);
            if (l != null) ShowList(l);
            else ShowView("tasks");
        }
        if (t.Completed)
        {
            var h = _headers.Values.FirstOrDefault(x => x.IsCompleted);
            if (h != null && !h.Expanded) { h.Expanded = true; RefreshView(); }
        }
        TaskView.SelectedItem = t;
        TaskView.ScrollIntoView(t);
    }

    public async Task StartMuseAsync()
    {
        var s = SettingsService.Current;
        await MuseServer.StartAsync(s.MusePort);
        if (MuseServer.IsRunning && s.MuseAutoTunnel && TunnelService.Find() != null) TunnelService.Start(s.MusePort);
        if (_view == "muse") RefreshView();
    }

    public async Task StopMuseAsync()
    {
        TunnelService.Stop();
        await MuseServer.StopAsync();
        if (_view == "muse") RefreshView();
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        CommitDetailText();
        SaveWindow();
        TaskStore.Flush();
        if (!_quitting && !App.IsSelfTest && SettingsService.Current.KeepRunningInTray)
        {
            e.Cancel = true;
            Hide();
            TrayService.Show();
            if (!SettingsService.Current.TrayHintShown)
            {
                SettingsService.Current.TrayHintShown = true;
                SettingsService.Save();
                Toast.Pop("Taskflare is still running", "Reminders and AI assistants keep working",
                    "Find Taskflare in the notification area by the clock. You can change this in Settings.", "", "Accent",
                    new (string, Action, bool)[] { ("Quit Taskflare", () => Quit(), false) }, 10);
            }
            return;
        }
        _quitting = true;
        Toast.CloseAll();
    }

    public void Quit()
    {
        _quitting = true;
        Toast.CloseAll();
        Close();
    }

    private void RestoreWindow()
    {
        var s = SettingsService.Current;
        Width = s.WinWidth;
        Height = s.WinHeight;
        if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop))
        {
            var area = SystemParameters.VirtualScreenWidth;
            if (s.WinLeft > SystemParameters.VirtualScreenLeft - 50 && s.WinLeft < SystemParameters.VirtualScreenLeft + area - 100 &&
                s.WinTop > SystemParameters.VirtualScreenTop - 50 && s.WinTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WinLeft;
                Top = s.WinTop;
            }
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WinMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindow()
    {
        if (App.IsSelfTest) return;
        var s = SettingsService.Current;
        s.WinMaximized = WindowState == WindowState.Maximized;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty && r.Width > 0)
        {
            s.WinLeft = r.Left;
            s.WinTop = r.Top;
            s.WinWidth = r.Width;
            s.WinHeight = r.Height;
        }
        SettingsService.Save();
    }
}
