using System.Globalization;
using System.Runtime.InteropServices;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// An order a key sends: buy or sell, limit at the ask (buy) or bid (sell) plus or minus the offset, a share of the
/// price (0.05 is 5%), with this time in force (DAY, GTC or IOC). The size is the shares given, or with OfPosition that share of the position it closes
/// (1 the whole position, 0.5 half), whole shares rounded down: a sell the long position, a buy a short one.
/// </summary>
/// <param name="WorkSeconds">
/// With more than zero, how long the order works from 9:30 to 16:00 ET before what's left is cancelled, as DAY, instead
/// of being IOC (HotkeyBuys).
/// </param>
internal sealed record OrderHotkey(string Keys, bool Buy, double Shares, double Offset, string Tif, double OfPosition = 0, double WorkSeconds = 0)
{
    /// <summary>How long a buy key's order works in regular hours: 1 second (your choice, 2026-11-24, on paper). It was IOC.</summary>
    public const double BuyWorkSeconds = 1;

    /// <summary>
    /// How far through the quote every key's limit goes: 2.5% of the price (your choice, 2026-11-19, after 5% the
    /// same day). It was 10 cents, which on VIVS at $0.25 was 40% past the ask, and IBKR refused each such buy ("We
    /// cannot accept an order at a limit price at or more aggressive than 0.2752686", about 8% past it).
    /// </summary>
    public const double OffsetShare = 0.025;

    /// <summary>The most the offset goes through the quote, whatever the price: 15 cents (your request, 2026-11-19, after 20), so from $6 up.</summary>
    public const double MaxOffset = 0.15;

    /// <summary>
    /// How long a position key waits for IBKR to confirm the cancels of the stock's working orders on the other side
    /// before sending anyway. On paper, IBKR's own cancels of orders 101 and 394 went from pending to done in 16-27 ms.
    /// </summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(1);

    /// <summary>Then how long it waits for IBKR's position to count those orders' last fills before going by the one it has.</summary>
    public static readonly TimeSpan PositionWait = TimeSpan.FromMilliseconds(500);

    /// <summary>Alt+1: buy 1,000 shares, limit at the ask plus 2.5%, working for a second (your requests, 2026-11-18, 19 and 24).</summary>
    public static OrderHotkey BuyAsk { get; } = new("Alt+1", true, 1000, OffsetShare, "IOC", WorkSeconds: BuyWorkSeconds);

    /// <summary>Alt+2: buy 2,000 shares, otherwise as Alt+1 (your request, 2026-11-18).</summary>
    public static OrderHotkey BuyTwoThousand { get; } = new("Alt+2", true, 2000, OffsetShare, "IOC", WorkSeconds: BuyWorkSeconds);

    /// <summary>Alt+3: buy 4,000 shares, otherwise as Alt+1 (your request, 2026-11-24).</summary>
    public static OrderHotkey BuyFourThousand { get; } = new("Alt+3", true, 4000, OffsetShare, "IOC", WorkSeconds: BuyWorkSeconds);

    /// <summary>Alt+0: buy 1 share, otherwise as Alt+1 (your request, 2026-11-23).</summary>
    public static OrderHotkey BuyOne { get; } = new("Alt+0", true, 1, OffsetShare, "IOC", WorkSeconds: BuyWorkSeconds);

    /// <summary>Shift+Z: sell the whole position, limit at the bid minus 2.5%, immediate or cancel (your request, 2026-11-18).</summary>
    public static OrderHotkey SellAll { get; } = new("Shift+Z", false, 0, OffsetShare, "IOC", OfPosition: 1);

    /// <summary>Shift+X: sell half the position, limit at the bid minus 2.5%, immediate or cancel (your request, 2026-11-18).</summary>
    public static OrderHotkey SellHalf { get; } = new("Shift+X", false, 0, OffsetShare, "IOC", OfPosition: 0.5);

    /// <summary>"the whole position", "half the position", or the shares.</summary>
    public string Size() => this.OfPosition switch
    {
        <= 0 => $"{this.Shares:#,0} shares",
        1 => "the whole position",
        0.5 => "half the position",
        _ => $"{this.OfPosition:P0} of the position",
    };

    public string Describe() =>
        $"{(this.Buy ? "buy" : "sell")} {this.Size()}, {(this.WorkSeconds > 0 ? $"DAY for {this.WorkSeconds:0.##} s (left working outside 9:30-16:00 ET)" : this.Tif + (this.Tif == "IOC" ? " (DAY outside 9:30-16:00 ET)" : ""))} limit at the {(this.Buy ? "ask plus" : "bid minus")} {this.Offset:0.##%}, at most {OrderHotkey.MaxOffset:0.00}";
}

/// <summary>
/// A depth symbol as a hotkey order sees it: IBKR's symbol, its NBBO bid and ask, whether it's halted, the paper
/// account's position in it (negative when short), and the shares still to fill in its working buy and sell orders.
/// </summary>
/// <param name="LimitDown">The Limit Down its depth shows (LuldBand), or NaN with none.</param>
/// <param name="LimitDownRough">That one was picked up mid-session, so it can be off by up to 1%.</param>
internal readonly record struct HotkeyQuote(string Symbol, double Bid, double Ask, bool Halted, double Position = 0, double WorkingBuys = 0, double WorkingSells = 0, double LimitDown = double.NaN, bool LimitDownRough = false);

/// <summary>
/// Order hotkeys (your requests, 2026-11-18). Quantower Free has no order placing strategies, and its own hotkeys place
/// at the plain ask or send a fixed preset order, so the plugin catches the key itself and sends the order through
/// IbkrTrader, the paper account's own connection, like an order from Quantower's order entry.
///
/// The stock is the one with a market depth open, since your DOM follows the stock you've picked; with none or
/// several it sends nothing and says why. The price is IBKR's NBBO, at the moment of the key, rounded to IBKR's
/// tick for the price: a cent from a dollar, a hundredth of a cent below. A sell never goes below Limit Down (SellFloor),
/// and one IBKR refuses as too far under its market price is sent again just inside what it takes (HotkeySells).
/// The order is a limit with the key's time in
/// force, which IbkrTrader sends with outsideRth for premarket and after hours. The sell keys are IOC (what doesn't
/// fill at once is cancelled) from 9:30 to 16:00 ET on weekdays, and DAY outside it (your choice, 2026-11-18); the buy
/// keys were too, and since 2026-11-24 work for a second in regular hours instead (HotkeyBuys). IBKR
/// ignores outsideRth on an IOC sent to SMART (warning 2109 on every hotkey order that morning), so an IOC wouldn't
/// work premarket, while a DAY limit does and stays working until it fills or you cancel it. A position key takes its
/// share of the position as IBKR last said it, never more than the shares not already in working orders on its side,
/// so presses while earlier ones are working can't go past it. Before that it cancels the stock's working orders on
/// the other side (your choice, 2026-11-19): IBKR reprices an order so it doesn't cross your own resting one, so a
/// Shift+Z while an Alt+1 was still filling on paper died unfilled (LVWR order 394).
/// </summary>
internal static class OrderHotkeys
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly TimeSpan RegularOpen = new(9, 30, 0);
    private static readonly TimeSpan RegularClose = new(16, 0, 0);

    /// <summary>Whether this moment is in the regular session, 9:30 to 16:00 ET on a weekday.</summary>
    public static bool InRegularHours(DateTime utc)
    {
        var et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);
        return et.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && et.TimeOfDay >= RegularOpen && et.TimeOfDay < RegularClose;
    }

    /// <summary>The key's time in force at this moment: an IOC key's order is DAY outside regular hours, and in them too when it works for a while.</summary>
    public static string TifAt(OrderHotkey hotkey, DateTime utc) =>
        hotkey.Tif == "IOC" && (hotkey.WorkSeconds > 0 || !InRegularHours(utc)) ? "DAY" : hotkey.Tif;

    /// <summary>How long the key's order works at this moment before what's left is cancelled; zero is left as it is.</summary>
    public static TimeSpan WorkFor(OrderHotkey hotkey, DateTime utc) =>
        hotkey.WorkSeconds > 0 && InRegularHours(utc) ? TimeSpan.FromSeconds(hotkey.WorkSeconds) : TimeSpan.Zero;

    /// <summary>
    /// The order for this key on these depth symbols at this moment (now when not given), or why there's none.
    /// <paramref name="restriction"/> gives IBKR's reason a stock can't be opened (IbkrRestrictions), or null. A sell's
    /// limit is never under <paramref name="lowest"/>, the lowest price IBKR takes one at (HotkeySells); 0 for any.
    /// </summary>
    public static (IbkrOrderTicket Ticket, double Quote, string Problem) Decide(OrderHotkey hotkey, IReadOnlyList<HotkeyQuote> depth, DateTime? utc = null, Func<string, string> restriction = null, double lowest = 0)
    {
        if (depth.Count == 0)
            return (null, double.NaN, "no market depth is open, so there's no stock to send it for");
        if (depth.Count > 1)
            return (null, double.NaN, $"{depth.Count} market depths are open ({string.Join(", ", depth.Select(d => d.Symbol))}), so it's not clear which stock");

        var target = depth[0];
        double quote = hotkey.Buy ? target.Ask : target.Bid;
        double shares = hotkey.Shares;
        if (hotkey.OfPosition > 0)
        {
            double held = hotkey.Buy ? -target.Position : target.Position;
            double working = hotkey.Buy ? target.WorkingBuys : target.WorkingSells;
            if (!(held > 0))
                return (null, quote, $"there's no {(hotkey.Buy ? "short" : "long")} position in {target.Symbol}");

            double free = held - working;
            if (!(free > 0))
                return (null, quote, $"all {held:#,0} shares of {target.Symbol} are already in working {(hotkey.Buy ? "buy" : "sell")} orders");

            shares = Math.Min(Math.Floor(held * Math.Min(hotkey.OfPosition, 1)), free);
            if (!(shares >= 1))
                return (null, quote, $"{hotkey.Size()} of {held:#,0} shares of {target.Symbol} is under one share");
        }

        // IBKR refuses an opening trade in a restricted stock outright (ZBAO, 21 times on 2026-11-23), so none is sent.
        // An order that closes a position still goes: IBKR allows that.
        bool opens = hotkey.Buy ? target.Position >= 0 : target.Position <= 0;
        if (opens && restriction?.Invoke(target.Symbol) is { } reason)
            return (null, quote, $"IBKR won't let you open a position in {target.Symbol} ({reason})");

        if (target.Halted)
            return (null, quote, $"{target.Symbol} is halted");
        if (!(quote > 0))
            return (null, quote, $"{target.Symbol} has no IBKR {(hotkey.Buy ? "ask" : "bid")}");

        double limit = LimitPrice(quote, hotkey.Buy ? hotkey.Offset : -hotkey.Offset);
        if (!hotkey.Buy)
            limit = Math.Max(Math.Max(limit, SellFloor(target)), lowest);
        if (!(limit > 0))
            return (null, quote, $"{target.Symbol}'s limit would be {limit}");

        string tif = TifAt(hotkey, utc ?? DateTime.UtcNow);
        return (new IbkrOrderTicket(target.Symbol, hotkey.Buy, shares, "LMT", limit, double.NaN, tif), quote, null);
    }

    /// <summary>
    /// The quote plus the offset, a share of it (minus for a sell) but never more than 15 cents (OrderHotkey.MaxOffset),
    /// on IBKR's tick for stocks: $0.01 from a dollar, $0.0001 below.
    /// </summary>
    public static double LimitPrice(double quote, double offset)
    {
        double through = Math.Min(Math.Abs(quote * offset), OrderHotkey.MaxOffset);
        double price = quote + (offset < 0 ? -through : through);
        return Math.Round(price, price >= 1.0 ? 2 : 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The lowest a sell key's limit goes, where its 2.5% would take it lower: Limit Down as the depth shows it (your
    /// request, 2026-11-25), since nothing can trade under it; 0 with none, as before 9:30 and after 16:00 ET. A bid
    /// already under one that was picked up mid-session shows that one's off, so then it's the bid.
    /// </summary>
    public static double SellFloor(HotkeyQuote stock) =>
        !(stock.LimitDown > 0) ? 0 : stock.LimitDownRough && stock.Bid > 0 ? Math.Min(stock.LimitDown, stock.Bid) : stock.LimitDown;

    /// <summary>"Alt+1", "Shift+Z", "Ctrl+Shift+F2" and the like: the modifiers that must be held, and the virtual key.</summary>
    public static bool TryParseKeys(string keys, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
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

        return virtualKey != 0 && modifiers != 0;
    }

    /// <summary>A key that types a character when pressed: one held with Shift alone, a letter or digit with no Ctrl or Alt.</summary>
    public static bool Types(uint modifiers, uint virtualKey) =>
        (modifiers & (HotkeyListener.Alt | HotkeyListener.Control)) == 0 && virtualKey is (>= '0' and <= '9') or (>= 'A' and <= 'Z');

    /// <summary>
    /// What one key-down does: which hotkey it fires (-1 none) and whether Quantower is kept from seeing it. Only with
    /// Quantower in front, the held modifiers exactly the key's, and not a key held down repeating; a key that types a
    /// character goes through as typing while a text box has the cursor.
    /// </summary>
    public static (int Fire, bool Swallow) OnKeyDown(IReadOnlyList<(uint Modifiers, uint Key)> keys, uint virtualKey, uint heldModifiers, bool repeat, bool quantowerInFront, bool typing)
    {
        if (!quantowerInFront)
            return (-1, false);

        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i].Key != virtualKey || keys[i].Modifiers != heldModifiers)
                continue;
            if (typing && Types(heldModifiers, virtualKey))
                return (-1, false);

            return (repeat ? -1 : i, true);
        }

        return (-1, false);
    }
}

/// <summary>
/// The order hotkeys' keyboard listener: a low-level keyboard hook on a thread of its own, one for the whole Quantower
/// process so Quantower's reconnects (a new vendor each time) don't fight over it. It acts on a key only while a
/// Quantower window is in front, and only then keeps the key from Quantower; every other program gets every key as
/// usual. While Quantower's window has a text cursor (a symbol box, say), a key that types a character types it.
/// </summary>
internal sealed class HotkeyListener
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;

    private const int KeyboardLowLevel = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint WmPress = 0x8001;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    /// <summary>The plugin's one listener.</summary>
    public static HotkeyListener Shared { get; } = new();

    private readonly object gate = new();
    private readonly HashSet<uint> down = new();
    private Thread thread;
    private uint threadId;
    private IReadOnlyList<OrderHotkey> keys = [];
    private List<(uint Modifiers, uint Key)> parsed = new();
    private Action<OrderHotkey> handler;
    private HookProc hookProc;
    private bool typingLogged;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    /// <summary>Whether a Quantower window is in front. The harness stands in for it.</summary>
    public Func<bool> InFront { get; set; } = IsThisProcessInFront;

    /// <summary>Whether the window in front has a text cursor. The harness stands in for it.</summary>
    public Func<bool> Typing { get; set; } = HasTextCursor;

    /// <summary>Whether the keyboard hook went in.</summary>
    public bool Hooked { get; private set; }

    /// <summary>Starts listening once, and sends each press to the handler, off the listener's thread.</summary>
    public void Start(IReadOnlyList<OrderHotkey> hotkeys, Action<OrderHotkey> onPress)
    {
        Volatile.Write(ref this.handler, onPress);
        lock (this.gate)
        {
            if (this.thread != null)
                return;

            this.keys = hotkeys;
            using var started = new ManualResetEventSlim();
            this.thread = new Thread(() => this.Run(started)) { IsBackground = true, Name = "ScalpFeed hotkeys" };
            this.thread.Start();
            started.Wait(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A reconnect's old vendor lets go, so a press until the new one takes over says it's not connected.</summary>
    public void Release(Action<OrderHotkey> onPress) => Interlocked.CompareExchange(ref this.handler, null, onPress);

    /// <summary>Takes the hook out and ends the thread. Only the harness needs it; the plugin keeps it while Quantower runs.</summary>
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

    /// <summary>The harness's stand-in for a key-down, decided as a real one would be, on the listener's thread.</summary>
    public bool Press(uint virtualKey, uint heldModifiers) =>
        PostThreadMessage(this.threadId, WmPress, (UIntPtr)virtualKey, (IntPtr)heldModifiers);

    private void Run(ManualResetEventSlim started)
    {
        this.threadId = GetCurrentThreadId();

        // Any message call gives the thread its queue before anyone posts to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        foreach (var key in this.keys)
        {
            if (OrderHotkeys.TryParseKeys(key.Keys, out uint modifiers, out uint virtualKey))
            {
                this.parsed.Add((modifiers, virtualKey));
                Log.Info($"[hotkey] {key.Keys}: {key.Describe()}, for the stock with the market depth open");
            }
            else
            {
                this.parsed.Add((0, 0));
                Log.Info($"[hotkey] {key.Keys} isn't a key this plugin understands");
            }
        }

        this.hookProc = this.OnHook;
        IntPtr hook = SetWindowsHookEx(KeyboardLowLevel, this.hookProc, GetModuleHandle(null), 0);
        this.Hooked = hook != IntPtr.Zero;
        if (!this.Hooked)
            Log.Info($"[hotkey] the keyboard hook couldn't go in (Windows error {Marshal.GetLastWin32Error()}), so no order hotkeys");

        started.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            // A stand-in press is a whole press, down and up.
            if (message.Message == WmPress)
            {
                this.OnKeyDown((uint)message.WParam, (uint)message.LParam);
                this.down.Remove((uint)message.WParam);
            }
        }

        if (hook != IntPtr.Zero)
            UnhookWindowsHookEx(hook);
    }

    // Windows waits on this for every key in every program, so it only decides and hands off.
    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            uint virtualKey = (uint)Marshal.ReadInt32(lParam);
            if (message is WmKeyUp or WmSysKeyUp)
            {
                this.down.Remove(virtualKey);
            }
            else if (message is WmKeyDown or WmSysKeyDown)
            {
                uint held = (Held(VkMenu) ? Alt : 0) | (Held(VkControl) ? Control : 0) | (Held(VkShift) ? Shift : 0);
                if (this.OnKeyDown(virtualKey, held))
                    return (IntPtr)1;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Returns whether the key is kept from Quantower.</summary>
    private bool OnKeyDown(uint virtualKey, uint held)
    {
        bool repeat = !this.down.Add(virtualKey);
        if (!this.parsed.Contains((held, virtualKey)))
            return false;

        bool inFront = this.InFront();
        if (!inFront && !repeat && !OrderHotkeys.Types(held, virtualKey))
        {
            // Which program was in front, said off the hook's thread. Keys that type aren't logged: that's typing elsewhere.
            uint process = ForegroundProcessId();
            _ = Task.Run(() => Log.Info($"[hotkey] {NameOf(held, virtualKey, this.keys, this.parsed)} pressed with {ProcessName(process)} in front, not Quantower (process {Environment.ProcessId}), so nothing was sent"));
        }

        bool typing = inFront && OrderHotkeys.Types(held, virtualKey) && this.Typing();
        var (fire, swallow) = OrderHotkeys.OnKeyDown(this.parsed, virtualKey, held, repeat, inFront, typing);
        if (typing && !swallow && !repeat && !this.typingLogged)
        {
            this.typingLogged = true;
            Log.Info("[hotkey] a key that types a character was pressed with a text cursor in Quantower, so it typed instead of trading (said once)");
        }

        if (fire >= 0)
            this.Fire(this.keys[fire]);
        return swallow;
    }

    private void Fire(OrderHotkey key)
    {
        var onPress = Volatile.Read(ref this.handler);
        if (onPress == null)
        {
            Log.Info($"[hotkey] {key.Keys} pressed while the connection is down, so nothing was sent");
            FeedAlarm.PlayWarning(); // muted (FeedAlarm.Muted)
            return;
        }

        _ = Task.Run(() => onPress(key));
    }

    private static bool Held(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string NameOf(uint held, uint virtualKey, IReadOnlyList<OrderHotkey> keys, List<(uint Modifiers, uint Key)> parsed)
    {
        int at = parsed.IndexOf((held, virtualKey));
        return at >= 0 ? keys[at].Keys : $"key {virtualKey:X}";
    }

    private static uint ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint process) != 0 ? process : 0;
    }

    private static string ProcessName(uint process)
    {
        if (process == 0)
            return "no window";

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)process);
            return $"{p.ProcessName} (process {process})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"process {process}";
        }
    }

    private static bool IsThisProcessInFront()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint process) != 0 && process == (uint)Environment.ProcessId;
    }

    private static bool HasTextCursor()
    {
        var window = GetForegroundWindow();
        uint uiThread = window == IntPtr.Zero ? 0 : GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return uiThread != 0 && GetGUIThreadInfo(uiThread, ref info) && info.Caret != IntPtr.Zero;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

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
