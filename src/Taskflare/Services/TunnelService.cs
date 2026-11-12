using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Taskflare.Services;

/// <summary>
/// AI assistants run in the cloud, so they can't see "localhost" on your PC. This starts Cloudflare's free
/// cloudflared program in "quick tunnel" mode, which gives the connector a public https address
/// (https://something.trycloudflare.com) that forwards to Taskflare on this PC. No Cloudflare account needed.
/// The address changes each time the tunnel starts, so Taskflare shows the current one to paste into your assistant.
/// </summary>
public static class TunnelService
{
    private static Process _proc;
    private static readonly Regex UrlRx = new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);

    public static string PublicUrl { get; private set; }
    public static string Status { get; private set; } = "Off";
    public static bool IsRunning => _proc != null && !_proc.HasExited;
    public static bool IsDownloading { get; private set; }

    public static event Action Changed;

    public static string OwnCopy => Path.Combine(SettingsService.Folder, "cloudflared.exe");

    public const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    /// <summary>Finds cloudflared.exe: Taskflare's own copy, then where winget or the MSI put it, then PATH.</summary>
    public static string Find()
    {
        var candidates = new List<string> { OwnCopy };
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(pf86, "cloudflared", "cloudflared.exe"));
        candidates.Add(Path.Combine(pf, "cloudflared", "cloudflared.exe"));
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "cloudflared.exe"));
        try
        {
            var pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk, "Cloudflare.cloudflared*"))
                    candidates.AddRange(Directory.GetFiles(d, "cloudflared*.exe", SearchOption.AllDirectories));
        }
        catch { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "cloudflared.exe")); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads cloudflared.exe (from Cloudflare's GitHub releases) into Taskflare's folder.</summary>
    public static async Task<string> DownloadAsync(IProgress<double> progress = null)
    {
        IsDownloading = true;
        Status = "Downloading cloudflared…";
        Changed?.Invoke();
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Taskflare/" + AppInfo.Version);
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            var tmp = OwnCopy + ".download";
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            File.Move(tmp, OwnCopy, true);
            Status = "Off";
            return OwnCopy;
        }
        catch (Exception ex)
        {
            Status = "Couldn't download cloudflared: " + ex.Message;
            return null;
        }
        finally
        {
            IsDownloading = false;
            Changed?.Invoke();
        }
    }

    public static void Start(int port)
    {
        Stop();
        var exe = Find();
        if (exe == null)
        {
            Status = "cloudflared isn't installed yet.";
            Changed?.Invoke();
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("tunnel");
            psi.ArgumentList.Add("--no-autoupdate");
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add($"http://localhost:{port}");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.Exited += (_, _) => Ui(() =>
            {
                if (_proc != p) return;
                _proc = null;
                PublicUrl = null;
                Status = "The public link stopped.";
                Changed?.Invoke();
            });
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            _proc = p;
            PublicUrl = null;
            Status = "Starting the public link…";
        }
        catch (Exception ex)
        {
            Status = "Couldn't start cloudflared: " + ex.Message;
        }
        Changed?.Invoke();
    }

    private static void OnLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var m = UrlRx.Match(line);
        if (!m.Success) return;
        Ui(() =>
        {
            PublicUrl = m.Value;
            Status = "Public link is on.";
            SettingsService.Current.MuseLastPublicUrl = PublicUrl;
            SettingsService.Save();
            Changed?.Invoke();
        });
    }

    public static void Stop()
    {
        var p = _proc;
        _proc = null;
        PublicUrl = null;
        Status = "Off";
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { p.Dispose(); } catch { }
        }
        Changed?.Invoke();
    }

    private static void Ui(Action a)
    {
        try { System.Windows.Application.Current?.Dispatcher.InvokeAsync(a); } catch { }
    }
}
