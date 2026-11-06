using System.Runtime.InteropServices;

namespace ScalpFeedVendor.Feed;

/// <summary>
/// Windows sleeps after a few minutes with no mouse or keyboard, even with Quantower open, and
/// every drop on 2026-11-05 came from that. Each call restarts Windows' idle-sleep timer. Nothing
/// has to be released: when the calls stop, the normal timer runs again. Settings aren't touched,
/// the screen can still turn off, and closing the lid or choosing Sleep still sleeps.
/// </summary>
internal static class StayAwake
{
    private const uint SystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Returns false if Windows refused.</summary>
    public static bool Renew() => SetThreadExecutionState(SystemRequired) != 0;
}
