using System.Text;
using Media = System.Windows.Media;

namespace ZSlayerCommandCenter.Launcher;

/// <summary>
/// Synthesizes and plays the watchdog boot sound sequence in C#,
/// timed to the wolf head build animation phases.
/// Pre-renders + pre-loads during startup for zero-latency playback.
/// </summary>
public static class BootSound
{
    private const int SampleRate = 44100;
    private const double Duration = 3.1; // end before animation settles to avoid feeling late

    private static Media.MediaPlayer? _mediaPlayer;

    private enum Ramp { Set, Linear, Exp }
    private record struct Pt(double Time, double Value, Ramp Type);

    private record Voice(
        double Start, double Stop,
        Pt[] Freq,
        Pt[] Gain,
        double Detune = 0
    );

    /// <summary>Pre-render WAV and pre-load MediaPlayer. Call during startup.</summary>
    public static void PreRender()
    {
        try
        {
            var wav = Render();
            var tempFile = Path.Combine(Path.GetTempPath(), "zslayer_boot.wav");
            File.WriteAllBytes(tempFile, wav);

            // Pre-load so Play() is instant (no file I/O at trigger time)
            _mediaPlayer = new Media.MediaPlayer();
            _mediaPlayer.Volume = 1.0;
            _mediaPlayer.Open(new Uri(tempFile, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Watchdog] Boot sound pre-render failed: {ex.Message}");
        }
    }

    /// <summary>Play the pre-loaded boot sound. Near-instant since media is already buffered.</summary>
    public static void Play()
    {
        if (_mediaPlayer == null) return;
        try
        {
            _mediaPlayer.Position = TimeSpan.Zero;
            _mediaPlayer.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Watchdog] Boot sound play failed: {ex.Message}");
        }
    }

    // ── Sound Design ───────────────────────────────────────────
    //
    // Phase 1 — Eyes (0–0.8s): Two tiny 2px dots pulse in darkness.
    //   Sound: barely-there sub-bass hum + faint high shimmer. ~5% energy.
    //
    // Phase 2 — Scan (0.8–1.8s): Wireframe scan line sweeps top→bottom.
    //   Sound: rising sweep tone, electronic/scanning feel. ~30% energy.
    //
    // Phase 3 — Cascade (1.8–2.6s): Hundreds of faces fill in, wolf materializes.
    //   Sound: CLIMAX — rich harmonic chord, sub-bass punch. 100% energy.
    //
    // Phase 4 — Resolve (2.6–3.1s): Fire boost decays, wolf settles.
    //   Sound: warm chord decays quickly, clean ending. ~30%→0% energy.

    private static byte[] Render()
    {
        int n = (int)(SampleRate * Duration);
        var buf = new float[n];

        // ── Phase 1: Eyes (0–0.8s) ──
        // Barely audible — two dots in the dark
        RenderChain(buf, 0.5, 400,
        [
            // Deep sub-bass presence
            new(0, 0.9,
                [new(0, 42, Ramp.Set)],
                [new(0, 0, Ramp.Set), new(0.5, 0.02, Ramp.Linear), new(0.9, 0.001, Ramp.Exp)]),
            // Faint high shimmer (like distant stars)
            new(0.2, 0.8,
                [new(0.2, 880, Ramp.Set)],
                [new(0.2, 0, Ramp.Set), new(0.5, 0.003, Ramp.Linear), new(0.8, 0.001, Ramp.Exp)],
                Detune: 7)
        ]);

        // ── Phase 2: Scan (0.8–1.8s) ──
        // Sweep tone rising with the scan line, building energy
        RenderChain(buf, 0.65, 2800,
        [
            // Main sweep — follows the scan line frequency
            new(0.8, 1.9,
                [new(0.8, 100, Ramp.Set), new(1.8, 280, Ramp.Exp)],
                [new(0.8, 0, Ramp.Set), new(1.0, 0.04, Ramp.Linear), new(1.6, 0.05, Ramp.Linear), new(1.9, 0.001, Ramp.Exp)]),
            // Detuned double — slight beating for electronic feel
            new(0.8, 1.85,
                [new(0.8, 102, Ramp.Set), new(1.8, 284, Ramp.Exp)],
                [new(0.8, 0, Ramp.Set), new(1.0, 0.018, Ramp.Linear), new(1.85, 0.001, Ramp.Exp)],
                Detune: 10),
            // Upper harmonic creeping in near the end — preview of cascade
            new(1.3, 1.9,
                [new(1.3, 200, Ramp.Set), new(1.8, 400, Ramp.Exp)],
                [new(1.3, 0, Ramp.Set), new(1.6, 0.012, Ramp.Linear), new(1.9, 0.001, Ramp.Exp)])
        ]);

        // ── Phase 3: Cascade (1.8–2.6s) — THE CLIMAX ──
        // Wolf materializes — maximum harmonic richness and energy
        RenderChain(buf, 0.7, 6000,
        [
            // Fundamental — strong, rising
            new(1.8, 2.75,
                [new(1.8, 165, Ramp.Set), new(2.5, 330, Ramp.Exp)],
                [new(1.8, 0, Ramp.Set), new(1.95, 0.09, Ramp.Linear), new(2.4, 0.09, Ramp.Set), new(2.75, 0.002, Ramp.Exp)]),
            // Perfect fifth — power chord
            new(1.85, 2.7,
                [new(1.85, 248, Ramp.Set), new(2.5, 495, Ramp.Exp)],
                [new(1.85, 0, Ramp.Set), new(2.0, 0.06, Ramp.Linear), new(2.4, 0.06, Ramp.Set), new(2.7, 0.001, Ramp.Exp)],
                Detune: -3),
            // Octave — brightness and presence
            new(1.9, 2.65,
                [new(1.9, 330, Ramp.Set), new(2.5, 660, Ramp.Exp)],
                [new(1.9, 0, Ramp.Set), new(2.05, 0.035, Ramp.Linear), new(2.35, 0.035, Ramp.Set), new(2.65, 0.001, Ramp.Exp)],
                Detune: 4),
            // High sparkle — the edge flash as faces appear
            new(1.95, 2.55,
                [new(1.95, 660, Ramp.Set), new(2.4, 880, Ramp.Exp)],
                [new(1.95, 0, Ramp.Set), new(2.05, 0.015, Ramp.Linear), new(2.55, 0.001, Ramp.Exp)]),
            // Sub-bass punch — weight and impact
            new(1.8, 2.5,
                [new(1.8, 55, Ramp.Set)],
                [new(1.8, 0.03, Ramp.Set), new(2.1, 0.07, Ramp.Linear), new(2.5, 0.001, Ramp.Exp)])
        ]);

        // ── Phase 4: Resolve (2.6–3.1s) ──
        // Brief warm sustain then clean fade — wolf is alive, settling
        RenderChain(buf, 0.4, 2200,
        [
            // Warm root
            new(2.55, 3.05,
                [new(2.55, 220, Ramp.Set)],
                [new(2.55, 0.035, Ramp.Set), new(2.7, 0.03, Ramp.Linear), new(3.05, 0.001, Ramp.Exp)]),
            // Soft fifth
            new(2.58, 2.95,
                [new(2.58, 330, Ramp.Set)],
                [new(2.58, 0.018, Ramp.Set), new(2.7, 0.015, Ramp.Linear), new(2.95, 0.001, Ramp.Exp)]),
            // Sub tail — last thing you hear
            new(2.55, 3.0,
                [new(2.55, 50, Ramp.Set)],
                [new(2.55, 0.025, Ramp.Set), new(3.0, 0.001, Ramp.Exp)])
        ]);

        Normalize(buf, 0.8f);
        return EncodeWav(buf);
    }

    // ── Chain / Voice rendering ────────────────────────────────

    private static void RenderChain(float[] output, double dryGain, double filterFreq, Voice[] voices)
    {
        var chain = new float[output.Length];
        foreach (var v in voices)
            RenderVoice(chain, v);

        for (int i = 0; i < chain.Length; i++)
            chain[i] *= (float)dryGain;

        ApplyLowpass(chain, filterFreq);

        for (int i = 0; i < output.Length; i++)
            output[i] += chain[i];
    }

    private static void RenderVoice(float[] buf, Voice v)
    {
        int startSample = Math.Max(0, (int)(v.Start * SampleRate));
        int stopSample = Math.Min(buf.Length, (int)((v.Stop + 0.05) * SampleRate));
        double detuneRatio = v.Detune != 0 ? Math.Pow(2, v.Detune / 1200.0) : 1.0;
        double phase = 0;

        for (int i = startSample; i < stopSample; i++)
        {
            double t = (double)i / SampleRate;
            double freq = Eval(v.Freq, t) * detuneRatio;
            double gain = Eval(v.Gain, t);
            buf[i] += (float)(Math.Sin(2 * Math.PI * phase) * gain);
            phase += freq / SampleRate;
        }
    }

    // ── Automation evaluation ──────────────────────────────────

    private static double Eval(Pt[] pts, double t)
    {
        if (pts.Length == 0) return 0;
        if (t <= pts[0].Time)
            return pts[0].Type == Ramp.Set ? pts[0].Value : 0;

        for (int i = 1; i < pts.Length; i++)
        {
            if (t <= pts[i].Time)
            {
                double prevVal = pts[i - 1].Value;
                double dt = pts[i].Time - pts[i - 1].Time;
                double frac = dt > 0 ? (t - pts[i - 1].Time) / dt : 1;

                return pts[i].Type switch
                {
                    Ramp.Set => prevVal,
                    Ramp.Linear => prevVal + (pts[i].Value - prevVal) * frac,
                    Ramp.Exp => ExpInterp(prevVal, pts[i].Value, frac),
                    _ => prevVal
                };
            }
        }

        return pts[^1].Value;
    }

    private static double ExpInterp(double from, double to, double frac)
    {
        if (from <= 0) from = 0.0001;
        if (to <= 0) to = 0.0001;
        return from * Math.Pow(to / from, frac);
    }

    // ── DSP ────────────────────────────────────────────────────

    private static void ApplyLowpass(float[] buf, double cutoff)
    {
        double omega = 2 * Math.PI * cutoff / SampleRate;
        double sinW = Math.Sin(omega);
        double cosW = Math.Cos(omega);
        double alpha = sinW / (2 * 0.7); // Q = 0.7

        double b0 = (1 - cosW) / 2;
        double b1 = 1 - cosW;
        double b2 = (1 - cosW) / 2;
        double a0 = 1 + alpha;
        double a1 = -2 * cosW;
        double a2 = 1 - alpha;

        b0 /= a0; b1 /= a0; b2 /= a0;
        a1 /= a0; a2 /= a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < buf.Length; i++)
        {
            double x0 = buf[i];
            double y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
            buf[i] = (float)y0;
        }
    }

    private static void Normalize(float[] buf, float target)
    {
        float peak = 0;
        for (int i = 0; i < buf.Length; i++)
            peak = Math.Max(peak, Math.Abs(buf[i]));

        if (peak > 0)
        {
            float scale = target / peak;
            for (int i = 0; i < buf.Length; i++)
                buf[i] *= scale;
        }
    }

    // ── WAV encoding ───────────────────────────────────────────

    private static byte[] EncodeWav(float[] samples)
    {
        int dataSize = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);    // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits per sample

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);

        for (int i = 0; i < samples.Length; i++)
        {
            var s = Math.Clamp(samples[i], -1f, 1f);
            w.Write((short)(s * 32767));
        }

        return ms.ToArray();
    }
}
