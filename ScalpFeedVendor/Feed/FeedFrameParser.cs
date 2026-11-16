using System.Text;
using System.Text.Json;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Decodes COMPACT FEED_DATA frames straight from the receive buffer into a reused row.
/// Nothing is allocated per event except a symbol's first appearance. Single threaded.
/// </summary>
internal sealed class FeedFrameParser
{
    private enum Kind : byte { Quote, Trade, TimeAndSale, Summary, Candle }

    private enum Column : byte
    {
        Skip, Symbol, Flags, Index, Time, BidTime, AskTime, Count, Price, Size, Bid, Ask, BidSize, AskSize,
        DayVolume, DayTurnover, Open, High, Low, Close, Volume, PrevClose, PrevVolume, Exchange, Aggressor, Sale, ExtendedHours, ValidTick, Conditions
    }

    private static readonly Dictionary<string, Column> CommonColumns = new()
    {
        ["eventSymbol"] = Column.Symbol,
        ["eventFlags"] = Column.Flags,
        ["index"] = Column.Index,
        ["time"] = Column.Time,
        ["bidTime"] = Column.BidTime,
        ["askTime"] = Column.AskTime,
        ["count"] = Column.Count,
        ["price"] = Column.Price,
        ["size"] = Column.Size,
        ["bidPrice"] = Column.Bid,
        ["askPrice"] = Column.Ask,
        ["bidSize"] = Column.BidSize,
        ["askSize"] = Column.AskSize,
        ["dayVolume"] = Column.DayVolume,
        ["dayTurnover"] = Column.DayTurnover,
        ["dayOpenPrice"] = Column.Open,
        ["dayHighPrice"] = Column.High,
        ["dayLowPrice"] = Column.Low,
        ["dayClosePrice"] = Column.Close,
        ["open"] = Column.Open,
        ["high"] = Column.High,
        ["low"] = Column.Low,
        ["close"] = Column.Close,
        ["volume"] = Column.Volume,
        ["prevDayClosePrice"] = Column.PrevClose,
        ["prevDayVolume"] = Column.PrevVolume,
        ["exchangeCode"] = Column.Exchange,
        ["aggressorSide"] = Column.Aggressor,
        ["type"] = Column.Sale,
        ["extendedTradingHours"] = Column.ExtendedHours,
        ["validTick"] = Column.ValidTick,
        ["exchangeSaleConditions"] = Column.Conditions
    };

    private sealed class Layout
    {
        public Layout(Kind kind, string name)
        {
            this.Kind = kind;
            this.Name = Encoding.UTF8.GetBytes(name);
        }

        public Kind Kind { get; }

        public byte[] Name { get; }

        public Column[] Columns { get; set; } = [];
    }

    private readonly Layout[] layouts;
    private readonly List<FeedSymbol> symbols = new();
    private readonly FeedRow row = new();

    public FeedFrameParser(IReadOnlyDictionary<string, string[]> fields)
    {
        this.layouts =
        [
            new Layout(Kind.Quote, "Quote"),
            new Layout(Kind.TimeAndSale, "TimeAndSale"),
            new Layout(Kind.Trade, "Trade"),
            new Layout(Kind.Summary, "Summary"),
            new Layout(Kind.Candle, "Candle")
        ];

        foreach (var (name, columns) in fields)
            this.UpdateLayout(name, columns);
    }

    /// <summary>Applies the column order the server reports in FEED_CONFIG.</summary>
    public void UpdateLayout(string eventName, IReadOnlyList<string> fields)
    {
        var bytes = Encoding.UTF8.GetBytes(eventName);
        foreach (var layout in this.layouts)
        {
            if (!layout.Name.AsSpan().SequenceEqual(bytes))
                continue;

            var columns = new Column[fields.Count];
            for (int i = 0; i < fields.Count; i++)
                columns[i] = CommonColumns.GetValueOrDefault(fields[i], Column.Skip);
            layout.Columns = columns;
        }
    }

    /// <summary>
    /// Returns false for anything that isn't FEED_DATA, which the caller handles on its
    /// slower control path.
    /// </summary>
    public bool TryParseData(ReadOnlySpan<byte> frame, IFeedSink sink, long arrivalUtcTicks)
    {
        var reader = new Utf8JsonReader(frame);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        bool isData = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                isData = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("FEED_DATA"u8);
                if (!isData)
                    return false;
            }
            else if (reader.ValueTextEquals("data"u8))
            {
                if (!isData)
                    return false;
                reader.Read();
                this.ParseData(ref reader, sink, arrivalUtcTicks);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return isData;
    }

    private void ParseData(ref Utf8JsonReader reader, IFeedSink sink, long arrivalUtcTicks)
    {
        // "data": ["Quote", [row, row, ...], "Trade", [...]]. Rows come flat or nested.
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var layout = reader.TokenType == JsonTokenType.String ? this.FindLayout(ref reader) : null;
            reader.Read();
            if (layout == null || layout.Columns.Length == 0)
            {
                reader.Skip();
                continue;
            }

            this.ParseRows(ref reader, layout, sink, arrivalUtcTicks);
        }
    }

    private Layout FindLayout(ref Utf8JsonReader reader)
    {
        foreach (var layout in this.layouts)
        {
            if (reader.ValueTextEquals(layout.Name))
                return layout;
        }

        return null;
    }

    private void ParseRows(ref Utf8JsonReader reader, Layout layout, IFeedSink sink, long arrivalUtcTicks)
    {
        var columns = layout.Columns;
        int column = 0;
        int depth = 0;
        this.row.Reset();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                depth++;
                continue;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                if (depth == 0)
                    return;
                depth--;
                continue;
            }

            this.SetColumn(columns[column], ref reader);
            if (++column == columns.Length)
            {
                column = 0;
                if (this.row.Symbol != null)
                {
                    this.row.ArrivalUtcTicks = arrivalUtcTicks;
                    Dispatch(layout.Kind, this.row, sink);
                }

                this.row.Reset();
            }
        }
    }

    private static void Dispatch(Kind kind, FeedRow row, IFeedSink sink)
    {
        switch (kind)
        {
            case Kind.Quote:
                sink.OnQuote(row);
                break;
            case Kind.TimeAndSale:
                sink.OnPrint(row);
                break;
            case Kind.Trade:
                sink.OnTrade(row);
                break;
            case Kind.Summary:
                sink.OnSummary(row);
                break;
            case Kind.Candle:
                sink.OnCandle(row);
                break;
        }
    }

    private void SetColumn(Column column, ref Utf8JsonReader reader)
    {
        var r = this.row;
        switch (column)
        {
            case Column.Skip:
                break;
            case Column.Symbol:
                r.Symbol = this.InternSymbol(ref reader);
                break;
            case Column.Flags:
                r.Flags = (int)ReadLong(ref reader);
                break;
            case Column.Index:
                r.Index = ReadLong(ref reader);
                break;
            case Column.Time:
                r.Time = ReadLong(ref reader);
                break;
            case Column.BidTime:
                r.BidTime = ReadLong(ref reader);
                break;
            case Column.AskTime:
                r.AskTime = ReadLong(ref reader);
                break;
            case Column.Count:
                r.Count = ReadLong(ref reader);
                break;
            case Column.Price:
                r.Price = ReadDouble(ref reader);
                break;
            case Column.Size:
                r.Size = ReadDouble(ref reader);
                break;
            case Column.Bid:
                r.Bid = ReadDouble(ref reader);
                break;
            case Column.Ask:
                r.Ask = ReadDouble(ref reader);
                break;
            case Column.BidSize:
                r.BidSize = ReadDouble(ref reader);
                break;
            case Column.AskSize:
                r.AskSize = ReadDouble(ref reader);
                break;
            case Column.DayVolume:
                r.DayVolume = ReadDouble(ref reader);
                break;
            case Column.DayTurnover:
                r.DayTurnover = ReadDouble(ref reader);
                break;
            case Column.Open:
                r.Open = ReadDouble(ref reader);
                break;
            case Column.High:
                r.High = ReadDouble(ref reader);
                break;
            case Column.Low:
                r.Low = ReadDouble(ref reader);
                break;
            case Column.Close:
                r.Close = ReadDouble(ref reader);
                break;
            case Column.Volume:
                r.Volume = ReadDouble(ref reader);
                break;
            case Column.PrevClose:
                r.PrevClose = ReadDouble(ref reader);
                break;
            case Column.PrevVolume:
                r.PrevVolume = ReadDouble(ref reader);
                break;
            case Column.Exchange:
                if (reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 0)
                    r.Exchange = (char)reader.ValueSpan[0];
                break;
            case Column.Aggressor:
                if (reader.TokenType == JsonTokenType.String)
                    r.Aggressor = reader.ValueTextEquals("BUY"u8) ? PrintSide.Buy : reader.ValueTextEquals("SELL"u8) ? PrintSide.Sell : PrintSide.Undefined;
                break;
            case Column.Sale:
                if (reader.TokenType == JsonTokenType.String)
                    r.Sale = reader.ValueTextEquals("CANCEL"u8) ? SaleKind.Cancel : reader.ValueTextEquals("CORRECTION"u8) ? SaleKind.Correction : SaleKind.New;
                break;
            case Column.ExtendedHours:
                r.ExtendedHours = reader.TokenType == JsonTokenType.True;
                break;
            case Column.ValidTick:
                r.ValidTick = reader.TokenType != JsonTokenType.False;
                break;
            case Column.Conditions:
                r.NormalOddLot = reader.TokenType == JsonTokenType.String && PrintFilter.IsNormalOddLot(reader.ValueSpan);
                r.Reopening = reader.TokenType == JsonTokenType.String && PrintFilter.IsReopening(reader.ValueSpan);
                break;
        }
    }

    private FeedSymbol InternSymbol(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            return null;

        // A scalper's list is short, so a scan beats hashing and allocates nothing.
        foreach (var known in this.symbols)
        {
            if (reader.ValueTextEquals(known.Utf8))
                return known;
        }

        var name = reader.GetString();
        var symbol = new FeedSymbol(name, Encoding.UTF8.GetBytes(name));
        this.symbols.Add(symbol);
        return symbol;
    }

    private static double ReadDouble(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : double.NaN;

    private static long ReadLong(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.Number)
            return 0;
        return reader.TryGetInt64(out long value) ? value : (long)reader.GetDouble();
    }
}
