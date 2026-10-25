using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CastBox.ScreenShare;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CastBox.Tests;

/// <summary>Stands in for FFmpeg: sends a few bytes, then keeps the stream open like a live screen until the viewer leaves.</summary>
internal sealed class FakeStreamer : IScreenStreamer
{
    public int Started;
    public async Task StreamAsync(int screen, bool withSound, Stream output, CancellationToken ct)
    {
        Interlocked.Increment(ref Started);
        await output.WriteAsync(new byte[] { 0x47, 0x40, 0x00, 0x10 }, ct);
        await output.FlushAsync(ct);
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
    }
}

public sealed class ScreenEndpointsTests : IAsyncLifetime
{
    private static readonly IReadOnlyList<ScreenInfo> Screens = new[]
    {
        new ScreenInfo(0, "Screen 1 (main)", "d1", 2560, 1080, true),
        new ScreenInfo(1, "Screen 2", "d2", 1920, 1080, false),
    };

    private readonly FakeStreamer _streamer = new();
    private ScreenShareService _service = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _base = "";

    public async Task InitializeAsync() => await StartAsync(_streamer, maxStreams: 3);

    private async Task StartAsync(IScreenStreamer? streamer, int maxStreams)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        _service = new ScreenShareService(() => Screens, new Dictionary<string, string> { ["allowed-tv"] = "Living room" });
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        ScreenEndpoints.Map(_app, _service, streamer, () => "127.0.0.1", port, _ => { }, maxStreams: maxStreams);
        await _app.StartAsync();
        _base = $"http://127.0.0.1:{port}";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task DisposeAsync() { _http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); }

    private async Task<JsonObject> RequestAsync(string tvId, int screen)
    {
        var response = await _http.PostAsJsonAsync(_base + "/api/screens/request", new { tvId, tvName = tvId, screen });
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(string url) => await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

    [Fact]
    public async Task The_screens_are_listed_with_their_sizes_and_the_TV_is_noted()
    {
        var o = JsonNode.Parse(await _http.GetStringAsync(_base + "/api/screens?tvId=tv9&tvName=Bedroom"))!.AsObject();
        Assert.True((bool)o["available"]!);
        Assert.Equal(new[] { "Screen 1 (main)", "Screen 2" }, o["screens"]!.AsArray().Select(s => (string)s!["name"]!));
        Assert.Equal(1920, (int)o["screens"]![1]!["width"]!);
        Assert.Equal("Bedroom", Assert.Single(_service.RecentTvs()).Name);
    }

    [Fact]
    public async Task A_TV_that_is_allowed_for_good_gets_a_stream_at_once()
    {
        var answer = await RequestAsync("allowed-tv", 1);
        Assert.Equal("approved", (string)answer["status"]!);
        var url = (string)answer["streamUrl"]!;
        Assert.Contains("screen=1&token=", url);

        using var stream = await OpenStreamAsync(url);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("video/mp2t", stream.Content.Headers.ContentType!.MediaType);
        var first = new byte[4];
        using var body = await stream.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(first);
        Assert.Equal(0x47, first[0]);
        Assert.Single(_service.Sessions);
    }

    [Fact]
    public async Task Leaving_ends_the_session_and_the_PC_can_stop_a_stream()
    {
        var url = (string)(await RequestAsync("allowed-tv", 0))["streamUrl"]!;
        using (var stream = await OpenStreamAsync(url)) { using var body = await stream.Content.ReadAsStreamAsync(); await body.ReadExactlyAsync(new byte[4]); }
        await WaitUntil(() => _service.Sessions.Count == 0);

        using var second = await OpenStreamAsync(url);
        using var secondBody = await second.Content.ReadAsStreamAsync();
        await secondBody.ReadExactlyAsync(new byte[4]);
        _service.StopAll();
        var rest = new byte[16];
        Assert.Equal(0, await secondBody.ReadAsync(rest)); // the stream ends from the PC's side
        await WaitUntil(() => _service.Sessions.Count == 0);
    }

    [Fact]
    public async Task A_stranger_waits_for_the_person_and_cannot_stream_until_allowed()
    {
        var asked = await RequestAsync("stranger", 0);
        Assert.Equal("pending", (string)asked["status"]!);
        Assert.Null(asked["streamUrl"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(_base + "/api/screens/stream?screen=0&token=guess")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync(_base + "/api/screens/stream?screen=0")).StatusCode);

        var id = (string)asked["requestId"]!;
        _service.Resolve(id, ConsentChoice.AllowOnce);
        var polled = JsonNode.Parse(await _http.GetStringAsync(_base + "/api/screens/request/" + id))!.AsObject();
        Assert.Equal("approved", (string)polled["status"]!);
        using var stream = await OpenStreamAsync((string)polled["streamUrl"]!);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
    }

    [Fact]
    public async Task A_refusal_is_reported_and_a_token_does_not_work_for_another_screen()
    {
        var denied = await RequestAsync("stranger", 0);
        _service.Resolve((string)denied["requestId"]!, ConsentChoice.Deny);
        var polled = JsonNode.Parse(await _http.GetStringAsync(_base + "/api/screens/request/" + (string)denied["requestId"]!))!.AsObject();
        Assert.Equal("denied", (string)polled["status"]!);
        Assert.Null(polled["streamUrl"]);

        var token = ((string)(await RequestAsync("allowed-tv", 0))["streamUrl"]!).Split("token=")[1];
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"{_base}/api/screens/stream?screen=1&token={token}")).StatusCode);
    }

    [Fact]
    public async Task Bad_requests_are_refused_with_clear_errors()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync(_base + "/api/screens/request", new { tvId = "x", tvName = "x", screen = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync(_base + "/api/screens/request", new { tvName = "x", screen = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsync(_base + "/api/screens/request", new StringContent("not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(_base + "/api/screens/request/unknown")).StatusCode);
    }

    [Fact]
    public async Task A_screen_the_PC_sends_reaches_a_TV_that_is_waiting_for_one()
    {
        // The TV asks and waits; nothing is on offer yet.
        var waiting = _http.GetAsync(_base + "/api/screens/inbox?tvId=tv-open&tvName=Kitchen&wait=10");
        await WaitUntil(() => _service.RecentTvs().Any(t => t.Id == "tv-open"));
        Assert.Equal("Kitchen", _service.RecentTvs().Single(t => t.Id == "tv-open").Name);

        Assert.True(_service.Push("tv-open", 1)); // the "Share to TV" button
        var response = await waiting;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var offer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(1, (int)offer["screen"]!);
        Assert.Equal("Screen 2", (string)offer["name"]!);

        using var stream = await OpenStreamAsync((string)offer["streamUrl"]!);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
    }

    [Fact]
    public async Task With_nothing_to_send_the_inbox_answers_no_content_after_waiting()
    {
        var response = await _http.GetAsync(_base + "/api/screens/inbox?tvId=tv-open&tvName=Kitchen&wait=1");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_streams_at_once_are_refused()
    {
        await DisposeAsync();
        await StartAsync(_streamer, maxStreams: 1);
        var url1 = (string)(await RequestAsync("allowed-tv", 0))["streamUrl"]!;
        var url2 = (string)(await RequestAsync("allowed-tv", 1))["streamUrl"]!;
        using var first = await OpenStreamAsync(url1);
        using var firstBody = await first.Content.ReadAsStreamAsync();
        await firstBody.ReadExactlyAsync(new byte[4]);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _http.GetAsync(url2)).StatusCode);
    }

    [Fact]
    public async Task Without_FFmpeg_screens_are_listed_as_unavailable_and_nothing_can_be_requested()
    {
        await DisposeAsync();
        await StartAsync(null, maxStreams: 3);
        var o = JsonNode.Parse(await _http.GetStringAsync(_base + "/api/screens"))!.AsObject();
        Assert.False((bool)o["available"]!);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _http.PostAsJsonAsync(_base + "/api/screens/request", new { tvId = "allowed-tv", tvName = "x", screen = 0 })).StatusCode);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "condition not reached in time");
    }
}
