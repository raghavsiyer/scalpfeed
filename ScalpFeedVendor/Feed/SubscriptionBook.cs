namespace ScalpFeedVendor.Feed;

/// <summary>
/// What we want subscribed versus what the server has. Quantower drops and re-adds
/// subscriptions within milliseconds when a chart opens; working from a diff means that
/// churn never reaches the feed.
/// </summary>
internal sealed class SubscriptionBook
{
    private readonly object gate = new();
    private readonly HashSet<FeedKey> wanted = new();
    private readonly HashSet<FeedKey> pinned = new();
    private HashSet<FeedKey> sent = new();

    /// <summary>Keeps a subscription on regardless of what the platform asks for.</summary>
    public void Pin(FeedKey key)
    {
        lock (this.gate)
        {
            this.pinned.Add(key);
            this.wanted.Add(key);
        }
    }

    public void Set(FeedKey key, bool on)
    {
        lock (this.gate)
        {
            if (on)
                this.wanted.Add(key);
            else if (!this.pinned.Contains(key))
                this.wanted.Remove(key);
        }
    }

    /// <summary>Forgets every wish except the pinned ones. What was already sent is unchanged.</summary>
    public void ClearUnpinned()
    {
        lock (this.gate)
        {
            this.wanted.Clear();
            this.wanted.UnionWith(this.pinned);
        }
    }

    /// <summary>Returns the changes since the last call and marks them as sent.</summary>
    public bool TakeDiff(out List<FeedKey> add, out List<FeedKey> remove)
    {
        lock (this.gate)
        {
            add = this.wanted.Where(k => !this.sent.Contains(k)).ToList();
            remove = this.sent.Where(k => !this.wanted.Contains(k)).ToList();
            this.sent = new HashSet<FeedKey>(this.wanted);
            return add.Count > 0 || remove.Count > 0;
        }
    }

    /// <summary>A new socket starts empty, so everything we want must be sent again.</summary>
    public List<FeedKey> TakeAll()
    {
        lock (this.gate)
        {
            this.sent = new HashSet<FeedKey>(this.wanted);
            return this.wanted.ToList();
        }
    }
}
