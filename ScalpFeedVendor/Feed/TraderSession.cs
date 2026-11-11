namespace ScalpFeedVendor.Feed;

/// <summary>
/// Keeps the trading connection across Quantower's reconnects, like IbkrSession does for market data.
/// A reconnect builds a new vendor, including the one that reloads charts after a tastytrade gap, and
/// working orders and positions mustn't blink out with it. So the trader outlives any one vendor and
/// keeps what IBKR last said, and the next vendor reads it from there.
/// </summary>
internal static class TraderSession
{
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromSeconds(10);
    private static readonly object Gate = new();

    private static IbkrTrader trader;
    private static ITraderSink owner;
    private static Timer releaseTimer;

    /// <summary>Returns the trader, or null if IBKR is turned off in ibkr.json.</summary>
    public static IbkrTrader Attach(ITraderSink sink, IbkrConfig config = null)
    {
        lock (Gate)
        {
            releaseTimer?.Dispose();
            releaseTimer = null;

            if (trader == null)
            {
                config ??= IbkrConfig.Load();
                if (!config.Enabled)
                    return null;

                trader = new IbkrTrader(config.Host, config.Port, config.TradingClientId);
                trader.SetSink(sink);
                trader.Start();
                Log.Info($"[trade] starting the paper trading connection, client {config.TradingClientId}");
            }
            else
            {
                trader.SetSink(sink);
            }

            owner = sink;
            return trader;
        }
    }

    public static void Detach(ITraderSink sink)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(owner, sink))
                return;

            owner = null;
            trader?.SetSink(null);
            releaseTimer?.Dispose();
            releaseTimer = new Timer(_ => Release(), null, ReleaseAfter, Timeout.InfiniteTimeSpan);
        }
    }

    private static void Release()
    {
        IbkrTrader stopping;
        lock (Gate)
        {
            if (owner != null || trader == null)
                return;

            stopping = trader;
            trader = null;
        }

        Log.Info("[trade] nobody came back, closing the trading connection");
        try
        {
            stopping.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            // A timer callback must never throw, or it takes Quantower down with it.
            Log.Info($"[trade] stopping: {ex.GetBaseException().Message}");
        }
    }
}
