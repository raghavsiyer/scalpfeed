namespace ScalpFeedVendor.Feed;

/// <summary>
/// How an indicator gets the shares traded at each price from the connection. Quantower loads the two separately, so
/// a type of ours isn't the same type in both; only .NET's own cross, as one function kept in the process's data.
/// </summary>
internal static class VolumeAtPriceShare
{
    private const string Key = "ScalpFeed.VolumeAtPrice.1";

    /// <summary>
    /// Null until the connection has started (or while one from before 2026-11-24 is loaded). Takes the symbol's
    /// Quantower id, a price step, a time to start from (Unix milliseconds) and what to call with the rows, and returns
    /// what stops them. It's called with true and every row so far once they've loaded, or have again; then with false
    /// and the rows that changed. Each call also says from when the rows are complete. A row is the shares traded in
    /// one period (its start and length in milliseconds) within one half step: Level counts half steps of price.
    /// </summary>
    public static Func<string, double, long, Action<bool, long, (long TimeMs, long LengthMs, long Level, double Volume)[]>, IDisposable> Offered
    {
        get => AppDomain.CurrentDomain.GetData(Key) as Func<string, double, long, Action<bool, long, (long TimeMs, long LengthMs, long Level, double Volume)[]>, IDisposable>;
        set => AppDomain.CurrentDomain.SetData(Key, value);
    }
}
