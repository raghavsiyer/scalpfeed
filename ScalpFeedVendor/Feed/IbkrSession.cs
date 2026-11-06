namespace ScalpFeedVendor.Feed;

/// <summary>
/// Keeps the Gateway connection across Quantower's reconnects. Quantower builds a new vendor for
/// each one, including the deliberate reconnect that reloads charts after a tastytrade gap, and
/// that mustn't interrupt IBKR's quotes and depth. So the client outlives any one vendor, and is
/// only shut down once nobody has come back for it.
/// </summary>
internal static class IbkrSession
{
    private static readonly TimeSpan ReleaseAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResyncWindow = TimeSpan.FromSeconds(3);
    private static readonly object Gate = new();

    private static IbkrClient client;
    private static IIbkrSink owner;
    private static Timer releaseTimer;

    /// <summary>Returns the client, or null if IBKR is turned off in ibkr.json.</summary>
    public static IbkrClient Attach(IIbkrSink sink, IbkrConfig config = null)
    {
        lock (Gate)
        {
            releaseTimer?.Dispose();
            releaseTimer = null;

            if (client == null)
            {
                config ??= IbkrConfig.Load();
                if (!config.Enabled)
                {
                    Log.Info("[ibkr] turned off in ibkr.json");
                    return null;
                }

                client = new IbkrClient(config);
                client.SetSink(sink);
                client.Start();
            }
            else
            {
                Log.Info("[ibkr] new vendor took over the connection");
                client.BeginResync(ResyncWindow);
                client.SetSink(sink);
            }

            owner = sink;
            return client;
        }
    }

    public static void Detach(IIbkrSink sink)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(owner, sink))
                return;

            owner = null;
            client?.SetSink(null);
            releaseTimer?.Dispose();
            releaseTimer = new Timer(_ => Release(), null, ReleaseAfter, Timeout.InfiniteTimeSpan);
        }
    }

    private static void Release()
    {
        IbkrClient stopping;
        lock (Gate)
        {
            if (owner != null || client == null)
                return;

            stopping = client;
            client = null;
        }

        Log.Info("[ibkr] nobody came back, closing the connection");
        try
        {
            stopping.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            // A timer callback must never throw, or it takes Quantower down with it.
            Log.Info($"[ibkr] stopping: {ex.GetBaseException().Message}");
        }
    }
}
