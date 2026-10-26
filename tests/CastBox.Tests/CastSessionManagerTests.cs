using System.Net;
using System.Net.Sockets;
using CastBox.Chromecast;
using CastBox.ScreenShare;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CastBox.Tests;

/// <summary>Stands in for FFmpeg's HLS output: writes a playlist with three pieces, then runs until cancelled.</summary>
internal sealed class FakeHls : IHlsStreamer
{
    public bool WritePlaylist = true;
    public Exception? FailWith;
    public int Starts;
    public bool Cancelled;
    public async Task RunHlsAsync(int screen, string directory, CancellationToken ct)
    {
        Interlocked.Increment(ref Starts);
        if (FailWith is not null) throw FailWith;
        Directory.CreateDirectory(directory);
        if (WritePlaylist)
        {
            for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(directory, $"seg{i:00000}.ts"), "ts");
            File.WriteAllText(Path.Combine(directory, "stream.m3u8"), "#EXTM3U\n#EXTINF:1.0,\nseg00000.ts\n#EXTINF:1.0,\nseg00001.ts\n#EXTINF:1.0,\nseg00002.ts\n");
        }
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { Cancelled = true; }
    }
}

internal sealed class FakeCast : ICastConnection
{
    public bool Launched, Disposed, FailLaunch, FailPlay;
    public string? PlayedUrl;
    public event Action? Closed;
    public void HangUp() => Closed?.Invoke();
    public Task LaunchPlayerAsync(CancellationToken ct) { if (FailLaunch) throw new CastException("no player"); Launched = true; return Task.CompletedTask; }
    public Task PlayLiveAsync(string url, string title, CancellationToken ct, string streamType = "LIVE")
    {
        if (FailPlay) throw new CastException("cannot play");
        PlayedUrl = url; return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public sealed class CastSessionManagerTests : IDisposable
{
    private static readonly IReadOnlyList<ScreenInfo> Screens = new[] { new ScreenInfo(0, "Screen 1 (main)", "d1", 2560, 1080, true), new ScreenInfo(1, "Screen 2", "d2", 1920, 1080, false) };
    private static readonly CastDevice Device = new("dev1", "Chromecast", "Chromecast", "192.168.1.11", 8009);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "castbox-hls-" + Guid.NewGuid().ToString("N"));
    private readonly ScreenShareService _service = new(() => Screens);
    private readonly FakeHls _hls = new();
    private readonly FakeCast _cast = new();
    private readonly List<string> _log = new();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private CastSessionManager Manager(Func<string, int, CancellationToken, Task<ICastConnection>>? connect = null, TimeSpan? ready = null) =>
        new(_service, _hls, () => "192.168.1.13", () => 5078, connect ?? ((_, _, _) => Task.FromResult<ICastConnection>(_cast)), _root, _log.Add, ready ?? TimeSpan.FromSeconds(5));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task Casting_starts_the_screen_then_the_Chromecast_player_and_lists_the_session()
    {
        var manager = Manager();
        var session = await manager.StartAsync(Device, 1, CancellationToken.None);

        Assert.True(_cast.Launched);
        Assert.Equal($"http://192.168.1.13:5078/api/screens/hls/{session.Id}/stream.m3u8", _cast.PlayedUrl);
        var listed = Assert.Single(_service.Sessions);
        Assert.Equal("Chromecast", listed.TvName);
        Assert.Equal(1, listed.Screen);
        Assert.NotNull(manager.FileFor(session.Id, "stream.m3u8"));
        Assert.NotNull(manager.FileFor(session.Id, "seg00002.ts"));
    }

    [Theory]
    [InlineData("../stream.m3u8")]
    [InlineData("..%2Fx.ts")]
    [InlineData("seg1.ts")]
    [InlineData("stream.m3u8.bak")]
    [InlineData("notes.txt")]
    [InlineData("")]
    public async Task Only_the_playlist_and_its_pieces_are_ever_served(string file)
    {
        var manager = Manager();
        var session = await manager.StartAsync(Device, 0, CancellationToken.None);
        Assert.Null(manager.FileFor(session.Id, file));
    }

    [Fact]
    public async Task An_unknown_session_gets_nothing_and_a_missing_piece_gets_nothing()
    {
        var manager = Manager();
        var session = await manager.StartAsync(Device, 0, CancellationToken.None);
        Assert.Null(manager.FileFor("someone-else", "stream.m3u8"));
        Assert.Null(manager.FileFor(session.Id, "seg99999.ts"));
    }

    [Fact]
    public async Task Stopping_from_the_PC_stops_the_capture_closes_the_player_and_cleans_up()
    {
        var manager = Manager();
        var session = await manager.StartAsync(Device, 0, CancellationToken.None);
        var directory = Path.Combine(_root, session.Id);
        Assert.True(Directory.Exists(directory));

        _service.Stop(session.Id);
        await WaitUntil(() => _cast.Disposed && _hls.Cancelled && _service.Sessions.Count == 0 && !Directory.Exists(directory));
        Assert.Null(manager.FileFor(session.Id, "stream.m3u8"));
    }

    [Fact]
    public async Task When_the_Chromecast_hangs_up_everything_is_torn_down()
    {
        var manager = Manager();
        var session = await manager.StartAsync(Device, 0, CancellationToken.None);
        _cast.HangUp();
        await WaitUntil(() => _hls.Cancelled && _service.Sessions.Count == 0);
        Assert.Null(manager.FileFor(session.Id, "stream.m3u8"));
    }

    [Fact]
    public async Task Casting_to_the_same_Chromecast_again_replaces_the_first_cast()
    {
        var casts = new List<FakeCast>();
        var manager = Manager((_, _, _) => { var c = new FakeCast(); casts.Add(c); return Task.FromResult<ICastConnection>(c); });
        var first = await manager.StartAsync(Device, 0, CancellationToken.None);
        var second = await manager.StartAsync(Device, 1, CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.True(casts[0].Disposed);
        Assert.False(casts[1].Disposed);
        Assert.Equal(second.Id, Assert.Single(_service.Sessions).Id);
        Assert.Null(manager.FileFor(first.Id, "stream.m3u8"));
    }

    [Fact]
    public async Task Stopping_everything_ends_every_cast()
    {
        var manager = Manager();
        await manager.StartAsync(Device, 0, CancellationToken.None);
        await manager.StopAllAsync();
        Assert.True(_cast.Disposed);
        Assert.Empty(_service.Sessions);
    }

    [Fact]
    public async Task A_screen_that_cannot_be_captured_is_reported_and_leaves_nothing_behind()
    {
        _hls.FailWith = new InvalidOperationException("ffmpeg crashed");
        var manager = Manager();
        var error = await Assert.ThrowsAsync<CastException>(() => manager.StartAsync(Device, 0, CancellationToken.None));
        Assert.Contains("captured", error.Message);
        Assert.Empty(_service.Sessions);
        Assert.False(_cast.Launched);
    }

    [Fact]
    public async Task A_screen_that_never_produces_pictures_times_out()
    {
        _hls.WritePlaylist = false;
        var manager = Manager(ready: TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAsync<CastException>(() => manager.StartAsync(Device, 0, CancellationToken.None));
        await WaitUntil(() => _hls.Cancelled);
        Assert.Empty(_service.Sessions);
    }

    [Fact]
    public async Task A_Chromecast_that_cannot_be_reached_or_cannot_play_ends_the_attempt_cleanly()
    {
        var unreachable = Manager((_, _, _) => throw new CastException("Couldn't reach the Chromecast."));
        await Assert.ThrowsAsync<CastException>(() => unreachable.StartAsync(Device, 0, CancellationToken.None));
        await WaitUntil(() => _hls.Cancelled && _service.Sessions.Count == 0);

        _cast.FailPlay = true;
        var failing = Manager();
        await Assert.ThrowsAsync<CastException>(() => failing.StartAsync(Device, 0, CancellationToken.None));
        await WaitUntil(() => _cast.Disposed && _service.Sessions.Count == 0);
    }

    [Fact]
    public async Task An_unknown_screen_is_refused_before_anything_starts()
    {
        var manager = Manager();
        await Assert.ThrowsAsync<CastException>(() => manager.StartAsync(Device, 9, CancellationToken.None));
        Assert.Equal(0, _hls.Starts);
    }
}

public sealed class HlsEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castbox-hlsep-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _base = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "stream.m3u8"), "#EXTM3U\n");
        File.WriteAllText(Path.Combine(_dir, "seg00000.ts"), "ts");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        var service = new ScreenShareService(() => new[] { new ScreenInfo(0, "Screen", "d", 1920, 1080, true) });
        ScreenEndpoints.Map(_app, service, new FakeStreamer(), () => "127.0.0.1", port, _ => { },
            hlsFile: (session, file) => session == "good" && File.Exists(Path.Combine(_dir, file)) ? Path.Combine(_dir, file) : null);
        await _app.StartAsync();
        _base = $"http://127.0.0.1:{port}";
        _http = new HttpClient();
    }

    public async Task DisposeAsync() { _http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task The_playlist_and_pieces_are_served_with_the_right_types_and_are_not_cached()
    {
        var playlist = await _http.GetAsync(_base + "/api/screens/hls/good/stream.m3u8");
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", playlist.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", playlist.Headers.CacheControl!.ToString());
        Assert.Equal("#EXTM3U\n", await playlist.Content.ReadAsStringAsync());

        var piece = await _http.GetAsync(_base + "/api/screens/hls/good/seg00000.ts");
        Assert.Equal("video/mp2t", piece.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Unknown_sessions_and_files_are_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_base + "/api/screens/hls/bad/stream.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_base + "/api/screens/hls/good/seg00001.ts")).StatusCode);
    }
}
