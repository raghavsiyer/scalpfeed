using System.Globalization;
using System.Runtime.InteropServices;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// An order a key sends: buy or sell, limit at the ask (buy) or bid (sell) plus or minus the offset, with this time in
/// force (DAY, GTC or IOC). The size is the shares given, or with OfPosition that share of the position it closes
/// (1 the whole position, 0.5 half), whole shares rounded down: a sell the long position, a buy a short one.
/// </summary>
internal sealed record OrderHotkey(string Keys, bool Buy, double Shares, double Offset, string Tif, double OfPosition = 0)
{
    /// <summary>Alt+1: buy 1,000 shares, limit at the ask plus 10 cents, immediate or cancel (your requests, 2026-11-18).</summary>
    public static OrderHotkey BuyAskPlusTen { get; } = new("Alt+1", true, 1000, 0.10, "IOC");

    /// <summary>Shift+Z: sell the whole position, limit at the bid minus 10 cents, immediate or cancel (your request, 2026-11-18).</summary>
    public static OrderHotkey SellAllBidMinusTen { get; } = new("Shift+Z", false, 0, 0.10, "IOC", OfPosition: 1);

    /// <summary>Shift+X: sell half the position, limit at the bid minus 10 cents, immediate or cancel (your request, 2026-11-18).</summary>
    public static OrderHotkey SellHalfBidMinusTen { get; } = new("Shift+X", false, 0, 0.10, "IOC", OfPosition: 0.5);

    /// <summary>"the whole position", "half the position", or the shares.</summary>
    public string Size() => this.OfPosition switch
    {
        <= 0 => $"{this.Shares:#,0} shares",
        1 => "the whole position",
        0.5 => "half the position",
        _ => $"{this.OfPosition:P0} of the position",
    };

    public string Describe() =>
        $"{(this.Buy ? "buy" : "sell")} {this.Size()}, {this.Tif} limit at the {(this.Buy ? "ask plus" : "bid minus")} {this.Offset}";
}

/// <summary>
/// A depth symbol as a hotkey order sees it: IBKR's symbol, its NBBO bid and ask, whether it's halted, the paper
/// account's position in it (negative when short), and the shares still to fill in its working buy and sell orders.
/// </summary>
internal readonly record struct HotkeyQuote(string Symbol, double Bid, double Ask, bool Halted, double Position = 0, double WorkingBuys = 0, double WorkingSells = 0);

/// <summary>
/// Order hotkeys (your requests, 2026-11-18). Quantower Free has no order placing strategies, and its own hotkeys place
/// at the plain ask or send a fixed preset order, so the plugin catches the key itself and sends the order through
/// IbkrTrader, the paper account's own connection, like an order from Quantower's order entry.
///
/// The stock is the one with a market depth open, since your DOM follows the stock you've picked; with none or
/// several it sends nothing and says why. The price is IBKR's NBBO, at the moment of the key, rounded to IBKR's
/// tick for the price: a cent from a dollar, a hundredth of a cent below. The order is a limit with the key's time in
/// force (all three keys' is IOC: what doesn't fill at once is cancelled), which IbkrTrader sends with outsideRth for
/// premarket and after hours. A position key takes its share of the position as IBKR last said it, never more than
/// the shares not already in working orders on its side, so presses while earlier ones are working can't go past it.
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

        if (target.Halted)
            return (null, quote, $"{target.Symbol} is halted");
        if (!(quote > 0))
            return (null, quote, $"{target.Symbol} has no IBKR {(hotkey.Buy ? "ask" : "bid")}");

        double limit = LimitPrice(quote, hotkey.Buy ? hotkey.Offset : -hotkey.Offset);
        if (!(limit > 0))
            return (null, quote, $"{target.Symbol}'s limit would be {limit}");

        return (new IbkrOrderTicket(target.Symbol, hotkey.Buy, shares, "LMT", limit, double.NaN, hotkey.Tif), quote, null);
    }

    /// <summary>The quote plus the offset, on IBKR's tick for stocks: $0.01 from a dollar, $0.0001 below.</summary>
    public static double LimitPrice(double quote, double offset)
    {
        double price = quote + offset;
        return Math.Round(price, price >= 1.0 ? 2 : 4, MidpointRounding.AwayFromZero);
    }

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
            FeedAlarm.PlayWarning();
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
