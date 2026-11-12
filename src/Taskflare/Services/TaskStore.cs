using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Taskflare.Models;

namespace Taskflare.Services;

/// <summary>
/// All tasks and lists, kept in memory and saved to %APPDATA%\Taskflare\tasks.json (written atomically,
/// with a daily backup). There's no limit on the number of tasks or lists.
/// Everything here runs on the UI thread; the Muse connector marshals its calls onto it.
/// </summary>
public static class TaskStore
{
    public static string FilePath => Path.Combine(SettingsService.Folder, "tasks.json");

    public static TaskData Data { get; private set; } = new();

    /// <summary>Lists shown in the sidebar (not deleted), in order.</summary>
    public static ObservableCollection<TaskList> Lists { get; } = new();

    public static IEnumerable<TaskItem> Tasks => Data.Tasks.Where(t => !t.Deleted);

    /// <summary>Raised after any change (on the UI thread).</summary>
    public static event Action Changed;

    /// <summary>Raised when an AI assistant adds a task.</summary>
    public static event Action<TaskItem> AiAdded;

    private static DispatcherTimer _saveTimer;
    private static bool _dirty;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<TaskData>(File.ReadAllText(FilePath), SettingsService.Options);
                if (d != null) Data = d;
            }
            else
            {
                Data = new TaskData();
                // Starter lists, like the ones in Microsoft To Do and Todoist. Delete or rename them freely.
                Data.Lists.Add(new TaskList { Name = "Work", Color = "#3B82F6", Icon = "work", Order = 1 });
                Data.Lists.Add(new TaskList { Name = "Personal", Color = "#8B5CF6", Icon = "home", Order = 2 });
                Data.Lists.Add(new TaskList { Name = "Shopping", Color = "#22C55E", Icon = "cart", Order = 3 });
                _dirty = true;
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
            Data = new TaskData();
        }
        Sanitize();
        RebuildLists();
        foreach (var t in Data.Tasks) Decorate(t);
        if (_dirty) SaveNow();
    }

    private static void Sanitize()
    {
        Data.Lists ??= new();
        Data.Tasks ??= new();
        if (string.IsNullOrEmpty(Data.DeviceId)) Data.DeviceId = Guid.NewGuid().ToString("N");
        foreach (var t in Data.Tasks)
        {
            t.Steps ??= new();
            t.Labels ??= new();
            t.Title ??= "";
            t.Source ??= "app";
            if (string.IsNullOrEmpty(t.Id)) t.Id = Guid.NewGuid().ToString("N");
        }
        // Deleted records are kept for 60 days (so a future sync can pass the deletion on), then dropped.
        var cutoff = DateTime.UtcNow.AddDays(-60);
        if (Data.Tasks.RemoveAll(t => t.Deleted && t.UpdatedAt < cutoff) > 0) _dirty = true;
        if (Data.Lists.RemoveAll(l => l.Deleted && l.UpdatedAt < cutoff) > 0) _dirty = true;
        // Tasks in a list that no longer exists go back to Tasks.
        var ids = Data.Lists.Where(l => !l.Deleted).Select(l => l.Id).ToHashSet();
        foreach (var t in Data.Tasks.Where(t => t.ListId != null && !ids.Contains(t.ListId) && !t.Deleted))
        {
            t.ListId = null;
            _dirty = true;
        }
    }

    // ---------- saving ----------

    public static void ScheduleSave()
    {
        _dirty = true;
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public static void SaveNow()
    {
        _saveTimer?.Stop();
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            // One backup a day of the previous file
            var backup = Path.Combine(SettingsService.Folder, "backups", $"tasks-{DateTime.Today:yyyy-MM-dd}.json");
            if (File.Exists(FilePath) && !File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(FilePath, backup);
                foreach (var old in Directory.GetFiles(Path.GetDirectoryName(backup), "tasks-*.json").OrderByDescending(f => f).Skip(14))
                    try { File.Delete(old); } catch { }
            }
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, SettingsService.Options));
            File.Move(tmp, FilePath, true);
            _dirty = false;
        }
        catch { /* best effort; tried again on the next change */ }
    }

    public static void Flush()
    {
        if (_dirty) SaveNow();
    }

    // ---------- lists ----------

    public static void RebuildLists()
    {
        var live = Data.Lists.Where(l => !l.Deleted).OrderBy(l => l.Order).ToList();
        Lists.Clear();
        foreach (var l in live) Lists.Add(l);
        UpdateCounts();
    }

    public static TaskList FindList(string id) => id == null ? null : Data.Lists.FirstOrDefault(l => l.Id == id && !l.Deleted);

    public static TaskList FindListByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().TrimStart('#');
        return Lists.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? Lists.FirstOrDefault(l => l.Name.Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
    }

    public static TaskList AddList(string name, string color = null, string icon = "list")
    {
        var l = new TaskList
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled list" : name.Trim(),
            Color = color ?? ListColors.All[Lists.Count % ListColors.All.Length],
            Icon = icon ?? "list",
            Order = (Data.Lists.Count == 0 ? 0 : Data.Lists.Max(x => x.Order)) + 1,
        };
        Data.Lists.Add(l);
        RebuildLists();
        Commit();
        return l;
    }

    public static void UpdateList(TaskList l)
    {
        l.Touch();
        foreach (var t in Data.Tasks.Where(t => t.ListId == l.Id)) Decorate(t);
        Commit();
    }

    /// <summary>Deletes a list and every task in it.</summary>
    public static void DeleteList(TaskList l)
    {
        l.Deleted = true;
        l.Touch();
        foreach (var t in Data.Tasks.Where(t => t.ListId == l.Id && !t.Deleted))
        {
            t.Deleted = true;
            t.Touch();
        }
        RebuildLists();
        Commit();
    }

    public static void MoveList(TaskList l, int newIndex)
    {
        var order = Lists.ToList();
        order.Remove(l);
        order.Insert(Math.Clamp(newIndex, 0, order.Count), l);
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i].Order != i + 1) { order[i].Order = i + 1; order[i].Touch(); }
        }
        RebuildLists();
        Commit();
    }

    // ---------- tasks ----------

    public static void Decorate(TaskItem t)
    {
        var l = FindList(t.ListId);
        t.ListName = l?.Name ?? "Tasks";
        t.ListColor = l?.Color ?? "#3B82F6";
    }

    public static TaskItem Find(string id) => string.IsNullOrEmpty(id) ? null : Data.Tasks.FirstOrDefault(t => t.Id == id && !t.Deleted);

    /// <param name="aiSource">The assistant adding it ("claude", "chatgpt"…), or null when you add it.</param>
    public static TaskItem Add(TaskItem t, string aiSource = null)
    {
        if (aiSource != null) t.Source = AiNames.Normalize(aiSource);
        t.Title = (t.Title ?? "").Trim();
        var live = Tasks.ToList();
        if (SettingsService.Current.AddToTop)
            t.Order = (live.Count == 0 ? 0 : live.Min(x => x.Order)) - 1;
        else
            t.Order = (live.Count == 0 ? 0 : live.Max(x => x.Order)) + 1;
        if (SettingsService.Current.RemindAtDueTime && t.Reminder == null && t.Due.HasValue && t.DueTime.HasValue && t.DueAt > DateTime.Now)
            t.Reminder = t.DueAt;
        t.CreatedAt = t.UpdatedAt = DateTime.UtcNow;
        Decorate(t);
        Data.Tasks.Add(t);
        Commit();
        if (aiSource != null) AiAdded?.Invoke(t);
        return t;
    }

    /// <summary>Call after changing a task.</summary>
    public static void Update(TaskItem t)
    {
        t.Touch();
        Decorate(t);
        Commit();
    }

    public static void SetCompleted(TaskItem t, bool completed)
    {
        if (t.Completed == completed) return;
        t.Completed = completed;
        t.CompletedAt = completed ? DateTime.Now : null;
        t.Touch();
        if (completed && t.Repeat != Repeat.None)
        {
            // Like Microsoft To Do: the finished one stays in Completed and the next one is added.
            var next = t.CloneForRepeat();
            var from = t.Due ?? DateTime.Today;
            var due = Dates.Next(from, t.Repeat);
            while (due < DateTime.Today) due = Dates.Next(due, t.Repeat);
            next.Due = due;
            if (t.Reminder.HasValue)
                next.Reminder = due.Date + t.Reminder.Value.TimeOfDay;
            // Don't repeat twice if it's un-ticked and ticked again.
            t.Repeat = Repeat.None;
            Add(next);
        }
        Commit();
    }

    public static void Delete(TaskItem t)
    {
        t.Deleted = true;
        t.Touch();
        Commit();
    }

    public static void Restore(TaskItem t)
    {
        t.Deleted = false;
        t.Touch();
        Decorate(t);
        Commit();
    }

    /// <summary>Puts a task before <paramref name="before"/> (or at the end when null) in manual order.</summary>
    public static void Reorder(TaskItem t, TaskItem before, IList<TaskItem> visible)
    {
        if (before == t) return;
        var list = visible.Where(x => x != t).ToList();
        int i = before == null ? list.Count : list.IndexOf(before);
        if (i < 0) i = list.Count;
        double prev = i > 0 ? list[i - 1].Order : (list.Count > 0 ? list[0].Order - 2 : 0);
        double next = i < list.Count ? list[i].Order : prev + 2;
        t.Order = (prev + next) / 2;
        t.Touch();
        Commit();
    }

    public static void UpdateCounts()
    {
        var counts = Tasks.Where(t => !t.Completed && t.ListId != null).GroupBy(t => t.ListId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var l in Lists) l.OpenCount = counts.TryGetValue(l.Id, out var c) ? c : 0;
    }

    private static void Commit()
    {
        UpdateCounts();
        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>Re-raise Changed (e.g. at midnight, so My Day and "Today" labels move on).</summary>
    public static void Refresh()
    {
        foreach (var t in Data.Tasks) t.RaiseAll();
        Changed?.Invoke();
    }

    // ---------- export / import (and the merge the sync will use) ----------

    public static void Export(string path)
    {
        SaveNow();
        File.WriteAllText(path, JsonSerializer.Serialize(Data, SettingsService.Options));
    }

    /// <summary>Merges another Taskflare file in: new records are added, and for records in both the newer edit wins.</summary>
    public static (int added, int updated) Import(string path)
    {
        var other = JsonSerializer.Deserialize<TaskData>(File.ReadAllText(path), SettingsService.Options) ?? new TaskData();
        return Merge(other);
    }

    public static (int added, int updated) Merge(TaskData other)
    {
        int added = 0, updated = 0;
        foreach (var l in other.Lists ?? new())
        {
            var mine = Data.Lists.FirstOrDefault(x => x.Id == l.Id);
            if (mine == null) { Data.Lists.Add(l); added++; }
            else if (l.UpdatedAt > mine.UpdatedAt) { Data.Lists[Data.Lists.IndexOf(mine)] = l; updated++; }
        }
        foreach (var t in other.Tasks ?? new())
        {
            t.Steps ??= new();
            t.Labels ??= new();
            var mine = Data.Tasks.FirstOrDefault(x => x.Id == t.Id);
            if (mine == null) { Data.Tasks.Add(t); added++; }
            else if (t.UpdatedAt > mine.UpdatedAt) { Data.Tasks[Data.Tasks.IndexOf(mine)] = t; updated++; }
        }
        Sanitize();
        RebuildLists();
        foreach (var t in Data.Tasks) Decorate(t);
        Commit();
        return (added, updated);
    }

    /// <summary>Replaces everything (self-test only).</summary>
    internal static void Reset(TaskData d)
    {
        Data = d;
        Sanitize();
        RebuildLists();
        foreach (var t in Data.Tasks) Decorate(t);
        Commit();
    }
}
