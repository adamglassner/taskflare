using System.Windows.Threading;
using Taskflare.Models;

namespace Taskflare.Services;

/// <summary>Checks for due reminders every 15 seconds, and moves the app on to a new day at midnight.</summary>
public static class ReminderService
{
    private static DispatcherTimer _timer;
    private static DateTime _today = DateTime.Today;

    public static event Action<TaskItem> ReminderDue;

    public static void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();
        Check();
    }

    public static void Check()
    {
        if (DateTime.Today != _today)
        {
            _today = DateTime.Today;
            TaskStore.Refresh();
        }
        var now = DateTime.Now;
        foreach (var t in TaskStore.Tasks.Where(t => !t.Completed && t.Reminder.HasValue && !t.ReminderFired && t.Reminder.Value <= now).ToList())
        {
            t.ReminderFired = true;
            TaskStore.ScheduleSave();
            // Reminders more than a day old (e.g. the PC was off) are skipped quietly.
            if (now - t.Reminder.Value < TimeSpan.FromDays(1)) ReminderDue?.Invoke(t);
        }
    }

    public static void Snooze(TaskItem t, TimeSpan by)
    {
        t.Reminder = DateTime.Now.Add(by);
        t.ReminderFired = false;
        TaskStore.Update(t);
    }
}
