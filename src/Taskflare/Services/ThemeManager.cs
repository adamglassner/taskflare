using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Taskflare.Services;

/// <summary>
/// Builds the colour palette in code and swaps it into Application.Resources (controls use DynamicResource,
/// so switching is instant). The sidebar stays deep navy in both modes, like the design; the task pane
/// is white in Light mode and dark slate in Dark mode.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }

    public static readonly (string Name, Color Light, Color Dark)[] Accents =
    {
        ("Blue",   C("#2563EB"), C("#60A5FA")),
        ("Ember",  C("#EA580C"), C("#FB923C")),
        ("Teal",   C("#0F766E"), C("#2DD4BF")),
        ("Violet", C("#7C3AED"), C("#A78BFA")),
        ("Rose",   C("#E11D48"), C("#FB7185")),
        ("Forest", C("#15803D"), C("#4ADE80")),
    };

    public static event Action ThemeChanged;

    private static ResourceDictionary _current;

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int i) return i == 0;
        }
        catch { }
        return false;
    }

    public static void Apply(AppSettings s)
    {
        IsDark = s.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemUsesDark(),
        };

        var d = new ResourceDictionary();
        void B(string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); d[key] = b; d[key + "Color"] = c; }
        void G(string key, string top, string bottom)
        {
            var g = new LinearGradientBrush(C(top), C(bottom), 90);
            g.Freeze();
            d[key] = g;
        }

        var accent = Accents.FirstOrDefault(a => a.Name == s.Accent);
        if (accent.Name == null) accent = Accents[0];
        var acc = IsDark ? accent.Dark : accent.Light;

        // ---- Sidebar (navy in both modes) ----
        if (IsDark)
        {
            G("SidebarBg", "#111A2E", "#0C1322");
            B("SidebarHover", C("#1C2944"));
            B("SidebarSelected", C("#22345A"));
            B("SidebarSearchBg", C("#1A2640"));
            B("SidebarDivider", C("#22304D"));
        }
        else
        {
            G("SidebarBg", "#1E2B4D", "#16213D");
            B("SidebarHover", C("#2A3A62"));
            B("SidebarSelected", C("#31467A"));
            B("SidebarSearchBg", C("#2A3A63"));
            B("SidebarDivider", C("#33446E"));
        }
        B("SidebarText", C("#EEF2FF"));
        B("SidebarTextDim", C("#9AA8C7"));
        B("SidebarAccent", accent.Dark);

        // ---- Task pane ----
        if (IsDark)
        {
            B("WindowBg", C("#121A2B"));
            B("PaneBg", C("#121A2B"));
            B("Text", C("#E6ECF7"));
            B("TextSecondary", C("#9AA6BC"));
            B("TextDim", C("#6B778D"));
            B("Divider", C("#243149"));
            B("RowBg", C("#00000000"));
            B("RowHover", C("#1A2539"));
            B("RowSelected", C("#1F2D47"));
            B("InputBg", C("#18233A"));
            B("InputBorder", C("#2D3B57"));
            B("CardBg", C("#18233A"));
            B("ControlHoverBg", C("#1FFFFFFF"));
            B("ControlPressedBg", C("#2BFFFFFF"));
            B("ButtonBg", C("#22304B"));
            B("ButtonHoverBg", C("#2A3A5A"));
            B("CheckBorder", C("#7D8AA3"));
            B("MenuBg", C("#1B263D"));
            B("MenuHover", C("#26355A"));
            B("MenuBorder", C("#2D3B57"));
            B("ToolTipBg", C("#1B263D"));
            B("DetailBg", C("#162036"));
            B("ScrollThumb", C("#3B4A68"));
            B("ScrollThumbHover", C("#56688C"));
            B("Star", C("#FBBF24"));
            B("Danger", C("#F87171"));
            B("DangerSoft", C("#3A1F2A"));
            B("DangerText", C("#FCA5A5"));
            B("Warning", C("#FBBF24"));
            B("WarningSoft", C("#3A2E17"));
            B("WarningText", C("#FCD34D"));
            B("Info", C("#60A5FA"));
            B("InfoSoft", C("#1B2B4A"));
            B("Success", C("#34D399"));
            B("Overdue", C("#F87171"));
            B("MuseBadge", C("#C4B5FD"));
            B("MuseSoft", C("#2A2350"));
        }
        else
        {
            B("WindowBg", C("#FFFFFF"));
            B("PaneBg", C("#FFFFFF"));
            B("Text", C("#1A1F36"));
            B("TextSecondary", C("#5B647A"));
            B("TextDim", C("#8A93A6"));
            B("Divider", C("#E6E9F0"));
            B("RowBg", C("#00FFFFFF"));
            B("RowHover", C("#F5F7FB"));
            B("RowSelected", C("#EDF2FD"));
            B("InputBg", C("#FFFFFF"));
            B("InputBorder", C("#D5DAE3"));
            B("CardBg", C("#F7F8FB"));
            B("ControlHoverBg", C("#0F000000"));
            B("ControlPressedBg", C("#1A000000"));
            B("ButtonBg", C("#F1F3F8"));
            B("ButtonHoverBg", C("#E6EAF2"));
            B("CheckBorder", C("#8A93A6"));
            B("MenuBg", C("#FFFFFF"));
            B("MenuHover", C("#EEF2FA"));
            B("MenuBorder", C("#DADFE8"));
            B("ToolTipBg", C("#FFFFFF"));
            B("DetailBg", C("#F7F8FB"));
            B("ScrollThumb", C("#C3C9D5"));
            B("ScrollThumbHover", C("#9AA2B3"));
            B("Star", C("#F5A524"));
            B("Danger", C("#E5484D"));
            B("DangerSoft", C("#FDECEC"));
            B("DangerText", C("#FFFFFF"));
            B("Warning", C("#F59E0B"));
            B("WarningSoft", C("#FEF3E2"));
            B("WarningText", C("#FFFFFF"));
            B("Info", C("#3B82F6"));
            B("InfoSoft", C("#E8F0FE"));
            B("Success", C("#16A34A"));
            B("Overdue", C("#DC2626"));
            B("MuseBadge", C("#6D28D9"));
            B("MuseSoft", C("#EFEAFE"));
        }

        // Priority pills are solid in Light mode (like the design) and soft in Dark mode.
        if (IsDark)
        {
            B("HighPillBg", C("#4A1F27"));
            B("HighPillText", C("#FCA5A5"));
            B("MediumPillBg", C("#46351A"));
            B("MediumPillText", C("#FCD34D"));
            B("LowPillBg", C("#1C2F50"));
            B("LowPillText", C("#93C5FD"));
        }
        else
        {
            B("HighPillBg", C("#E5484D"));
            B("HighPillText", C("#FFFFFF"));
            B("MediumPillBg", C("#F59E0B"));
            B("MediumPillText", C("#FFFFFF"));
            B("LowPillBg", C("#DBEAFE"));
            B("LowPillText", C("#1D4ED8"));
        }

        B("Accent", acc);
        B("AccentHover", Blend(acc, IsDark ? Colors.White : Colors.Black, 0.12));
        B("AccentText", IsDark ? C("#0B1220") : Colors.White);
        B("AccentSoft", Color.FromArgb((byte)(IsDark ? 0x40 : 0x1F), acc.R, acc.G, acc.B));
        B("Selection", Color.FromArgb(0x66, acc.R, acc.G, acc.B));
        B("AccentSoftHover", Color.FromArgb((byte)(IsDark ? 0x55 : 0x33), acc.R, acc.G, acc.B));
        B("FocusRing", acc);

        // Names the shared control styles use
        d["ControlBorder"] = d["InputBorder"];
        d["TextDisabled"] = d["TextDim"];
        d["DialogBg"] = d["DetailBg"];
        B("ControlBg", Colors.Transparent);

        var app = Application.Current;
        if (_current != null) app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Insert(0, d);
        _current = d;

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke();
    }

    // ---------- native dark title bar ----------
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    public static void Attach(Window w) => w.SourceInitialized += (_, _) => ApplyTitleBar(w);

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
