namespace ScalpFeedVendor.Feed;

/// <summary>
/// Which of tastytrade's symbol search hits Quantower's search box gets. tastytrade's search matches every symbol
/// that starts with what's typed and files warrants as equities, so typing BNAI also brought BNAIW, "Brand Engagement
/// Network Inc. - Warrant" (2026-11-23). Warrants are left out (your request, 2026-11-23), told by tastytrade's
/// description, unless their exact symbol is typed.
/// </summary>
internal static class SearchFilter
{
    public static bool Shows(string symbol, string description, string typed) =>
        !IsWarrant(description) || string.Equals(symbol, typed?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool IsWarrant(string description) =>
        description != null && description.Contains("warrant", StringComparison.OrdinalIgnoreCase);
}
