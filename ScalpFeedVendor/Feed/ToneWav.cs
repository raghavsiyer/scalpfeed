namespace ScalpFeedVendor.Feed;

/// <summary>
/// Tones as an in-memory WAV (16-bit mono, 44.1 kHz), for FeedAlarm: Windows' Beep has no volume, so the alarm
/// is played as a sound at a set loudness instead (your request, 2026-11-20: quieter). Each tone is a sine with a
/// 5 ms fade at each end, so they don't click.
/// </summary>
internal static class ToneWav
{
    public const int SampleRate = 44_100;
    private const int FadeSamples = SampleRate / 200;

    /// <param name="loudness">The peak as a share of full scale, 0 to 1.</param>
    public static byte[] Build((uint Hz, uint Ms)[] tones, double loudness)
    {
        int samples = 0;
        foreach (var (_, ms) in tones)
            samples += (int)(ms * SampleRate / 1000);

        int dataBytes = samples * 2;
        var wav = new byte[44 + dataBytes];
        using (var w = new BinaryWriter(new MemoryStream(wav)))
        {
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(SampleRate);
            w.Write(SampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(dataBytes);

            double peak = Math.Clamp(loudness, 0, 1) * short.MaxValue;
            foreach (var (hz, ms) in tones)
            {
                int n = (int)(ms * SampleRate / 1000);
                for (int i = 0; i < n; i++)
                {
                    double fade = Math.Min(1, Math.Min(i, n - 1 - i) / (double)FadeSamples);
                    w.Write((short)Math.Round(peak * fade * Math.Sin(2 * Math.PI * hz * i / SampleRate)));
                }
            }
        }

        return wav;
    }
}
