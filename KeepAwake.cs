using System.Runtime.InteropServices;

using Microsoft.AspNetCore.Http;

namespace CastBox;

/// <summary>
/// Stops Windows from sleeping while something is being streamed. Video requests count while they are open and for a few
/// minutes after (a paused video holds no request but will be resumed), and so does any shared screen. The display may still
/// turn off for video, but not while a screen is being shared (a sleeping display captures as black).
/// </summary>
public sealed class KeepAwake
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _now;
    private int _inFlight;
    private long _lastEndTicks;

    public KeepAwake(Func<DateTime>? now = null) { _now = now ?? (() => DateTime.UtcNow); _lastEndTicks = DateTime.MinValue.Ticks; }

    /// <summary>True for the requests that carry video or a picture to the TV, not for the TV's small status checks.</summary>
    public static bool IsStreamingPath(PathString path)
    {
        var p = path.Value ?? "";
        if (p.StartsWith("/api/media/", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith("/api/screens/hls/", StringComparison.OrdinalIgnoreCase) || p.Equals("/api/screens/stream", StringComparison.OrdinalIgnoreCase)) return true;
        // /api/personal/{id} is the file itself; /thumb, /browse and /roots are not.
        const string personal = "/api/personal/";
        if (!p.StartsWith(personal, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = p[personal.Length..];
        return rest.Length > 0 && !rest.Contains('/') && rest is not ("roots" or "browse");
    }

    public void Begin() => Interlocked.Increment(ref _inFlight);

    public void End()
    {
        Interlocked.Exchange(ref _lastEndTicks, _now().Ticks);
        Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>What Windows should be told right now.</summary>
    public (bool System, bool Display) Wanted(bool screenSharing)
    {
        var recent = _now().Ticks - Interlocked.Read(ref _lastEndTicks) < Grace.Ticks;
        return (screenSharing || Volatile.Read(ref _inFlight) > 0 || recent, screenSharing);
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>
    /// Tells Windows. The setting belongs to the calling thread, so always call this from the same thread (the window's).
    /// </summary>
    public static void Apply((bool System, bool Display) wanted)
    {
        var flags = EsContinuous;
        if (wanted.System) flags |= EsSystemRequired;
        if (wanted.Display) flags |= EsDisplayRequired;
        SetThreadExecutionState(flags);
    }

    /// <summary>Lets Windows sleep again.</summary>
    public static void Release() => SetThreadExecutionState(EsContinuous);
}
