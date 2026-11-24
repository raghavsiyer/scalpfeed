namespace ScalpFeedVendor.Feed;

/// <summary>Writing a file the connection keeps and a chart reads.</summary>
internal static class CacheFile
{
    /// <summary>
    /// Writes the text beside the file, then swaps it in, so a reader never sees half of it. Windows refuses the swap
    /// while a chart has the old one open to read, which is for a few milliseconds, so it's tried a few times.
    /// </summary>
    public static void Replace(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && (ex is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(20);
            }
        }
    }
}

/// <summary>A file as a chart reads it: again when it changes, looked at no more than every 2 seconds.</summary>
internal sealed class WatchedFile<T>(string path, Func<string, T> read, Func<long> nowMs = null)
    where T : class
{
    private readonly object gate = new();
    private T value;
    private DateTime fileTimeUtc;

    // Null until the first look. The split marker's was long.MinValue, and now minus that overflows to a negative
    // number, so until 2026-11-24 its file was never looked at.
    private long? checkedMs;

    public T Value
    {
        get
        {
            lock (this.gate)
            {
                long now = nowMs?.Invoke() ?? Environment.TickCount64;
                if (this.checkedMs is not long at || now - at >= 2000)
                {
                    this.checkedMs = now;
                    var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                    if (this.value == null || time != this.fileTimeUtc)
                    {
                        this.fileTimeUtc = time;
                        this.value = read(path);
                    }
                }

                return this.value;
            }
        }
    }
}
