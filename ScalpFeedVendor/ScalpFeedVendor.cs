using System.Collections.Generic;
using System.Threading;
using ScalpFeedVendor.Ibkr;
using ScalpFeedVendor.Tastytrade;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Integration;

namespace ScalpFeedVendor;

/// <summary>
/// The one connection Quantower sees. tastytrade fills MultiVendor's symbol, quote and history
/// slots and supplies the tape and chart history. IBKR supplies bid, ask, depth and the tape's
/// other venues through the same quote slot, and paper trading through the account and order
/// methods below; IBKR-SEAMS.md lists where each piece plugs in and what must be true before
/// real orders go through it.
/// </summary>
public sealed class ScalpFeedVendor : MultiVendor
{
    private const string VendorName = "ScalpFeed";
    private const string TastytradeKey = "tasty";

    private TastytradeMarketVendor marketData;
    private PaperTrading trading;

    public static VendorMetaData GetVendorMetaData() => new()
    {
        VendorName = VendorName,
        VendorDescription = "tastytrade tape and charts, IBKR bid, ask and depth, and IBKR paper trading.",
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

        var result = base.Connect(connectRequestParameters);
        if (this.trading == null)
        {
            this.trading = new PaperTrading(this.PushMessage);
            this.trading.Start();
        }

        return result;
    }

    public override void Disconnect()
    {
        this.trading?.Stop();
        this.trading = null;
        base.Disconnect();
    }

    public override IList<MessageAccount> GetAccounts(CancellationToken token) => this.trading?.GetAccounts() ?? new List<MessageAccount>();

    public override IList<MessageRule> GetRules(CancellationToken token) => this.trading?.GetRules() ?? new List<MessageRule>();

    public override IList<OrderType> GetAllowedOrderTypes(CancellationToken token) => this.trading?.GetAllowedOrderTypes() ?? new List<OrderType>();

    public override IList<MessageOpenOrder> GetPendingOrders(CancellationToken token) => this.trading?.GetPendingOrders() ?? new List<MessageOpenOrder>();

    public override IList<MessageOpenPosition> GetPositions(CancellationToken token) => this.trading?.GetPositions() ?? new List<MessageOpenPosition>();

    public override IList<MessageTrade> GetTrades(TradesHistoryRequestParameters parameters) => this.trading?.GetTrades() ?? new List<MessageTrade>();

    public override TradingOperationResult PlaceOrder(PlaceOrderRequestParameters parameters) =>
        this.trading?.PlaceOrder(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");

    public override TradingOperationResult ModifyOrder(ModifyOrderRequestParameters parameters) =>
        this.trading?.ModifyOrder(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");

    public override TradingOperationResult CancelOrder(CancelOrderRequestParameters parameters) =>
        this.trading?.CancelOrder(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");

    public override TradingOperationResult ClosePosition(ClosePositionRequestParameters parameters) =>
        this.trading?.ClosePosition(parameters) ?? TradingOperationResult.CreateError(parameters.RequestId, "not connected");
}
