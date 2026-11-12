using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taskflare.Services;

public enum ThemeMode { System, Light, Dark }

public enum SortMode { Manual, Importance, DueDate, Priority, Alphabetical, Created }

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";

    public const string RepoUrl = "https://github.com/adamglassner/taskflare";
}

/// <summary>Preferences. Saved in %APPDATA%\Taskflare\settings.json.</summary>
public sealed class AppSettings
{
    // ---- Appearance ----
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = "Blue";

    // ---- Behaviour ----
    public bool SmartQuickAdd { get; set; } = true;
    public bool AddToTop { get; set; } = false;
    public bool ConfirmDelete { get; set; } = true;
    /// <summary>Sound for reminders.</summary>
    public bool PlaySound { get; set; } = true;
    /// <summary>Sound when you tick off a task.</summary>
    public bool CompleteSound { get; set; } = true;
    public string CompleteSoundName { get; set; } = "Chime";
    public bool RemindAtDueTime { get; set; } = true;
    public bool KeepRunningInTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool TrayHintShown { get; set; }
    public Dictionary<string, SortMode> Sorts { get; set; } = new();
    public Dictionary<string, bool> ShowCompleted { get; set; } = new();
    public string LastView { get; set; } = "myday";

    // ---- AI connector (named "Muse" in 1.0; the names are kept so settings carry over) ----
    public bool MuseEnabled { get; set; } = false;
    public int MusePort { get; set; } = 47811;
    public string MuseKey { get; set; } = "";
    public bool MuseAllowEdits { get; set; } = true;
    public bool MuseNotify { get; set; } = true;
    public bool MuseAutoTunnel { get; set; } = false;
    public string MuseLastPublicUrl { get; set; } = "";

    // ---- Window ----
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 1100;
    public double WinHeight { get; set; } = 720;
    public bool WinMaximized { get; set; }

    public static string NewKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes).Replace('+', 'x').Replace('/', 'y').TrimEnd('=');
    }
}

public static class SettingsService
{
    /// <summary>%APPDATA%\Taskflare. The self-test points this at a temporary folder.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Taskflare");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static event Action Changed;

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s != null) Current = s;
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            Current = new AppSettings();
        }
        Sanitize(Current);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, true);
        }
        catch { /* best effort */ }
    }

    /// <summary>Save and let everything re-apply settings.</summary>
    public static void Commit()
    {
        Sanitize(Current);
        Save();
        Changed?.Invoke();
    }

    private static void Sanitize(AppSettings s)
    {
        s.Sorts ??= new();
        s.ShowCompleted ??= new();
        if (string.IsNullOrWhiteSpace(s.MuseKey) || s.MuseKey.Length < 16) s.MuseKey = AppSettings.NewKey();
        if (s.MusePort < 1024 || s.MusePort > 65535) s.MusePort = 47811;
        if (double.IsNaN(s.WinWidth) || s.WinWidth < 640) s.WinWidth = 1100;
        if (double.IsNaN(s.WinHeight) || s.WinHeight < 460) s.WinHeight = 720;
        s.LastView ??= "myday";
        s.Accent ??= "Blue";
        s.CompleteSoundName ??= "Chime";
    }
}
