using System.IO;
using System.Media;

namespace Taskflare.Services;

/// <summary>
/// The little sound when you tick off a task. The sounds are made in code (short synthesised tones),
/// so there are no audio files to ship.
/// </summary>
public static class SoundService
{
    public static readonly string[] Names = { "Chime", "Ding", "Pop", "Bubble" };

    private static readonly Dictionary<string, byte[]> Cache = new();
    private const int Rate = 44100;

    /// <summary>Plays the "task done" sound if it's turned on in Settings.</summary>
    public static void PlayComplete()
    {
        var s = SettingsService.Current;
        if (!s.CompleteSound) return;
        Play(s.CompleteSoundName);
    }

    public static void Play(string name)
    {
        try
        {
            var player = new SoundPlayer(new MemoryStream(Wav(name)));
            player.Play(); // plays in the background
        }
        catch { /* no audio device */ }
    }

    /// <summary>The sound as a WAV file in memory.</summary>
    public static byte[] Wav(string name)
    {
        if (!Names.Contains(name)) name = Names[0];
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var b)) return b;
            var samples = name switch
            {
                "Ding" => Bell(1046.5, 0.65, 0.30),
                "Pop" => Pop(),
                "Bubble" => Bubble(),
                _ => Concat(Bell(1318.5, 0.16, 0.26, tail: false), Bell(1760.0, 0.55, 0.28)), // E6 then A6, like a happy "ta-da"
            };
            b = ToWav(samples);
            Cache[name] = b;
            return b;
        }
    }

    // ---------- synthesis ----------

    /// <summary>A soft bell: a sine with a few inharmonic partials and an exponential fade.</summary>
    private static float[] Bell(double freq, double seconds, double volume, bool tail = true)
    {
        int n = (int)(Rate * seconds);
        var s = new float[n];
        double decay = tail ? 5.5 / seconds : 2.5 / seconds;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double attack = Math.Min(1.0, t / 0.004);
            double env = attack * Math.Exp(-decay * t);
            double v = Math.Sin(2 * Math.PI * freq * t)
                       + 0.35 * Math.Sin(2 * Math.PI * freq * 2.0 * t) * Math.Exp(-decay * 1.5 * t)
                       + 0.15 * Math.Sin(2 * Math.PI * freq * 3.01 * t) * Math.Exp(-decay * 2.5 * t);
            s[i] = (float)(volume * env * v / 1.5);
        }
        if (!tail) FadeOut(s, 0.02);
        return s;
    }

    private static float[] Pop()
    {
        int n = (int)(Rate * 0.09);
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double f = 900 * Math.Exp(-t * 28) + 220;
            phase += 2 * Math.PI * f / Rate;
            double env = Math.Min(1.0, t / 0.002) * Math.Exp(-t * 45);
            s[i] = (float)(0.45 * env * Math.Sin(phase));
        }
        return s;
    }

    private static float[] Bubble()
    {
        int n = (int)(Rate * 0.16);
        var s = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double f = 380 + 1400 * (t / 0.16) * (t / 0.16);
            phase += 2 * Math.PI * f / Rate;
            double env = Math.Min(1.0, t / 0.006) * Math.Exp(-t * 18);
            s[i] = (float)(0.38 * env * Math.Sin(phase));
        }
        return s;
    }

    private static float[] Concat(params float[][] parts)
    {
        var all = new float[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { Array.Copy(p, 0, all, at, p.Length); at += p.Length; }
        return all;
    }

    private static void FadeOut(float[] s, double seconds)
    {
        int n = Math.Min(s.Length, (int)(Rate * seconds));
        for (int i = 0; i < n; i++) s[s.Length - 1 - i] *= (float)i / n;
    }

    private static byte[] ToWav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);      // PCM
        w.Write((short)1);      // mono
        w.Write(Rate);
        w.Write(Rate * 2);      // byte rate
        w.Write((short)2);      // block align
        w.Write((short)16);     // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var f in samples) w.Write((short)Math.Clamp(f * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
