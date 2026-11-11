using System.Globalization;
using System.Text.RegularExpressions;
using Taskflare.Models;

namespace Taskflare.Services;

public sealed class ParsedTask
{
    public string Title = "";
    public DateTime? Due;
    public TimeSpan? Time;
    public Priority Priority;
    public bool Important;
    public Repeat Repeat;
    public TaskList List;
    public List<string> Labels = new();
    /// <summary>What was understood, for the chips under the add box.</summary>
    public List<string> Chips = new();
}

/// <summary>
/// Understands Todoist-style quick add, for example
/// "Pay rent every month on the 1st" is not supported, but these are:
///   "Call mom tomorrow at 5pm #Personal p1"
///   "Submit report fri !!"            "Dentist nov 20 2:30pm"
///   "Water plants every monday"       "Gym tonight *"           "Read @book in 3 days"
/// </summary>
public static class QuickAddParser
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Pre = @"(?<=^|\s)";
    private const string Post = @"(?=$|\s|[,.;!?])";
    private const string DuePrefix = @"(?:(?:on|by|due(?:\s+on)?)\s+)?";

    private static readonly string[] DayNames = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    private const string Weekday =
        @"(monday|mon|tuesday|tues|tue|wednesday|wed|thursday|thurs|thur|thu|friday|fri|saturday|sunday)";

    private const string Month =
        @"(january|jan|february|feb|march|mar|april|apr|may|june|jun|july|jul|august|aug|september|sept|sep|october|oct|november|nov|december|dec)";

    private static readonly Regex RxRepeat = new(Pre + @"(?:every\s+(day|weekday|week|month|year|" + Weekday[1..^1] + @")|(daily|weekly|monthly|yearly|annually|weekdays))" + Post, Opt);
    private static readonly Regex RxInN = new(Pre + @"in\s+(\d{1,3}|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?|months?)" + Post, Opt);
    private static readonly Regex RxNext = new(Pre + @"next\s+(week|month|year)" + Post, Opt);
    private static readonly Regex RxIso = new(Pre + DuePrefix + @"(\d{4})-(\d{1,2})-(\d{1,2})" + Post, Opt);
    private static readonly Regex RxMonthDay = new(Pre + DuePrefix + Month + @"\.?\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(\d{4}))?" + Post, Opt);
    private static readonly Regex RxDayMonth = new(Pre + DuePrefix + @"(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?" + Month + @"(?:,?\s+(\d{4}))?" + Post, Opt);
    private static readonly Regex RxSlash = new(Pre + DuePrefix + @"(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?" + Post, Opt);
    private static readonly Regex RxRelDay = new(Pre + DuePrefix + @"(day after tomorrow|today|tod|tonight|tomorrow|tomorow|tmrw|tmr)" + Post, Opt);
    private static readonly Regex RxWeekday = new(Pre + @"(?:(on|this|next)\s+)?" + Weekday + Post, Opt);
    private static readonly Regex RxTime12 = new(Pre + @"(?:at\s+|@\s*)?(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.|a|p)" + Post, Opt);
    private static readonly Regex RxTime24 = new(Pre + @"(?:at\s+|@\s*)?(\d{1,2}):(\d{2})" + Post, Opt);
    private static readonly Regex RxTimeAt = new(Pre + @"(?:at|@)\s*(\d{1,2})" + Post, Opt);
    private static readonly Regex RxTimeWord = new(Pre + @"(?:at\s+)?(noon|midday|midnight)" + Post, Opt);
    private static readonly Regex RxPriority = new(Pre + @"(?:p([1-4])|!([1-3])|(!!!|!!))" + Post, Opt);
    private static readonly Regex RxStar = new(Pre + @"(\*|!important)" + Post, Opt);
    private static readonly Regex RxList = new(Pre + @"#([\p{L}\p{N}_\-]+)" + Post, Opt);
    private static readonly Regex RxLabel = new(Pre + @"@([\p{L}\p{N}_\-]+)" + Post, Opt);

    public static ParsedTask Parse(string text, DateTime now, IEnumerable<TaskList> lists = null, bool smart = true)
    {
        var r = new ParsedTask();
        var s = " " + (text ?? "").Trim() + " ";
        if (!smart)
        {
            r.Title = (text ?? "").Trim();
            return r;
        }
        var today = now.Date;
        bool tonight = false;

        // Lists first (names may contain spaces): "#Work", "#Grocery list"
        if (lists != null)
        {
            foreach (var l in lists.OrderByDescending(l => l.Name.Length))
            {
                var rx = new Regex(Pre + "#" + Regex.Escape(l.Name) + Post, Opt);
                var m = rx.Match(s);
                if (!m.Success) continue;
                r.List = l;
                s = Cut(s, m);
                break;
            }
            if (r.List == null)
            {
                var m = RxList.Match(s);
                if (m.Success)
                {
                    var l = lists.FirstOrDefault(x => x.Name.Replace(" ", "").Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                    if (l != null) { r.List = l; s = Cut(s, m); }
                }
            }
        }

        // Repeats: "every monday", "every day", "weekly"
        var rm = RxRepeat.Match(s);
        if (rm.Success)
        {
            var w = (rm.Groups[1].Success ? rm.Groups[1].Value : rm.Groups[2].Value).ToLowerInvariant();
            switch (w)
            {
                case "day": case "daily": r.Repeat = Repeat.Daily; break;
                case "weekday": case "weekdays": r.Repeat = Repeat.Weekdays; break;
                case "week": case "weekly": r.Repeat = Repeat.Weekly; break;
                case "month": case "monthly": r.Repeat = Repeat.Monthly; break;
                case "year": case "yearly": case "annually": r.Repeat = Repeat.Yearly; break;
                default:
                    r.Repeat = Repeat.Weekly;
                    r.Due = NextWeekday(today, DayIndex(w), allowToday: true);
                    break;
            }
            s = Cut(s, rm);
        }

        // Dates
        if (r.Due == null && TryMatch(RxInN, ref s, out var m1))
        {
            int n = Number(m1.Groups[1].Value);
            var unit = m1.Groups[2].Value.ToLowerInvariant();
            r.Due = unit.StartsWith("day") ? today.AddDays(n) : unit.StartsWith("week") ? today.AddDays(7 * n) : today.AddMonths(n);
        }
        if (r.Due == null && TryMatch(RxNext, ref s, out var m2))
        {
            var unit = m2.Groups[1].Value.ToLowerInvariant();
            r.Due = unit == "week" ? NextWeekday(today, (int)DayOfWeek.Monday, allowToday: false)
                : unit == "month" ? new DateTime(today.Year, today.Month, 1).AddMonths(1)
                : new DateTime(today.Year + 1, 1, 1);
        }
        if (r.Due == null && TryMatch(RxIso, ref s, out var m3))
            r.Due = SafeDate(Int(m3.Groups[1]), Int(m3.Groups[2]), Int(m3.Groups[3]));
        if (r.Due == null && TryMatch(RxMonthDay, ref s, out var m4))
            r.Due = MonthDate(today, MonthIndex(m4.Groups[1].Value), Int(m4.Groups[2]), m4.Groups[3].Success ? Int(m4.Groups[3]) : 0);
        if (r.Due == null && TryMatch(RxDayMonth, ref s, out var m5))
            r.Due = MonthDate(today, MonthIndex(m5.Groups[2].Value), Int(m5.Groups[1]), m5.Groups[3].Success ? Int(m5.Groups[3]) : 0);
        if (r.Due == null && TryMatch(RxSlash, ref s, out var m6))
        {
            int y = m6.Groups[3].Success ? Int(m6.Groups[3]) : 0;
            if (y > 0 && y < 100) y += 2000;
            r.Due = MonthDate(today, Int(m6.Groups[1]), Int(m6.Groups[2]), y);
        }
        if (r.Due == null && TryMatch(RxRelDay, ref s, out var m7))
        {
            var w = m7.Groups[1].Value.ToLowerInvariant();
            if (w == "day after tomorrow") r.Due = today.AddDays(2);
            else if (w is "today" or "tod") r.Due = today;
            else if (w == "tonight") { r.Due = today; tonight = true; }
            else r.Due = today.AddDays(1);
        }
        if (r.Due == null && TryMatch(RxWeekday, ref s, out var m8))
        {
            bool next = m8.Groups[1].Value.Equals("next", StringComparison.OrdinalIgnoreCase);
            r.Due = NextWeekday(today, DayIndex(m8.Groups[2].Value), allowToday: !next);
        }

        // Times
        if (TryMatch(RxTime12, ref s, out var t1))
        {
            int h = Int(t1.Groups[1]), min = t1.Groups[2].Success ? Int(t1.Groups[2]) : 0;
            bool pm = t1.Groups[3].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            if (h >= 1 && h <= 12 && min < 60)
                r.Time = new TimeSpan(h % 12 + (pm ? 12 : 0), min, 0);
        }
        else if (TryMatch(RxTime24, ref s, out var t2))
        {
            int h = Int(t2.Groups[1]), min = Int(t2.Groups[2]);
            if (h < 24 && min < 60) r.Time = new TimeSpan(h, min, 0);
        }
        else if (TryMatch(RxTimeWord, ref s, out var t3))
        {
            r.Time = t3.Groups[1].Value.Equals("midnight", StringComparison.OrdinalIgnoreCase) ? new TimeSpan(23, 59, 0) : new TimeSpan(12, 0, 0);
        }
        else if (TryMatch(RxTimeAt, ref s, out var t4))
        {
            int h = Int(t4.Groups[1]);
            if (h < 24) r.Time = new TimeSpan(h >= 1 && h <= 6 ? h + 12 : h, 0, 0);
        }
        if (tonight && r.Time == null) r.Time = new TimeSpan(20, 0, 0);
        if (r.Time != null && r.Due == null)
            r.Due = now.TimeOfDay > r.Time.Value ? today.AddDays(1) : today;
        if (r.Repeat != Repeat.None && r.Due == null) r.Due = today;

        // Priority, important, labels
        if (TryMatch(RxPriority, ref s, out var p))
        {
            if (p.Groups[1].Success) r.Priority = p.Groups[1].Value switch { "1" => Priority.High, "2" => Priority.Medium, "3" => Priority.Low, _ => Priority.None };
            else if (p.Groups[2].Success) r.Priority = p.Groups[2].Value switch { "1" => Priority.High, "2" => Priority.Medium, _ => Priority.Low };
            else r.Priority = p.Groups[3].Value == "!!!" ? Priority.High : Priority.Medium;
        }
        if (TryMatch(RxStar, ref s, out _)) r.Important = true;
        Match lm;
        while ((lm = RxLabel.Match(s)).Success)
        {
            var label = lm.Groups[1].Value;
            if (!r.Labels.Contains(label, StringComparer.OrdinalIgnoreCase)) r.Labels.Add(label);
            s = Cut(s, lm);
        }

        r.Title = Regex.Replace(s, @"\s+", " ").Trim();
        if (r.Title.Length == 0) r.Title = (text ?? "").Trim();

        // Chips
        if (r.Due != null)
        {
            var d = "Due " + Dates.Friendly(r.Due.Value);
            if (r.Time != null) d += ", " + Dates.Time(r.Time.Value);
            r.Chips.Add(d);
        }
        if (r.Repeat != Repeat.None) r.Chips.Add("Repeats " + r.Repeat.ToString().ToLowerInvariant());
        if (r.Priority != Priority.None) r.Chips.Add(r.Priority == Priority.High ? "High priority" : r.Priority + " priority");
        if (r.Important) r.Chips.Add("Important");
        if (r.List != null) r.Chips.Add("List: " + r.List.Name);
        foreach (var l in r.Labels) r.Chips.Add("@" + l);
        return r;
    }

    /// <summary>A date from the API: "2026-11-20", "tomorrow", "next friday", "nov 20"…</summary>
    public static DateTime? ParseDate(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        string[] formats = { "yyyy-MM-dd", "yyyy-M-d", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "M/d/yyyy" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d.Date;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && text.Contains('T')) return dto.LocalDateTime.Date;
        var p = Parse(text, now);
        return p.Due;
    }

    /// <summary>A time from the API: "17:30", "5pm", "5:30 pm", "noon".</summary>
    public static TimeSpan? ParseTime(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var ts) && ts < TimeSpan.FromDays(1))
            return ts;
        var p = Parse("x " + (char.IsDigit(text[0]) && !text.Contains(':') && !Regex.IsMatch(text, "[ap]", Opt) ? "at " + text : text), now);
        return p.Time;
    }

    /// <summary>A date and time from the API ("2026-11-20T09:00", "tomorrow 9am").</summary>
    public static DateTime? ParseDateTime(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        string[] formats = { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss.fff" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && text.Contains('T')) return dto.LocalDateTime;
        var p = Parse(text, now);
        if (p.Due == null) return null;
        return p.Due.Value.Date + (p.Time ?? new TimeSpan(9, 0, 0));
    }

    // ---------- helpers ----------

    private static bool TryMatch(Regex rx, ref string s, out Match m)
    {
        m = rx.Match(s);
        if (!m.Success) return false;
        s = Cut(s, m);
        return true;
    }

    private static string Cut(string s, Match m) => s[..m.Index] + " " + s[(m.Index + m.Length)..];

    private static int Int(Group g) => int.TryParse(g.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    private static int Number(string w) => w.ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        _ => int.TryParse(w, out var n) ? n : 1,
    };

    private static int DayIndex(string w)
    {
        w = w.ToLowerInvariant();
        for (int i = 0; i < DayNames.Length; i++)
            if (DayNames[i].StartsWith(w[..Math.Min(3, w.Length)])) return i;
        return 1;
    }

    private static int MonthIndex(string w)
    {
        w = w.ToLowerInvariant()[..3];
        string[] m = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        return Array.IndexOf(m, w) + 1;
    }

    private static DateTime NextWeekday(DateTime today, int dow, bool allowToday)
    {
        int ahead = (dow - (int)today.DayOfWeek + 7) % 7;
        if (ahead == 0 && !allowToday) ahead = 7;
        return today.AddDays(ahead);
    }

    private static DateTime? SafeDate(int y, int m, int d)
    {
        if (y < 1900 || y > 3000 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return null;
        return new DateTime(y, m, d);
    }

    /// <summary>Month/day with no year means the next time that date comes round.</summary>
    private static DateTime? MonthDate(DateTime today, int month, int day, int year)
    {
        if (year > 0) return SafeDate(year, month, day);
        var d = SafeDate(today.Year, month, day);
        if (d != null && d < today) d = SafeDate(today.Year + 1, month, day);
        return d;
    }
}
