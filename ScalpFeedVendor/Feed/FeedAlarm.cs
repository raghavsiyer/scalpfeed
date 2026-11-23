using System.Runtime.InteropServices;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Sound for a feed problem, because a warning that only reaches a notification panel is easy
/// to miss mid-trade. It repeats until a real quote arrives. Static because Quantower builds a
/// new vendor for every reconnect, and an alarm has to outlive those. Never call from the data
/// path; the tones block their own worker thread for about a second. Muted (<see cref="Muted"/>).
/// </summary>
internal static class FeedAlarm
{
    /// <summary>
    /// No sound at all, for any warning (your request, 2026-11-23: no sounds on any notification). The pop-ups and
    /// the alarm's log lines carry on as before; false brings the sounds back unchanged.
    /// </summary>
    public static bool Muted { get; set; } = true;

    private static readonly (uint Hz, uint Ms)[] WarningTones =
        [(1000, 150), (700, 150), (1000, 150), (700, 150), (1000, 150), (700, 150)];

    private static readonly (uint Hz, uint Ms)[] AllClearTones = [(600, 120), (900, 180)];

    /// <summary>
    /// How loud the tones are, as a share of full scale (your request, 2026-11-20: quieter). They used to be Windows'
    /// Beep, which has no volume; as a sound they also follow Quantower's slider in the Windows volume mixer.
    /// </summary>
    public const double Loudness = 0.25;

    private static readonly byte[] WarningSound = ToneWav.Build(WarningTones, Loudness);
    private static readonly byte[] AllClearSound = ToneWav.Build(AllClearTones, Loudness);

    private static readonly object Gate = new();
    private static Timer repeatTimer;
    private static DateTime lastKeepAliveUtc;
    private static int playing;
    private static bool soundFailureLogged;

    public static TimeSpan RepeatEvery { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// With no sign anyone is still working on the feed, as when the user disconnected,
    /// stop rather than ring forever. Quantower's retries arrive well inside this.
    /// </summary>
    public static TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Replaces the speaker in tests. The argument is true for a warning.</summary>
    public static Action<bool> TestPlayer { get; set; }

    public static bool IsActive
    {
        get
        {
            lock (Gate)
                return repeatTimer != null;
        }
    }

    public static void Raise(string why)
    {
        lock (Gate)
        {
            lastKeepAliveUtc = DateTime.UtcNow;
            if (repeatTimer != null)
                return;
            repeatTimer = new Timer(OnRepeat, null, RepeatEvery, RepeatEvery);
        }

        Log.Info($"Alarm on: {why}");
        PlayWarning();
    }

    /// <summary>Says the problem is still being worked on, so keep sounding.</summary>
    public static void KeepAlive()
    {
        lock (Gate)
            lastKeepAliveUtc = DateTime.UtcNow;
    }

    /// <summary>Ends the alarm and sounds the all-clear. Does nothing if it isn't on.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            if (repeatTimer == null)
                return;
            repeatTimer.Dispose();
            repeatTimer = null;
        }

        Log.Info("Alarm off: data is flowing");
        PlayAllClear();
    }

    public static void PlayWarning() => Play(WarningSound, true);

    public static void PlayAllClear() => Play(AllClearSound, false);

    private static void OnRepeat(object state)
    {
        try
        {
            lock (Gate)
            {
                if (repeatTimer == null)
                    return;

                if (DateTime.UtcNow - lastKeepAliveUtc > GiveUpAfter)
                {
                    repeatTimer.Dispose();
                    repeatTimer = null;
                    Log.Info("Alarm stopped: nothing is working on the feed any more");
                    return;
                }
            }

            PlayWarning();
        }
        catch (Exception ex)
        {
            // An exception escaping a timer callback would take Quantower down with it.
            Log.Info($"Alarm repeat failed: {ex.Message}");
        }
    }

    private static void Play(byte[] sound, bool warning)
    {
        if (Muted)
            return;

        var test = TestPlayer;
        if (test != null)
        {
            test(warning);
            return;
        }

        // Tones overlapping would just sound broken, so one set plays at a time.
        if (Interlocked.CompareExchange(ref playing, 1, 0) != 0)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                // Synchronous, so the sound's bytes stay in use only until it has played.
                if (!PlaySound(sound, IntPtr.Zero, SoundMemory | SoundSync | SoundNoDefault) && !soundFailureLogged)
                {
                    soundFailureLogged = true;
                    Log.Info($"Windows refused to play the alarm sound: error {Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                Volatile.Write(ref playing, 0);
            }
        });
    }

    private const uint SoundSync = 0x0000;
    private const uint SoundNoDefault = 0x0002;
    private const uint SoundMemory = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
}
