using System.Collections.Generic;
using ScalpFeedVendor.Tastytrade;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace ScalpFeedVendor;

/// <summary>
/// The one connection Quantower sees. tastytrade fills MultiVendor's symbol, quote and history
/// slots and supplies the tape and chart history. IBKR supplies bid, ask, depth and the tape's
/// other venues through the same quote slot, and will add execution; IBKR-SEAMS.md lists where
/// each piece plugs in and what must be true before real orders go through it.
/// </summary>
public sealed class ScalpFeedVendor : MultiVendor
{
    private const string VendorName = "ScalpFeed";
    private const string TastytradeKey = "tasty";

    private TastytradeMarketVendor marketData;

    public static VendorMetaData GetVendorMetaData() => new()
    {
        VendorName = VendorName,
        VendorDescription = "tastytrade tape and charts, IBKR bid, ask and depth. Data only, no trading.",
        GetDefaultConnections = () => new List<ConnectionInfo>
        {
            CreateDefaultConnectionInfo(typeof(ScalpFeedVendor).Assembly, VendorName, VendorName, "scalpfeed.svg", null, true)
        },
        GetConnectionParameters = () => new List<SettingItem>()
    };

    public override ConnectionResult Connect(ConnectRequestParameters connectRequestParameters)
    {
        if (this.marketData == null)
        {
            this.marketData = new TastytradeMarketVendor();
            ((IVendor)this.marketData).Key = TastytradeKey;
            this.RegisterSymbolVendor(this.marketData);
            this.RegisterQuoteVendor(this.marketData);
            this.RegisterHistoryVendor(this.marketData);
        }

        return base.Connect(connectRequestParameters);
    }
}
