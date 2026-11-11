using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Diagnostics for the slow updates seen since 2026-11-09: in most busy minutes one frame or flush takes about
/// 16 ms, one Windows timer tick, and Sleep(1) in a process that hasn't asked for a finer timer takes 15.6 ms.
/// When on, this records each slow piece of work on the feed threads and each slow hand-off into Quantower,
/// every garbage collection pause from .NET's own events, and lock contention, and says once a minute how they
/// line up. Off unless the file diag-stalls is in .scalpfeed when the connection starts.
/// </summary>
internal static class StallWatch
{
    public const string TriggerFile = "diag-stalls";

    private static readonly long SlowStopwatchTicks = Stopwatch.Frequency * 5 / 1000;
    private static readonly long KeepTicks = TimeSpan.FromMinutes(2).Ticks;

    // .NET delivers its runtime events to listeners a little late, so only work older than this is judged.
    private static readonly long SettleTicks = TimeSpan.FromSeconds(5).Ticks;
    private static readonly long SlackTicks = TimeSpan.FromMilliseconds(1).Ticks;
    private static readonly object Gate = new();
    private static readonly List<(long Start, long End, string Where)> Slow = new();
    private static readonly List<(long Start, long End, int Gen, long SuspendTicks)> Pauses = new();

    private static volatile bool on;
    private static GcListener listener;
    private static long pausesHeard;
    private static long lastPausesHeard;
    private static long lastContention;
    private static readonly int[] LastCollections = new int[3];
    private static TimeSpan lastPauseTotal;

    public static bool On => on;

    public static void Start()
    {
        lock (Gate)
        {
            if (on)
                return;

            lastContention = Monitor.LockContentionCount;
            for (int g = 0; g < 3; g++)
                LastCollections[g] = GC.CollectionCount(g);
            lastPauseTotal = GC.GetTotalPauseDuration();
            listener = new GcListener();
            on = true;
        }
    }

    /// <summary>Call right after a piece of work ends, with the Stopwatch time it began.</summary>
    public static void Note(string where, long startStopwatch)
    {
        if (!on)
            return;

        long took = Stopwatch.GetTimestamp() - startStopwatch;
        if (took < SlowStopwatchTicks)
            return;

        long endUtc = DateTime.UtcNow.Ticks;
        long tookUtc = took * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        lock (Gate)
        {
            if (Slow.Count < 5000)
                Slow.Add((endUtc - tookUtc, endUtc, where));
        }
    }

    /// <summary>For tests: a pause as the runtime's events would report it, in UTC ticks.</summary>
    public static void AddPause(long startUtc, long endUtc, int gen, long suspendTicks)
    {
        lock (Gate)
        {
            Pauses.Add((startUtc, endUtc, gen, suspendTicks));
            long cutoff = endUtc - KeepTicks;
            Pauses.RemoveAll(p => p.End < cutoff);
        }
    }

    /// <summary>One line for the log, about the slow work that has settled since the last call.</summary>
    public static string TakeSummary()
    {
        if (!on)
            return null;

        long nowUtc = DateTime.UtcNow.Ticks;
        List<(long Start, long End, string Where)> judged;
        List<(long Start, long End, int Gen, long SuspendTicks)> pauses;
        lock (Gate)
        {
            judged = Slow.Where(s => s.End <= nowUtc - SettleTicks).ToList();
            Slow.RemoveAll(s => s.End <= nowUtc - SettleTicks);
            pauses = Pauses.ToList();
        }

        long contention = Monitor.LockContentionCount;
        var gcs = new int[3];
        for (int g = 0; g < 3; g++)
        {
            int count = GC.CollectionCount(g);
            gcs[g] = count - LastCollections[g];
            LastCollections[g] = count;
        }

        var pauseTotal = GC.GetTotalPauseDuration();
        double pauseMs = (pauseTotal - lastPauseTotal).TotalMilliseconds;
        lastPauseTotal = pauseTotal;
        long contended = contention - lastContention;
        lastContention = contention;
        long heard = Interlocked.Read(ref pausesHeard);
        long heardNow = heard - lastPausesHeard;
        lastPausesHeard = heard;

        var recent = pauses.Where(p => p.End > nowUtc - TimeSpan.FromMinutes(1).Ticks - SettleTicks).ToList();
        double maxPause = recent.Count == 0 ? 0 : recent.Max(p => (p.End - p.Start) / (double)TimeSpan.TicksPerMillisecond);
        double maxSuspend = recent.Count == 0 ? 0 : recent.Max(p => p.SuspendTicks / (double)TimeSpan.TicksPerMillisecond);

        int inPause = 0;
        var outside = new List<string>();
        foreach (var s in judged)
        {
            bool overlaps = pauses.Any(p => s.Start <= p.End + SlackTicks && p.Start <= s.End + SlackTicks);
            if (overlaps)
                inPause++;
            else if (outside.Count < 4)
                outside.Add($"{s.Where} {(s.End - s.Start) / (double)TimeSpan.TicksPerMillisecond:F1}ms");
        }

        string byWhere = string.Join(" ", judged.GroupBy(s => s.Where).OrderBy(g => g.Key)
            .Select(g => $"{g.Key.Replace(' ', '_')}={g.Count()}/{g.Max(s => (s.End - s.Start) / (double)TimeSpan.TicksPerMillisecond):F1}ms"));
        return $"stalls 60s: slow(>=5ms) {judged.Count} [{byWhere}] during_gc_pause={inPause} outside=[{string.Join(", ", outside)}] " +
               $"gcs={gcs[0]}/{gcs[1]}/{gcs[2]} gcPauseMs={pauseMs:F1} pausesHeard={heardNow} maxPauseMs={maxPause:F1} maxSuspendMs={maxSuspend:F1} " +
               $"lockContentions={contended}";
    }

    /// <summary>How long Sleep(1) really takes in this process: 1-2 ms with a fine timer, 15.6 ms without.</summary>
    public static (double MedianMs, double MaxMs) MeasureSleep1()
    {
        var took = new double[21];
        for (int i = 0; i < took.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            took[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(took);
        return (took[took.Length / 2], took[^1]);
    }

    /// <summary>Garbage collection pauses from the runtime's own events: suspend begin to restart end.</summary>
    private sealed class GcListener : EventListener
    {
        private const int GcStart = 1;
        private const int RestartEnd = 3;
        private const int SuspendEnd = 8;
        private const int SuspendBegin = 9;

        private long suspendBegin;
        private long suspendEnd;
        private int gen = -1;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                this.EnableEvents(source, EventLevel.Informational, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            var stamp = e.TimeStamp;
            long t = (stamp.Kind == DateTimeKind.Local ? stamp.ToUniversalTime() : stamp).Ticks;
            switch (e.EventId)
            {
                case SuspendBegin:
                    this.suspendBegin = t;
                    this.suspendEnd = 0;
                    break;
                case SuspendEnd:
                    this.suspendEnd = t;
                    break;
                case GcStart:
                    int at = e.PayloadNames?.IndexOf("Depth") ?? -1;
                    this.gen = at >= 0 && e.Payload?[at] is { } depth ? Convert.ToInt32(depth) : -1;
                    break;
                case RestartEnd when this.suspendBegin != 0:
                    AddPause(this.suspendBegin, t, this.gen, this.suspendEnd > 0 ? this.suspendEnd - this.suspendBegin : 0);
                    Interlocked.Increment(ref pausesHeard);
                    this.suspendBegin = 0;
                    this.gen = -1;
                    break;
            }
        }
    }
}
