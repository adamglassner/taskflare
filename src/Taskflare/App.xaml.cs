using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Taskflare.Services;

namespace Taskflare;

public partial class App : Application
{
    /// <summary>Set by "--self-test &lt;output.txt&gt; [screenshot folder]" (used by the build to test Taskflare).</summary>
    public static string SelfTestOutput { get; private set; }
    public static string SelfTestShots { get; private set; }
    public static bool IsSelfTest => SelfTestOutput != null;
    internal static string TestName = "(starting)";

    private Mutex _single;
    private EventWaitHandle _showSignal;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        int st = Array.FindIndex(e.Args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        bool background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        if (st >= 0)
        {
            SelfTestOutput = st + 1 < e.Args.Length ? Path.GetFullPath(e.Args[st + 1]) : Path.Combine(Path.GetTempPath(), "taskflare-selftest.txt");
            SelfTestShots = st + 2 < e.Args.Length ? Path.GetFullPath(e.Args[st + 2]) : null;
            // Never touch the real tasks or settings while testing.
            SettingsService.Folder = Path.Combine(Path.GetTempPath(), "TaskflareSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
            background = false;
        }
        else
        {
            // One Taskflare at a time: a second start just brings the first one forward.
            var id = "Taskflare-" + Environment.UserName;
            _single = new Mutex(true, id + "-single", out bool first);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, id + "-show");
            if (!first)
            {
                _showSignal.Set();
                Shutdown();
                return;
            }
            var t = new Thread(() =>
            {
                while (true)
                {
                    try { _showSignal.WaitOne(); } catch { return; }
                    Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.BringToFront());
                }
            }) { IsBackground = true };
            t.Start();
        }

        SettingsService.Load();
        ThemeManager.Apply(SettingsService.Current);
        SettingsService.Changed += () => ThemeManager.Apply(SettingsService.Current);
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (SettingsService.Current.Theme == ThemeMode.System)
                Dispatcher.InvokeAsync(() => ThemeManager.Apply(SettingsService.Current));
        };
        TaskStore.Load();

        var win = new MainWindow();
        MainWindow = win;
        if (!background) win.Show();
        ReminderService.Start();
        if (!IsSelfTest && (background || SettingsService.Current.KeepRunningInTray)) TrayService.Show();
        if (!IsSelfTest && SettingsService.Current.MuseEnabled) await win.StartMuseAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { TaskStore.Flush(); } catch { }
        try { TunnelService.Stop(); } catch { }
        try { MuseServer.StopAsync().Wait(1500); } catch { }
        try { TrayService.Dispose(); } catch { }
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is ResourceReferenceKeyNotFoundException rk)
        {
            if (IsSelfTest)
                try { File.AppendAllText(SelfTestOutput, $"FAIL missing resource '{rk.Key}' during \"{TestName}\"\r\n"); } catch { }
            e.Handled = true;
            return;
        }
        if (IsSelfTest)
        {
            try { File.AppendAllText(SelfTestOutput, $"FAIL unhandled exception during \"{TestName}\": {e.Exception}\r\n"); } catch { }
            e.Handled = true;
            Environment.Exit(99);
            return;
        }
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(Path.Combine(SettingsService.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
        }
        catch { }
        MessageBox.Show(MainWindow, "Taskflare hit an unexpected error:\n\n" + e.Exception.Message, "Taskflare",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
