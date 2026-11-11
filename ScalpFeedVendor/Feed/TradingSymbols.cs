namespace ScalpFeedVendor.Feed;

/// <summary>
/// Symbol names between Quantower's trading side and IBKR. At the connection level Quantower names a
/// symbol by tastytrade's name, a pipe and the part of the connection it came from: "WHLR | tasty",
/// "BRK/B | tasty" (seen in the workspace file). IBKR names share classes with a space: "BRK B".
/// </summary>
internal static class TradingSymbols
{
    private const string Separator = " | ";

    public static string ToIbkr(string quantowerId)
    {
        int bar = quantowerId.IndexOf(Separator, StringComparison.Ordinal);
        return IbkrClient.ToIbkrSymbol(bar >= 0 ? quantowerId[..bar] : quantowerId);
    }

    public static string FromIbkr(string ibkrSymbol, string vendorKey) => ibkrSymbol.Replace(' ', '/') + Separator + vendorKey;
}
