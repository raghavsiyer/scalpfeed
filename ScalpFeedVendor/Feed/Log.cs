using System.Collections.Concurrent;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Lifecycle logging only. Never call this per tick: the data path counts events and
/// a timer writes them out once a minute.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 5_000_000;

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    private static bool rolled;

    // Deliberately not under AppData. Windows gives packaged apps a private, redirected copy
    // of AppData, so a file written there by a tool running inside one (the Claude desktop
    // app, for instance) would be invisible to Quantower, and the other way round.
    public static string AppDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".scalpfeed");

    public static string FilePath { get; set; } = Path.Combine(AppDir, "scalpfeed.log");

    /// <summary>Also writes to the console, for the test harness.</summary>
    public static bool Echo { get; set; }

    public static void AddSecret(string value)
    {
        if (!string.IsNullOrEmpty(value))
            Secrets[value] = 0;
    }

    public static string Scrub(string text)
    {
        foreach (var secret in Secrets.Keys)
            text = text.Replace(secret, "<redacted>");
        return text;
    }

    public static void Info(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {Scrub(message)}{Environment.NewLine}";
            lock (Gate)
            {
                if (!rolled)
                {
                    rolled = true;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".1", true);
                }

                File.AppendAllText(FilePath, line);
            }

            if (Echo)
                Console.Write(line);
        }
        catch
        {
            // Diagnostics must never affect the feed or Quantower.
        }
    }
}
