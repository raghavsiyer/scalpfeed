using System.Globalization;
using System.Runtime.InteropServices;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// An order a key sends: buy or sell this many shares, limit at the ask (buy) or bid (sell) plus or minus the offset,
/// with this time in force (DAY, GTC or IOC).
/// </summary>
internal sealed record OrderHotkey(string Keys, bool Buy, double Shares, double Offset, string Tif)
{
    /// <summary>Alt+1: buy 1,000 shares, limit at the ask plus 10 cents, immediate or cancel (your requests, 2026-11-18).</summary>
    public static OrderHotkey BuyAskPlusTen { get; } = new("Alt+1", true, 1000, 0.10, "IOC");
}

/// <summary>A depth symbol as a hotkey order sees it: IBKR's symbol, its NBBO bid and ask, and whether it's halted.</summary>
internal readonly record struct HotkeyQuote(string Symbol, double Bid, double Ask, bool Halted);

/// <summary>
/// Order hotkeys (your request, 2026-11-18). Quantower Free has no order placing strategies, and its own hotkeys place
/// at the plain ask or send a fixed preset order, so the plugin catches the key itself and sends the order through
/// IbkrTrader, the paper account's own connection, like an order from Quantower's order entry.
///
/// The stock is the one with a market depth open, since your DOM follows the stock you've picked; with none or
/// several it sends nothing and says why. The price is IBKR's NBBO, at the moment of the key, rounded to IBKR's
/// tick for the price: a cent from a dollar, a hundredth of a cent below. The order is a limit with the key's time in
/// force (Alt+1's is IOC: what doesn't fill at once is cancelled), which IbkrTrader sends with outsideRth for
/// premarket and after hours. Keys only count while a Quantower window is in front; held down, a key sends once.
/// </summary>
internal static class OrderHotkeys
{
    /// <summary>The order for this key on these depth symbols, or why there's none.</summary>
    public static (IbkrOrderTicket Ticket, double Quote, string Problem) Decide(OrderHotkey hotkey, IReadOnlyList<HotkeyQuote> depth)
    {
        if (depth.Count == 0)
            return (null, double.NaN, "no market depth is open, so there's no stock to send it for");
        if (depth.Count > 1)
            return (null, double.NaN, $"{depth.Count} market depths are open ({string.Join(", ", depth.Select(d => d.Symbol))}), so it's not clear which stock");

        var target = depth[0];
        double quote = hotkey.Buy ? target.Ask : target.Bid;
        if (target.Halted)
            return (null, quote, $"{target.Symbol} is halted");
        if (!(quote > 0))
            return (null, quote, $"{target.Symbol} has no IBKR {(hotkey.Buy ? "ask" : "bid")}");

        double limit = LimitPrice(quote, hotkey.Buy ? hotkey.Offset : -hotkey.Offset);
        if (!(limit > 0))
            return (null, quote, $"{target.Symbol}'s limit would be {limit}");

        return (new IbkrOrderTicket(target.Symbol, hotkey.Buy, hotkey.Shares, "LMT", limit, double.NaN, hotkey.Tif), quote, null);
    }

    /// <summary>The quote plus the offset, on IBKR's tick for stocks: $0.01 from a dollar, $0.0001 below.</summary>
    public static double LimitPrice(double quote, double offset)
    {
        double price = quote + offset;
        return Math.Round(price, price >= 1.0 ? 2 : 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>"Alt+1", "Ctrl+Shift+F2" and the like, as RegisterHotKey's modifiers (with no repeat) and virtual key.</summary>
    public static bool TryParseKeys(string keys, out uint modifiers, out uint virtualKey)
    {
        modifiers = HotkeyListener.NoRepeat;
        virtualKey = 0;
        foreach (var raw in keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.ToUpperInvariant();
            if (part is "ALT")
                modifiers |= HotkeyListener.Alt;
            else if (part is "CTRL" or "CONTROL")
                modifiers |= HotkeyListener.Control;
            else if (part is "SHIFT")
                modifiers |= HotkeyListener.Shift;
            else if (virtualKey != 0)
                return false;
            else if (part.Length == 1 && part[0] is (>= '0' and <= '9') or (>= 'A' and <= 'Z'))
                virtualKey = part[0];
            else if (part.Length is 2 or 3 && part[0] == 'F' && int.TryParse(part[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 12)
                virtualKey = (uint)(0x70 + f - 1);
            else
                return false;
        }

        return virtualKey != 0 && (modifiers & ~HotkeyListener.NoRepeat) != 0;
    }
}

/// <summary>
/// System-wide keys, registered with Windows from a thread of its own, one for the whole Quantower process so
/// Quantower's reconnects (a new vendor each time) don't fight over them. A key pressed while another program is in
/// front is dropped, though Windows still keeps it from that program while it's registered.
/// </summary>
internal sealed class HotkeyListener
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint NoRepeat = 0x4000;

    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    /// <summary>The plugin's one listener.</summary>
    public static HotkeyListener Shared { get; } = new();

    private readonly object gate = new();
    private Thread thread;
    private uint threadId;
    private IReadOnlyList<OrderHotkey> keys = [];
    private Action<OrderHotkey> handler;

    /// <summary>Whether a Quantower window is in front. The harness stands in for it.</summary>
    public Func<bool> InFront { get; set; } = IsThisProcessInFront;

    /// <summary>Registers the keys once, and sends each press to the handler, off the listener's thread.</summary>
    public void Start(IReadOnlyList<OrderHotkey> hotkeys, Action<OrderHotkey> onPress)
    {
        Volatile.Write(ref this.handler, onPress);
        lock (this.gate)
        {
            if (this.thread != null)
                return;

            this.keys = hotkeys;
            using var registered = new ManualResetEventSlim();
            this.thread = new Thread(() => this.Run(registered)) { IsBackground = true, Name = "ScalpFeed hotkeys" };
            this.thread.Start();
            registered.Wait(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A reconnect's old vendor lets go, so a press until the new one takes over says it's not connected.</summary>
    public void Release(Action<OrderHotkey> onPress) => Interlocked.CompareExchange(ref this.handler, null, onPress);

    /// <summary>Unregisters the keys and ends the thread. Only the harness needs it; the plugin keeps them while Quantower runs.</summary>
    public void Stop()
    {
        lock (this.gate)
        {
            if (this.thread == null)
                return;

            PostThreadMessage(this.threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
            this.thread.Join(TimeSpan.FromSeconds(2));
            this.thread = null;
        }
    }

    /// <summary>The harness's stand-in for a key press: the same message Windows would post.</summary>
    public bool Press(int index) => PostThreadMessage(this.threadId, WmHotkey, (UIntPtr)(uint)index, IntPtr.Zero);

    private void Run(ManualResetEventSlim registered)
    {
        this.threadId = GetCurrentThreadId();

        // Any message call gives the thread its queue before anyone posts to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        for (int i = 0; i < this.keys.Count; i++)
        {
            var key = this.keys[i];
            if (!OrderHotkeys.TryParseKeys(key.Keys, out uint modifiers, out uint virtualKey))
                Log.Info($"[hotkey] {key.Keys} isn't a key this plugin understands");
            else if (!RegisterHotKey(IntPtr.Zero, i, modifiers, virtualKey))
                Log.Info($"[hotkey] {key.Keys} couldn't be registered: another program has it (Windows error {Marshal.GetLastWin32Error()})");
            else
                Log.Info($"[hotkey] {key.Keys}: {(key.Buy ? "buy" : "sell")} {key.Shares:#,0} shares, {key.Tif} limit at the {(key.Buy ? "ask plus" : "bid minus")} {key.Offset}, for the stock with the market depth open");
        }

        registered.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message != WmHotkey)
                continue;

            int index = (int)message.WParam;
            if (index < 0 || index >= this.keys.Count)
                continue;

            var key = this.keys[index];
            if (!this.InFront())
            {
                Log.Info($"[hotkey] {key.Keys} pressed with another program in front, so nothing was sent");
                continue;
            }

            var onPress = Volatile.Read(ref this.handler);
            if (onPress == null)
            {
                Log.Info($"[hotkey] {key.Keys} pressed while the connection is down, so nothing was sent");
                FeedAlarm.PlayWarning();
                continue;
            }

            _ = Task.Run(() => onPress(key));
        }

        for (int i = 0; i < this.keys.Count; i++)
            UnregisterHotKey(IntPtr.Zero, i);
    }

    private static bool IsThisProcessInFront()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint process) != 0 && process == (uint)Environment.ProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
