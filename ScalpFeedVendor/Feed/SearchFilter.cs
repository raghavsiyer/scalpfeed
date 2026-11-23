namespace ScalpFeedVendor.Feed;

/// <summary>
/// Which of tastytrade's symbol search hits Quantower's search box gets. tastytrade's search matches every symbol
/// that starts with what's typed and files warrants as equities, so typing BNAI also brought BNAIW, "Brand Engagement
/// Network Inc. - Warrant" (2026-11-23). Warrants are left out (your request, 2026-11-23), told by tastytrade's
/// description, unless their exact symbol is typed. It also matches company names ("brand" brought ACCO, BLMN and
/// CAG for "Brands" in theirs), so only tickers that start with what's typed are kept (your request, 2026-11-23).
/// A dot and a slash count as the same (BRK.B, tastytrade's BRK/B).
/// </summary>
internal static class SearchFilter
{
    public static bool Shows(string symbol, string description, string typed)
    {
        string ticker = Normal(symbol), text = Normal(typed);
        return text.Length > 0 && ticker.StartsWith(text, StringComparison.Ordinal) && (!IsWarrant(description) || ticker == text);
    }

    public static bool IsWarrant(string description) =>
        description != null && description.Contains("warrant", StringComparison.OrdinalIgnoreCase);

    private static string Normal(string text) => (text ?? string.Empty).Trim().ToUpperInvariant().Replace('.', '/');
}
