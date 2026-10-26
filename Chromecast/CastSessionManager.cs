using System.Text.RegularExpressions;
using CastBox.ScreenShare;

namespace CastBox.Chromecast;

/// <summary>
/// Sends a screen to a Chromecast through Google Cast: FFmpeg writes a live HLS playlist, this PC serves it, and the
/// Chromecast's built-in player is told to play it. Works even when no CastBox app is open on the Chromecast.
/// </summary>
public sealed partial class CastSessionManager
{
    private sealed class Active
    {
        public required ShareSession Session { get; init; }
        public required string DeviceId { get; init; }
        public required string Directory { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public ICastConnection? Cast { get; set; }
        public int Ended;
    }

    private readonly ScreenShareService _service;
    private readonly IHlsStreamer _streamer;
    private readonly Func<string> _lanIp;
    private readonly Func<int> _port;
    private readonly Func<string, int, CancellationToken, Task<ICastConnection>> _connect;
    private readonly string _root;
    private readonly Action<string> _log;
    private readonly TimeSpan _readyTimeout;
    private readonly object _lock = new();
    private readonly Dictionary<string, Active> _active = new();   // by session id

    public CastSessionManager(ScreenShareService service, IHlsStreamer streamer, Func<string> lanIp, Func<int> port,
        Func<string, int, CancellationToken, Task<ICastConnection>> connect, string hlsRoot, Action<string> log, TimeSpan? readyTimeout = null)
    {
        _service = service; _streamer = streamer; _lanIp = lanIp; _port = port; _connect = connect; _root = hlsRoot; _log = log;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(20);
        try { if (Directory.Exists(hlsRoot)) Directory.Delete(hlsRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^(stream\.m3u8|seg\d{5}\.ts)$")]
    private static partial Regex FileName();

    /// <summary>The file to serve for <paramref name="file"/> of an active session, or null (unknown session, odd name, not written yet).</summary>
    public string? FileFor(string sessionId, string file)
    {
        if (!FileName().IsMatch(file)) return null;
        Active? a; lock (_lock) _active.TryGetValue(sessionId, out a);
        if (a is null) return null;
        var path = Path.Combine(a.Directory, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Starts casting <paramref name="screen"/> to <paramref name="device"/>, replacing anything this PC is already casting to it.</summary>
    public async Task<ShareSession> StartAsync(CastDevice device, int screen, CancellationToken ct)
    {
        if (_service.GetScreen(screen) is null) throw new CastException("That screen no longer exists.");
        List<Active> previous; lock (_lock) previous = _active.Values.Where(a => a.DeviceId == device.Id).ToList();
        foreach (var old in previous) await EndAsync(old).ConfigureAwait(false);

        var session = _service.BeginExternal(device.Name, screen);
        var active = new Active
        {
            Session = session, DeviceId = device.Id, Directory = Path.Combine(_root, session.Id),
            Cts = CancellationTokenSource.CreateLinkedTokenSource(session.Ended),
        };
        lock (_lock) _active[session.Id] = active;
        try
        {
            var run = Task.Run(() => _streamer.RunHlsAsync(screen, active.Directory, active.Cts.Token));
            _ = run.ContinueWith(t => { if (t.IsFaulted) _log("Capture failed: " + t.Exception!.GetBaseException().Message); _ = EndAsync(active); }, TaskScheduler.Default);
            await WaitForPlaylistAsync(active.Directory, run, ct).ConfigureAwait(false);

            var cast = await _connect(device.Address, device.Port, ct).ConfigureAwait(false);
            active.Cast = cast;
            cast.Closed += () => _ = EndAsync(active);
            await cast.LaunchPlayerAsync(ct).ConfigureAwait(false);
            var url = $"http://{_lanIp()}:{_port()}/api/screens/hls/{session.Id}/{StreamCommand.HlsPlaylist}";
            await cast.PlayLiveAsync(url, $"{_service.GetScreen(screen)!.Name} of this PC", ct).ConfigureAwait(false);
            session.Ended.Register(() => _ = EndAsync(active));
            _log($"Casting {_service.GetScreen(screen)!.Name} to {device.Name}.");
            return session;
        }
        catch
        {
            await EndAsync(active).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Waits until FFmpeg has written a playlist with a couple of pieces, so the player has something to start on.</summary>
    private async Task WaitForPlaylistAsync(string directory, Task run, CancellationToken ct)
    {
        var playlist = Path.Combine(directory, StreamCommand.HlsPlaylist);
        var deadline = DateTime.UtcNow + _readyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (run.IsCompleted) throw new CastException("The screen could not be captured. Is FFmpeg working?");
            try { if (File.Exists(playlist) && File.ReadAllText(playlist).Split("#EXTINF").Length - 1 >= 2) return; } catch (IOException) { /* being written */ }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new CastException("The screen did not start in time. Is FFmpeg working?");
    }

    private async Task EndAsync(Active a)
    {
        if (Interlocked.Exchange(ref a.Ended, 1) == 1) return;
        lock (_lock) _active.Remove(a.Session.Id);
        try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
        if (a.Cast is not null) { try { await a.Cast.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
        try { if (Directory.Exists(a.Directory)) Directory.Delete(a.Directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _service.Close(a.Session);
        _log($"Stopped casting to {a.Session.TvName}.");
    }

    /// <summary>Stops every Chromecast this PC is casting to.</summary>
    public async Task StopAllAsync()
    {
        List<Active> all; lock (_lock) all = _active.Values.ToList();
        foreach (var a in all) await EndAsync(a).ConfigureAwait(false);
    }
}
