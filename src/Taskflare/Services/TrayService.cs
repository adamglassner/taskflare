using Forms = System.Windows.Forms;

namespace Taskflare.Services;

/// <summary>The Taskflare icon in the notification area (so reminders and the AI connector keep working after you close the window).</summary>
public static class TrayService
{
    private static Forms.NotifyIcon _icon;

    public static event Action OpenRequested;
    public static event Action QuickAddRequested;
    public static event Action QuitRequested;

    public static void Show()
    {
        if (_icon != null) { _icon.Visible = true; return; }
        System.Drawing.Icon ico = null;
        try { ico = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Taskflare", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("New task…", null, (_, _) => QuickAddRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Taskflare", null, (_, _) => QuitRequested?.Invoke());
        _icon = new Forms.NotifyIcon
        {
            Icon = ico ?? System.Drawing.SystemIcons.Application,
            Text = "Taskflare",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    public static void Hide()
    {
        if (_icon != null) _icon.Visible = false;
    }

    public static void Dispose()
    {
        if (_icon == null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}
