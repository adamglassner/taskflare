using System.Text.Json.Nodes;
using Taskflare.Models;

namespace Taskflare.Services;

/// <summary>A mistake in what the AI asked for (shown back to it, not a crash).</summary>
public sealed class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
}

/// <summary>
/// The actions an AI assistant (Claude, ChatGPT, Gemini, Grok, Muse…) can take on Taskflare. Used by both the MCP endpoint and the REST API.
/// Always called on the UI thread.
/// </summary>
public static class MuseTools
{
    public const string Instructions =
        "Taskflare is the user's to-do list on their Windows PC. Use add_task (or add_tasks for several) when the user " +
        "asks you to remember, plan or schedule something they need to do. Put tasks in the list the user names " +
        "(list_lists shows the lists; a list that doesn't exist yet is created). Dates can be YYYY-MM-DD or words like " +
        "'tomorrow' or 'next friday'; times can be '17:30' or '5pm'. Check list_tasks before adding to avoid duplicates.";

    /// <summary>MCP tool definitions (JSON Schema for each tool's input).</summary>
    public const string ToolsJson = """
    [
      {
        "name": "add_task",
        "title": "Add a task",
        "description": "Add a to-do to the user's Taskflare list on their PC. Returns the new task.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "title": { "type": "string", "description": "What needs doing, e.g. 'Book dentist appointment'." },
            "notes": { "type": "string", "description": "Optional details." },
            "list": { "type": "string", "description": "List name, e.g. 'Work', 'Personal' or 'Shopping'. Created if it doesn't exist. Leave out for the default Tasks list." },
            "due_date": { "type": "string", "description": "Due date: YYYY-MM-DD or words such as 'today', 'tomorrow', 'friday', 'next week', 'nov 20'." },
            "due_time": { "type": "string", "description": "Optional due time, e.g. '14:00' or '2pm'." },
            "reminder": { "type": "string", "description": "Optional reminder time on the PC: 'YYYY-MM-DDTHH:mm' or words like 'tomorrow 9am'." },
            "important": { "type": "boolean", "description": "Star it as important." },
            "priority": { "type": "string", "enum": ["none", "low", "medium", "high"] },
            "add_to_my_day": { "type": "boolean", "description": "Also put it in today's My Day list." },
            "repeat": { "type": "string", "enum": ["none", "daily", "weekdays", "weekly", "monthly", "yearly"] },
            "steps": { "type": "array", "items": { "type": "string" }, "description": "Optional checklist of smaller steps." },
            "labels": { "type": "array", "items": { "type": "string" }, "description": "Optional labels (tags)." }
          },
          "required": ["title"]
        }
      },
      {
        "name": "add_tasks",
        "title": "Add several tasks",
        "description": "Add several to-dos at once, e.g. a shopping list or the steps of a plan. Each item takes the same fields as add_task.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "tasks": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "notes": { "type": "string" },
                  "list": { "type": "string" },
                  "due_date": { "type": "string" },
                  "due_time": { "type": "string" },
                  "reminder": { "type": "string" },
                  "important": { "type": "boolean" },
                  "priority": { "type": "string", "enum": ["none", "low", "medium", "high"] },
                  "add_to_my_day": { "type": "boolean" },
                  "repeat": { "type": "string", "enum": ["none", "daily", "weekdays", "weekly", "monthly", "yearly"] },
                  "steps": { "type": "array", "items": { "type": "string" } },
                  "labels": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["title"]
              }
            },
            "list": { "type": "string", "description": "Default list for items that don't name one." }
          },
          "required": ["tasks"]
        }
      },
      {
        "name": "quick_add",
        "title": "Quick add (natural language)",
        "description": "Add a task written the Todoist way, e.g. 'Call mom tomorrow at 5pm #Personal p1' or 'Water plants every monday'. #List picks a list, p1/p2/p3 sets priority, @word adds a label, * marks it important.",
        "inputSchema": {
          "type": "object",
          "properties": { "text": { "type": "string" } },
          "required": ["text"]
        }
      },
      {
        "name": "list_tasks",
        "title": "List tasks",
        "description": "Read the user's tasks. Use it to answer questions about their to-dos and to avoid adding duplicates.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "view": { "type": "string", "enum": ["all", "my_day", "important", "planned", "overdue", "today", "completed", "ai"], "description": "Which tasks. 'all' (default) is every open task; 'ai' is tasks added by AI assistants." },
            "list": { "type": "string", "description": "Only tasks in this list." },
            "search": { "type": "string", "description": "Only tasks whose title or notes contain this text." },
            "limit": { "type": "integer", "description": "Maximum number to return (default 100)." }
          }
        }
      },
      {
        "name": "list_lists",
        "title": "List the lists",
        "description": "The user's lists (such as Work, Personal, Shopping) with how many open tasks each has.",
        "inputSchema": { "type": "object", "properties": {} }
      },
      {
        "name": "complete_task",
        "title": "Complete a task",
        "description": "Tick off a task (or un-tick it with completed=false). Give its id from list_tasks, or its exact title.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "title": { "type": "string" },
            "completed": { "type": "boolean", "description": "Default true." }
          }
        }
      },
      {
        "name": "update_task",
        "title": "Change a task",
        "description": "Change a task's title, notes, list, dates, priority and so on. Only the fields you give are changed. Use an empty string to clear due_date, due_time or reminder.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "title": { "type": "string" },
            "notes": { "type": "string" },
            "list": { "type": "string" },
            "due_date": { "type": "string" },
            "due_time": { "type": "string" },
            "reminder": { "type": "string" },
            "important": { "type": "boolean" },
            "priority": { "type": "string", "enum": ["none", "low", "medium", "high"] },
            "add_to_my_day": { "type": "boolean" },
            "repeat": { "type": "string", "enum": ["none", "daily", "weekdays", "weekly", "monthly", "yearly"] },
            "add_steps": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["id"]
        }
      },
      {
        "name": "delete_task",
        "title": "Delete a task",
        "description": "Delete a task by id. Only when the user clearly asks for it to be deleted.",
        "inputSchema": {
          "type": "object",
          "properties": { "id": { "type": "string" } },
          "required": ["id"]
        }
      },
      {
        "name": "create_list",
        "title": "Create a list",
        "description": "Make a new list, e.g. 'Holiday packing'.",
        "inputSchema": {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "color": { "type": "string", "description": "Optional colour as #RRGGBB." }
          },
          "required": ["name"]
        }
      }
    ]
    """;

    [ThreadStatic] private static string _source;

    private static readonly HashSet<string> EditTools = new() { "complete_task", "update_task", "delete_task" };

    /// <param name="source">Which assistant is calling ("claude", "chatgpt"…); tasks it adds are labelled with it.</param>
    public static JsonNode Execute(string name, JsonObject args, string source = "ai")
    {
        _source = AiNames.Normalize(source);
        args ??= new JsonObject();
        if (EditTools.Contains(name) && !SettingsService.Current.MuseAllowEdits)
            throw new ToolError("The user has only allowed adding and reading tasks. Changing, completing and deleting are turned off in Taskflare's settings.");
        return name switch
        {
            "add_task" => TaskJson(AddTask(args, null)),
            "add_tasks" => AddTasks(args),
            "quick_add" => QuickAdd(args),
            "list_tasks" => ListTasks(args),
            "list_lists" => ListLists(),
            "complete_task" => CompleteTask(args),
            "update_task" => UpdateTask(args),
            "delete_task" => DeleteTask(args),
            "create_list" => CreateList(args),
            _ => throw new ToolError($"Unknown tool '{name}'."),
        };
    }

    // ---------- tools ----------

    private static TaskItem AddTask(JsonObject a, string defaultList)
    {
        var title = Str(a, "title")?.Trim();
        if (string.IsNullOrEmpty(title)) throw new ToolError("A task needs a title.");
        if (title.Length > 500) title = title[..500];
        var now = DateTime.Now;
        var t = new TaskItem { Title = title, Notes = Str(a, "notes") ?? "" };
        ApplyFields(t, a, now, Str(a, "list") ?? defaultList);
        foreach (var s in Strings(a, "steps")) t.Steps.Add(new TaskStep { Title = s });
        foreach (var l in Strings(a, "labels")) t.Labels.Add(l.TrimStart('@'));
        return TaskStore.Add(t, _source ?? "ai");
    }

    private static JsonNode AddTasks(JsonObject a)
    {
        if (a["tasks"] is not JsonArray arr || arr.Count == 0) throw new ToolError("Give 'tasks' as a list of tasks.");
        if (arr.Count > 200) throw new ToolError("Add at most 200 tasks at a time.");
        var defaultList = Str(a, "list");
        var result = new JsonArray();
        foreach (var item in arr)
        {
            var o = item as JsonObject;
            if (o == null && item is JsonValue v && v.TryGetValue<string>(out var s)) o = new JsonObject { ["title"] = s };
            if (o == null) continue;
            result.Add(TaskJson(AddTask(o, defaultList)));
        }
        return new JsonObject { ["added"] = result.Count, ["tasks"] = result };
    }

    private static JsonNode QuickAdd(JsonObject a)
    {
        var text = Str(a, "text");
        if (string.IsNullOrWhiteSpace(text)) throw new ToolError("Give the task as 'text'.");
        var p = QuickAddParser.Parse(text, DateTime.Now, TaskStore.Lists);
        var t = new TaskItem
        {
            Title = p.Title,
            Due = p.Due,
            DueTime = p.Time,
            Priority = p.Priority,
            Important = p.Important,
            Repeat = p.Repeat,
            ListId = p.List?.Id,
            Labels = p.Labels,
        };
        return TaskJson(TaskStore.Add(t, _source ?? "ai"));
    }

    private static JsonNode ListTasks(JsonObject a)
    {
        var view = (Str(a, "view") ?? "all").ToLowerInvariant().Replace(' ', '_');
        var listName = Str(a, "list");
        var search = Str(a, "search");
        int limit = Int(a, "limit") ?? 100;
        limit = Math.Clamp(limit, 1, 1000);
        var today = DateTime.Today;

        IEnumerable<TaskItem> q = TaskStore.Tasks;
        q = view switch
        {
            "completed" => q.Where(t => t.Completed).OrderByDescending(t => t.CompletedAt),
            "my_day" or "myday" => q.Where(t => !t.Completed && (t.InMyDay || t.Due?.Date == today)),
            "important" => q.Where(t => !t.Completed && t.Important),
            "planned" => q.Where(t => !t.Completed && t.Due.HasValue).OrderBy(t => t.DueAt),
            "overdue" => q.Where(t => t.IsOverdue).OrderBy(t => t.DueAt),
            "today" => q.Where(t => !t.Completed && t.Due.HasValue && t.Due.Value.Date <= today).OrderBy(t => t.DueAt),
            "muse" or "ai" or "added_by_ai" => q.Where(t => t.FromAi).OrderByDescending(t => t.CreatedAt),
            _ => q.Where(t => !t.Completed).OrderBy(t => t.Order),
        };
        if (!string.IsNullOrWhiteSpace(listName))
        {
            var l = ResolveList(listName, create: false, out bool isDefault);
            if (l == null && !isDefault) throw new ToolError($"There's no list called '{listName}'. Lists: {ListNames()}.");
            var id = l?.Id;
            q = q.Where(t => t.ListId == id);
        }
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(t => t.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || (t.Notes ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        var all = q.ToList();
        var arr = new JsonArray();
        foreach (var t in all.Take(limit)) arr.Add(TaskJson(t));
        return new JsonObject { ["count"] = all.Count, ["returned"] = arr.Count, ["tasks"] = arr };
    }

    private static JsonNode ListLists()
    {
        var arr = new JsonArray
        {
            new JsonObject { ["name"] = "Tasks", ["default"] = true, ["open_tasks"] = TaskStore.Tasks.Count(t => !t.Completed && t.ListId == null) },
        };
        foreach (var l in TaskStore.Lists)
            arr.Add(new JsonObject { ["id"] = l.Id, ["name"] = l.Name, ["color"] = l.Color, ["open_tasks"] = l.OpenCount });
        return new JsonObject { ["lists"] = arr };
    }

    private static JsonNode CompleteTask(JsonObject a)
    {
        var t = FindTask(a);
        bool done = Bool(a, "completed") ?? true;
        TaskStore.SetCompleted(t, done);
        return TaskJson(t);
    }

    private static JsonNode UpdateTask(JsonObject a)
    {
        var t = FindTask(a);
        var title = Str(a, "title");
        if (title != null)
        {
            if (string.IsNullOrWhiteSpace(title)) throw new ToolError("The title can't be empty.");
            t.Title = title.Trim();
        }
        var notes = Str(a, "notes");
        if (notes != null) t.Notes = notes;
        ApplyFields(t, a, DateTime.Now, Str(a, "list"), updating: true);
        foreach (var s in Strings(a, "add_steps")) t.Steps.Add(new TaskStep { Title = s });
        t.StepsChanged();
        TaskStore.Update(t);
        return TaskJson(t);
    }

    private static JsonNode DeleteTask(JsonObject a)
    {
        var t = FindTask(a);
        TaskStore.Delete(t);
        return new JsonObject { ["deleted"] = true, ["id"] = t.Id, ["title"] = t.Title };
    }

    private static JsonNode CreateList(JsonObject a)
    {
        var name = Str(a, "name")?.Trim();
        if (string.IsNullOrEmpty(name)) throw new ToolError("A list needs a name.");
        var existing = TaskStore.FindListByName(name);
        if (existing != null) return new JsonObject { ["id"] = existing.Id, ["name"] = existing.Name, ["already_existed"] = true };
        var color = Str(a, "color");
        if (color != null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")) color = null;
        var l = TaskStore.AddList(name, color);
        return new JsonObject { ["id"] = l.Id, ["name"] = l.Name, ["color"] = l.Color };
    }

    // ---------- helpers ----------

    private static void ApplyFields(TaskItem t, JsonObject a, DateTime now, string listName, bool updating = false)
    {
        if (listName != null)
        {
            var l = ResolveList(listName, create: true, out _);
            t.ListId = l?.Id;
        }

        if (a.ContainsKey("due_date"))
        {
            var s = Str(a, "due_date");
            if (string.IsNullOrWhiteSpace(s)) { t.Due = null; t.DueTime = null; }
            else t.Due = QuickAddParser.ParseDate(s, now) ?? throw new ToolError($"Couldn't understand the date '{s}'. Use YYYY-MM-DD.");
        }
        if (a.ContainsKey("due_time"))
        {
            var s = Str(a, "due_time");
            if (string.IsNullOrWhiteSpace(s)) t.DueTime = null;
            else
            {
                t.DueTime = QuickAddParser.ParseTime(s, now) ?? throw new ToolError($"Couldn't understand the time '{s}'. Use HH:mm.");
                if (t.Due == null) t.Due = now.TimeOfDay > t.DueTime ? now.Date.AddDays(1) : now.Date;
            }
        }
        if (a.ContainsKey("reminder"))
        {
            var s = Str(a, "reminder");
            if (string.IsNullOrWhiteSpace(s)) t.Reminder = null;
            else t.Reminder = QuickAddParser.ParseDateTime(s, now) ?? throw new ToolError($"Couldn't understand the reminder '{s}'. Use YYYY-MM-DDTHH:mm.");
            t.ReminderFired = false;
        }
        var imp = Bool(a, "important");
        if (imp != null) t.Important = imp.Value;
        var pr = Str(a, "priority");
        if (pr != null)
        {
            t.Priority = pr.ToLowerInvariant() switch
            {
                "high" or "p1" or "urgent" => Priority.High,
                "medium" or "p2" or "normal" => Priority.Medium,
                "low" or "p3" => Priority.Low,
                _ => Priority.None,
            };
        }
        var myDay = Bool(a, "add_to_my_day");
        if (myDay != null) t.MyDay = myDay.Value ? DateTime.Today : null;
        var rep = Str(a, "repeat");
        if (rep != null)
        {
            t.Repeat = rep.ToLowerInvariant() switch
            {
                "daily" or "day" => Repeat.Daily,
                "weekdays" or "weekday" => Repeat.Weekdays,
                "weekly" or "week" => Repeat.Weekly,
                "monthly" or "month" => Repeat.Monthly,
                "yearly" or "year" or "annually" => Repeat.Yearly,
                _ => Repeat.None,
            };
            if (t.Repeat != Repeat.None && t.Due == null) t.Due = DateTime.Today;
        }
        if (updating && (a.ContainsKey("due_date") || a.ContainsKey("due_time")) && !a.ContainsKey("reminder")
            && SettingsService.Current.RemindAtDueTime && t.DueAt > DateTime.Now && t.DueTime.HasValue)
        {
            t.Reminder = t.DueAt;
            t.ReminderFired = false;
        }
    }

    private static TaskList ResolveList(string name, bool create, out bool isDefault)
    {
        isDefault = false;
        name = name.Trim().TrimStart('#');
        if (name.Length == 0 || name.Equals("tasks", StringComparison.OrdinalIgnoreCase) || name.Equals("inbox", StringComparison.OrdinalIgnoreCase))
        {
            isDefault = true;
            return null;
        }
        var l = TaskStore.FindListByName(name);
        if (l == null && create) l = TaskStore.AddList(name);
        return l;
    }

    private static string ListNames() => string.Join(", ", new[] { "Tasks" }.Concat(TaskStore.Lists.Select(l => l.Name)));

    private static TaskItem FindTask(JsonObject a)
    {
        var id = Str(a, "id");
        if (!string.IsNullOrWhiteSpace(id))
            return TaskStore.Find(id.Trim()) ?? throw new ToolError($"No task with id '{id}'. Use list_tasks to find ids.");
        var title = Str(a, "title");
        if (string.IsNullOrWhiteSpace(title)) throw new ToolError("Give the task's id (from list_tasks).");
        var exact = TaskStore.Tasks.Where(t => t.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Completed).ToList();
        if (exact.Count == 1) return exact[0];
        var open = exact.Where(t => !t.Completed).ToList();
        if (open.Count == 1) return open[0];
        if (exact.Count > 1) throw new ToolError($"{exact.Count} tasks are called '{title}'. Give the id instead (use list_tasks).");
        var partial = TaskStore.Tasks.Where(t => !t.Completed && t.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count > 1) throw new ToolError($"Several tasks match '{title}': " + string.Join("; ", partial.Take(10).Select(t => $"{t.Title} (id {t.Id})")));
        throw new ToolError($"No task called '{title}'.");
    }

    public static JsonObject TaskJson(TaskItem t)
    {
        var steps = new JsonArray();
        foreach (var s in t.Steps) steps.Add(new JsonObject { ["title"] = s.Title, ["done"] = s.Done });
        var labels = new JsonArray();
        foreach (var l in t.Labels) labels.Add(l);
        return new JsonObject
        {
            ["id"] = t.Id,
            ["title"] = t.Title,
            ["notes"] = string.IsNullOrEmpty(t.Notes) ? null : t.Notes,
            ["list"] = t.ListName,
            ["completed"] = t.Completed,
            ["completed_at"] = t.CompletedAt?.ToString("yyyy-MM-ddTHH:mm"),
            ["important"] = t.Important,
            ["priority"] = t.Priority.ToString().ToLowerInvariant(),
            ["due_date"] = t.Due?.ToString("yyyy-MM-dd"),
            ["due_time"] = t.DueTime.HasValue ? DateTime.Today.Add(t.DueTime.Value).ToString("HH:mm") : null,
            ["overdue"] = t.IsOverdue,
            ["reminder"] = t.Reminder?.ToString("yyyy-MM-ddTHH:mm"),
            ["repeat"] = t.Repeat.ToString().ToLowerInvariant(),
            ["in_my_day"] = t.InMyDay,
            ["steps"] = steps,
            ["labels"] = labels,
            ["added_by"] = t.FromAi ? t.Source : "user",
            ["created_at"] = t.CreatedAt.ToLocalTime().ToString("yyyy-MM-ddTHH:mm"),
        };
    }

    private static string Str(JsonObject a, string key)
    {
        var n = a[key];
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            if (v.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        }
        return null;
    }

    private static bool? Bool(JsonObject a, string key)
    {
        if (a[key] is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    private static int? Int(JsonObject a, string key)
    {
        if (a[key] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var j)) return j;
        }
        return null;
    }

    private static IEnumerable<string> Strings(JsonObject a, string key)
    {
        if (a[key] is JsonArray arr)
        {
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) yield return s.Trim();
        }
        else if (a[key] is JsonValue one && one.TryGetValue<string>(out var s1))
        {
            foreach (var part in s1.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }
    }
}
