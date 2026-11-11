using Microsoft.Win32;

namespace Taskflare.Services;

/// <summary>"Start Taskflare when I sign in to Windows" (HKCU Run key; starts in the notification area).</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue("Taskflare", $"\"{Environment.ProcessPath}\" --background");
            else if (key.GetValue("Taskflare") != null) key.DeleteValue("Taskflare");
        }
        catch { }
    }
}
