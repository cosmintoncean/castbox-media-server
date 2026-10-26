using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CastBox.Chromecast;
using Xunit;

namespace CastBox.Tests;

public sealed class CastMessageCodecTests
{
    [Fact]
    public void A_message_survives_encoding_and_decoding_including_non_ASCII_text()
    {
        var message = new CastMessage("sender-castbox", "receiver-0", "urn:x-cast:com.google.cast.receiver", "{\"title\":\"Ecran și sunet\"}");
        var bytes = CastMessageCodec.Encode(message);
        Assert.Equal(bytes.Length - 4, (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]); // the length prefix
        Assert.Equal(message, CastMessageCodec.Decode(bytes.AsSpan(4)));
    }

    [Fact]
    public void Fields_it_does_not_know_are_skipped_and_damaged_records_are_refused()
    {
        var known = CastMessageCodec.Encode(new CastMessage("a", "b", "c", "{}")).AsSpan(4).ToArray();
        var withExtra = known.Concat(new byte[] { 0x3A, 0x02, 0x01, 0x02, 0x40, 0x07 }).ToArray(); // payload_binary and an unknown varint field
        Assert.Equal(new CastMessage("a", "b", "c", "{}"), CastMessageCodec.Decode(withExtra));
        Assert.Throws<FormatException>(() => CastMessageCodec.Decode(new byte[] { 0x12, 0x20, 0x41 })); // claims 32 bytes, has 1
        Assert.Throws<FormatException>(() => CastMessageCodec.Decode(new byte[] { 0x12 }));
    }
}

/// <summary>Plays the part of a Chromecast over a real local TCP connection (without the encryption).</summary>
internal sealed class FakeChromecast : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public readonly List<CastMessage> Received = new();
    public string MediaReply = """{"type":"MEDIA_STATUS","status":[{"playerState":"PLAYING"}]}""";
    private NetworkStream? _stream;
    private Task? _task;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeChromecast() { _listener.Start(); _task = Task.Run(Run); }

    public async Task<CastConnection> ConnectAsync()
    {
        var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, Port);
        return await CastConnection.OpenAsync(client.GetStream(), CancellationToken.None);
    }

    private async Task Reply(string source, string ns, string payload)
    {
        var bytes = CastMessageCodec.Encode(new CastMessage(source, "sender-castbox", ns, payload));
        await _stream!.WriteAsync(bytes); await _stream.FlushAsync();
    }

    private async Task Run()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            _stream = client.GetStream();
            var header = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token);
                var record = new byte[(header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3]];
                await _stream.ReadExactlyAsync(record, _cts.Token);
                var message = CastMessageCodec.Decode(record);
                lock (Received) Received.Add(message);
                var json = JsonNode.Parse(message.Payload)!.AsObject();
                switch ((string?)json["type"])
                {
                    case "LAUNCH":
                        await Reply("receiver-0", "urn:x-cast:com.google.cast.receiver",
                            """{"type":"RECEIVER_STATUS","status":{"applications":[{"appId":"CC1AD845","transportId":"web-7","sessionId":"session-7"}]}}""");
                        break;
                    case "LOAD": await Reply("web-7", "urn:x-cast:com.google.cast.media", MediaReply); break;
                }
            }
        }
        catch (Exception) { /* the test is over */ }
    }

    public void HangUp() => _stream?.Dispose();

    public async ValueTask DisposeAsync() { _cts.Cancel(); _listener.Stop(); try { _stream?.Dispose(); } catch { } if (_task is not null) { try { await _task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { } } }
}

public sealed class CastConnectionTests
{
    [Fact]
    public async Task It_starts_the_media_player_and_asks_it_to_play_a_live_stream()
    {
        await using var device = new FakeChromecast();
        await using var cast = await device.ConnectAsync();
        await cast.LaunchPlayerAsync(CancellationToken.None);
        await cast.PlayLiveAsync("http://192.168.1.13:5078/api/screens/hls/abc/stream.m3u8", "Screen 2 of PC", CancellationToken.None);

        List<CastMessage> got; lock (device.Received) got = device.Received.ToList();
        Assert.Equal("urn:x-cast:com.google.cast.tp.connection", got[0].Namespace);          // first it connects to the receiver
        var launch = JsonNode.Parse(got.First(m => m.Payload.Contains("LAUNCH")).Payload)!;
        Assert.Equal("CC1AD845", (string?)launch["appId"]);
        Assert.Contains(got, m => m.Destination == "web-7" && m.Payload.Contains("CONNECT"));  // then to the player itself
        var load = got.Single(m => m.Payload.Contains("\"LOAD\""));
        Assert.Equal("web-7", load.Destination);
        var media = JsonNode.Parse(load.Payload)!["media"]!;
        Assert.Equal("http://192.168.1.13:5078/api/screens/hls/abc/stream.m3u8", (string?)media["contentId"]);
        Assert.Equal("application/x-mpegURL", (string?)media["contentType"]);
        Assert.Equal("LIVE", (string?)media["streamType"]);
        Assert.Equal("session-7", (string?)JsonNode.Parse(load.Payload)!["sessionId"]);
    }

    [Fact]
    public async Task A_stream_the_Chromecast_cannot_play_is_reported()
    {
        await using var device = new FakeChromecast { MediaReply = """{"type":"LOAD_FAILED","requestId":3}""" };
        await using var cast = await device.ConnectAsync();
        await cast.LaunchPlayerAsync(CancellationToken.None);
        await Assert.ThrowsAsync<CastException>(() => cast.PlayLiveAsync("http://x/s.m3u8", "t", CancellationToken.None));

        await using var device2 = new FakeChromecast { MediaReply = """{"type":"MEDIA_STATUS","status":[{"playerState":"IDLE","idleReason":"ERROR"}]}""" };
        await using var cast2 = await device2.ConnectAsync();
        await cast2.LaunchPlayerAsync(CancellationToken.None);
        await Assert.ThrowsAsync<CastException>(() => cast2.PlayLiveAsync("http://x/s.m3u8", "t", CancellationToken.None));
    }

    [Fact]
    public async Task Stopping_closes_the_player_and_the_link_politely()
    {
        var device = new FakeChromecast();
        var cast = await device.ConnectAsync();
        await cast.LaunchPlayerAsync(CancellationToken.None);
        await cast.DisposeAsync();
        await Task.Delay(300);
        List<CastMessage> got; lock (device.Received) got = device.Received.ToList();
        var stop = got.Single(m => m.Payload.Contains("\"STOP\""));
        Assert.Contains("session-7", stop.Payload);
        Assert.Contains(got, m => m.Payload.Contains("CLOSE"));
        await device.DisposeAsync();
    }

    [Fact]
    public async Task When_the_Chromecast_hangs_up_the_session_reports_it_and_waiting_calls_fail()
    {
        await using var device = new FakeChromecast();
        await using var cast = await device.ConnectAsync();
        var closed = new TaskCompletionSource();
        cast.Closed += () => closed.TrySetResult();
        await cast.LaunchPlayerAsync(CancellationToken.None);
        device.HangUp();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Playing_before_the_player_is_started_is_refused()
    {
        await using var device = new FakeChromecast();
        await using var cast = await device.ConnectAsync();
        await Assert.ThrowsAsync<CastException>(() => cast.PlayLiveAsync("http://x/s.m3u8", "t", CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_address_is_a_clear_error()
    {
        var free = new TcpListener(IPAddress.Loopback, 0); free.Start(); var port = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
        await Assert.ThrowsAsync<CastException>(() => CastConnection.ConnectAsync("127.0.0.1", port, CancellationToken.None));
    }
}
