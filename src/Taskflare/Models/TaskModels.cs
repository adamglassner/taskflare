using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Taskflare.Models;

public enum Priority { None = 0, Low = 1, Medium = 2, High = 3 }

public enum Repeat { None, Daily, Weekdays, Weekly, Monthly, Yearly }

/// <summary>Base class for objects the UI binds to.</summary>
public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    public void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>Refresh every binding on this object.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A step (sub-task) inside a task.</summary>
public sealed class TaskStep : Notify
{
    private string _title = "";
    private bool _done;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set => Set(ref _title, value ?? ""); }
    public bool Done { get => _done; set => Set(ref _done, value); }
}

/// <summary>
/// A to-do. Every record has a stable id, an UpdatedAt time and a Deleted flag (deleted tasks are kept
/// as tombstones for a while) so the mobile version can sync with the PC later.
/// </summary>
public sealed class TaskItem : Notify
{
    private string _title = "";
    private string _notes = "";
    private string _listId;
    private bool _important;
    private Priority _priority;
    private DateTime? _due;
    private TimeSpan? _dueTime;
    private DateTime? _reminder;
    private Repeat _repeat;
    private DateTime? _myDay;
    private bool _completed;
    private DateTime? _completedAt;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set { if (Set(ref _title, value ?? "")) Raise(nameof(SearchText)); } }
    public string Notes { get => _notes; set { if (Set(ref _notes, value ?? "")) Raise(nameof(HasNotes)); } }
    /// <summary>The list it's in. Null means the built-in "Tasks" list.</summary>
    public string ListId { get => _listId; set => Set(ref _listId, value); }
    public bool Important { get => _important; set => Set(ref _important, value); }
    public Priority Priority { get => _priority; set { if (Set(ref _priority, value)) Raise(nameof(HasPriority), nameof(PriorityText)); } }
    /// <summary>Due date (date only, local).</summary>
    public DateTime? Due { get => _due; set { if (Set(ref _due, value?.Date)) RaiseDue(); } }
    /// <summary>Optional time of day for the due date.</summary>
    public TimeSpan? DueTime { get => _dueTime; set { if (Set(ref _dueTime, value)) RaiseDue(); } }
    /// <summary>When to pop up a reminder (local time).</summary>
    public DateTime? Reminder { get => _reminder; set { if (Set(ref _reminder, value)) Raise(nameof(HasReminder), nameof(ReminderText)); } }
    public bool ReminderFired { get; set; }
    public Repeat Repeat { get => _repeat; set { if (Set(ref _repeat, value)) Raise(nameof(HasRepeat), nameof(RepeatText)); } }
    /// <summary>The day it was added to My Day. It's in My Day only while this is today.</summary>
    public DateTime? MyDay { get => _myDay; set { if (Set(ref _myDay, value?.Date)) Raise(nameof(InMyDay)); } }
    public bool Completed { get => _completed; set => Set(ref _completed, value); }
    public DateTime? CompletedAt { get => _completedAt; set => Set(ref _completedAt, value); }
    public ObservableCollection<TaskStep> Steps { get; set; } = new();
    public List<string> Labels { get; set; } = new();
    /// <summary>"app", or "muse" when Meta's Muse AI added it through the connector.</summary>
    public string Source { get; set; } = "app";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }
    public double Order { get; set; }

    // ---------- display helpers (not saved) ----------
    private string _listName = "Tasks";
    private string _listColor = "#3B82F6";
    private bool _showList = true;
    private bool _selected;

    [JsonIgnore] public string ListName { get => _listName; set => Set(ref _listName, value); }
    [JsonIgnore] public string ListColor { get => _listColor; set => Set(ref _listColor, value); }
    /// <summary>Hide the list name on the row when you're looking at that list already.</summary>
    [JsonIgnore] public bool ShowList { get => _showList; set => Set(ref _showList, value); }
    [JsonIgnore] public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

    [JsonIgnore] public bool FromMuse => string.Equals(Source, "muse", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool InMyDay => _myDay.HasValue && _myDay.Value.Date == DateTime.Today;
    [JsonIgnore] public bool HasDue => _due.HasValue;
    [JsonIgnore] public bool HasDueTime => _due.HasValue && _dueTime.HasValue;
    [JsonIgnore] public bool HasNotes => !string.IsNullOrWhiteSpace(_notes);
    [JsonIgnore] public bool HasReminder => _reminder.HasValue;
    [JsonIgnore] public bool HasRepeat => _repeat != Repeat.None;
    [JsonIgnore] public bool HasPriority => _priority != Priority.None;
    [JsonIgnore] public bool HasSteps => Steps.Count > 0;
    [JsonIgnore] public string StepsText => $"{Steps.Count(s => s.Done)} of {Steps.Count}";
    [JsonIgnore] public string SearchText => _title;
    [JsonIgnore] public bool HasLabels => Labels.Count > 0;
    [JsonIgnore] public string LabelsText => string.Join("  ", Labels.Select(l => "@" + l));

    [JsonIgnore] public DateTime? DueAt => _due.HasValue ? _due.Value.Date + (_dueTime ?? TimeSpan.Zero) : null;

    [JsonIgnore]
    public bool IsOverdue
    {
        get
        {
            if (_completed || !_due.HasValue) return false;
            if (_due.Value.Date < DateTime.Today) return true;
            return _due.Value.Date == DateTime.Today && _dueTime.HasValue && DateTime.Now > DueAt;
        }
    }

    [JsonIgnore]
    public string DueText
    {
        get
        {
            if (!_due.HasValue) return "";
            var text = Dates.Friendly(_due.Value);
            if (_dueTime.HasValue) text += ", " + Dates.Time(_dueTime.Value);
            return text;
        }
    }

    [JsonIgnore]
    public string PriorityText => _priority switch
    {
        Priority.High => "High priority",
        Priority.Medium => "Medium",
        Priority.Low => "Low",
        _ => "",
    };

    [JsonIgnore]
    public string ReminderText => _reminder.HasValue ? "Remind me " + Dates.Friendly(_reminder.Value).ToLowerInvariantFirst() + ", " + Dates.Time(_reminder.Value.TimeOfDay) : "";

    [JsonIgnore]
    public string RepeatText => _repeat switch
    {
        Repeat.Daily => "Daily",
        Repeat.Weekdays => "Weekdays",
        Repeat.Weekly => "Weekly",
        Repeat.Monthly => "Monthly",
        Repeat.Yearly => "Yearly",
        _ => "",
    };

    [JsonIgnore]
    public string CreatedText
    {
        get
        {
            var local = CreatedAt.ToLocalTime();
            var by = FromMuse ? " by Muse" : "";
            return local.Date == DateTime.Today ? $"Created{by} today" : $"Created{by} {Dates.Friendly(local)}";
        }
    }

    private void RaiseDue() => Raise(nameof(HasDue), nameof(HasDueTime), nameof(DueText), nameof(IsOverdue), nameof(DueAt));

    /// <summary>Call after Steps change so the "1 of 3" text updates.</summary>
    public void StepsChanged() => Raise(nameof(HasSteps), nameof(StepsText));

    public void Touch() => UpdatedAt = DateTime.UtcNow;

    /// <summary>A copy for the next time a repeating task comes round.</summary>
    public TaskItem CloneForRepeat()
    {
        var t = new TaskItem
        {
            Title = Title,
            Notes = Notes,
            ListId = ListId,
            Important = Important,
            Priority = Priority,
            Repeat = Repeat,
            DueTime = DueTime,
            Source = Source,
            Labels = new List<string>(Labels),
        };
        foreach (var s in Steps) t.Steps.Add(new TaskStep { Title = s.Title });
        return t;
    }
}

/// <summary>A user list such as Work, Personal or Shopping.</summary>
public sealed class TaskList : Notify
{
    private string _name = "";
    private string _color = "#3B82F6";
    private string _icon = "list";
    private int _openCount;
    private bool _isActive;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public string Color { get => _color; set => Set(ref _color, value ?? "#3B82F6"); }
    /// <summary>One of <see cref="ListIcons"/>' keys.</summary>
    public string Icon { get => _icon; set { if (Set(ref _icon, value ?? "list")) Raise(nameof(Glyph)); } }
    public double Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; }

    [JsonIgnore] public int OpenCount { get => _openCount; set { if (Set(ref _openCount, value)) Raise(nameof(HasOpen)); } }
    [JsonIgnore] public bool HasOpen => _openCount > 0;
    [JsonIgnore] public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    [JsonIgnore] public string Glyph => ListIcons.Glyph(_icon);

    public void Touch() => UpdatedAt = DateTime.UtcNow;
}

public static class ListIcons
{
    /// <summary>Icon key → Segoe Fluent Icons / MDL2 glyph.</summary>
    public static readonly (string Key, string Glyph)[] All =
    {
        ("list", ""),
        ("work", ""),
        ("home", ""),
        ("cart", ""),
        ("heart", ""),
        ("book", ""),
        ("plane", ""),
        ("game", ""),
        ("star", ""),
        ("people", ""),
    };

    public static string Glyph(string key) => All.FirstOrDefault(a => a.Key == key).Glyph ?? "";
}

public static class ListColors
{
    public static readonly string[] All =
    {
        "#3B82F6", "#8B5CF6", "#22C55E", "#F59E0B", "#EF4444", "#14B8A6", "#EC4899", "#64748B",
    };
}

/// <summary>The data file: %APPDATA%\Taskflare\tasks.json</summary>
public sealed class TaskData
{
    public int Version { get; set; } = 1;
    /// <summary>Identifies this PC for syncing later.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public List<TaskList> Lists { get; set; } = new();
    public List<TaskItem> Tasks { get; set; } = new();
}

public static class Dates
{
    public static string Friendly(DateTime d)
    {
        var day = d.Date;
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(1)) return "Tomorrow";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today && day < today.AddDays(7)) return day.ToString("dddd");
        return day.Year == today.Year ? day.ToString("ddd, MMM d") : day.ToString("ddd, MMM d, yyyy");
    }

    public static string Time(TimeSpan t) => DateTime.Today.Add(t).ToString("h:mm tt");

    public static string ToLowerInvariantFirst(this string s) =>
        string.IsNullOrEmpty(s) || s.Length < 2 || !char.IsUpper(s[0]) || (s.Length > 1 && s[1] == ',') ? s
        : s is "Today" or "Tomorrow" or "Yesterday" ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    /// <summary>The next due date for a repeating task, counted from its current due date (or today).</summary>
    public static DateTime Next(DateTime from, Repeat r)
    {
        var d = from.Date;
        switch (r)
        {
            case Repeat.Daily: return d.AddDays(1);
            case Repeat.Weekly: return d.AddDays(7);
            case Repeat.Monthly: return d.AddMonths(1);
            case Repeat.Yearly: return d.AddYears(1);
            case Repeat.Weekdays:
                d = d.AddDays(1);
                while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
                return d;
            default: return d;
        }
    }
}
