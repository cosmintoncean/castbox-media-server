using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CastBox.Chromecast;

/// <summary>One Google Cast message (the "CastMessage" protobuf: a length-prefixed record with a few text fields).</summary>
public sealed record CastMessage(string Source, string Destination, string Namespace, string Payload);

/// <summary>Reads and writes Cast messages. The wire format is a 4-byte big-endian length followed by a protobuf record.</summary>
public static class CastMessageCodec
{
    public static byte[] Encode(CastMessage m)
    {
        var body = new List<byte>();
        body.Add(0x08); body.Add(0x00);                     // protocol_version = CASTV2_1_0
        AddString(body, 0x12, m.Source);                    // source_id
        AddString(body, 0x1A, m.Destination);               // destination_id
        AddString(body, 0x22, m.Namespace);                 // namespace
        body.Add(0x28); body.Add(0x00);                     // payload_type = STRING
        AddString(body, 0x32, m.Payload);                   // payload_utf8
        var framed = new byte[4 + body.Count];
        framed[0] = (byte)(body.Count >> 24); framed[1] = (byte)(body.Count >> 16); framed[2] = (byte)(body.Count >> 8); framed[3] = (byte)body.Count;
        body.CopyTo(framed, 4);
        return framed;
    }

    /// <summary>Decodes the protobuf record (without its length prefix). Fields it does not know are skipped.</summary>
    public static CastMessage Decode(ReadOnlySpan<byte> record)
    {
        string source = "", destination = "", ns = "", payload = "";
        var pos = 0;
        while (pos < record.Length)
        {
            var tag = ReadVarint(record, ref pos);
            var field = (int)(tag >> 3); var wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: ReadVarint(record, ref pos); break;
                case 2:
                    var length = (int)ReadVarint(record, ref pos);
                    if (length < 0 || pos + length > record.Length) throw new FormatException("truncated field");
                    var text = field is 2 or 3 or 4 or 6 ? Encoding.UTF8.GetString(record.Slice(pos, length)) : "";
                    pos += length;
                    if (field == 2) source = text; else if (field == 3) destination = text; else if (field == 4) ns = text; else if (field == 6) payload = text;
                    break;
                default: throw new FormatException("unsupported wire type " + wire);
            }
        }
        return new CastMessage(source, destination, ns, payload);
    }

    private static void AddString(List<byte> to, byte tag, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        to.Add(tag); AddVarint(to, (ulong)bytes.Length); to.AddRange(bytes);
    }

    private static void AddVarint(List<byte> to, ulong v)
    {
        while (v >= 0x80) { to.Add((byte)(v | 0x80)); v >>= 7; }
        to.Add((byte)v);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> s, ref int pos)
    {
        ulong result = 0; var shift = 0;
        while (true)
        {
            if (pos >= s.Length || shift > 63) throw new FormatException("bad varint");
            var b = s[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }
}

public sealed class CastException : Exception
{
    public CastException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A session with one Chromecast: connects, keeps the link alive, starts the Default Media Receiver (Google's built-in
/// player) and asks it to play a stream. Nothing has to be installed or open on the Chromecast.
/// </summary>
public interface ICastConnection : IAsyncDisposable
{
    event Action? Closed;
    Task LaunchPlayerAsync(CancellationToken ct);
    Task PlayLiveAsync(string url, string title, CancellationToken ct, string streamType = "LIVE");
}

public sealed class CastConnection : ICastConnection
{
    public const string DefaultMediaReceiver = "CC1AD845";
    private const string ConnectionNs = "urn:x-cast:com.google.cast.tp.connection", HeartbeatNs = "urn:x-cast:com.google.cast.tp.heartbeat";
    private const string ReceiverNs = "urn:x-cast:com.google.cast.receiver", MediaNs = "urn:x-cast:com.google.cast.media";
    private const string Sender = "sender-castbox";

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TaskCompletionSource<JsonObject>> _waiters = new();
    private readonly List<Func<JsonObject, bool>> _filters = new();
    private Task? _reader, _heartbeat;
    private int _requestId = 1;
    private string? _transportId, _sessionId;

    private CastConnection(Stream stream) => _stream = stream;

    /// <summary>Raised once when the Chromecast closes the session or the link is lost.</summary>
    public event Action? Closed;

    public static async Task<CastConnection> ConnectAsync(string address, int port, CancellationToken ct)
    {
        try
        {
            var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Parse(address), port, ct).ConfigureAwait(false);
            // Chromecasts present their own certificate, which no public authority signed, so it can't be checked the usual way.
            var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(address).ConfigureAwait(false);
            var connection = new CastConnection(tls);
            connection.Start();
            await connection.SendAsync("receiver-0", ConnectionNs, new JsonObject { ["type"] = "CONNECT" }, ct).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            throw new CastException("Couldn't reach the Chromecast.", ex);
        }
    }

    /// <summary>Opens a connection over an existing stream (used by tests that play the part of a Chromecast).</summary>
    public static async Task<CastConnection> OpenAsync(Stream stream, CancellationToken ct)
    {
        var connection = new CastConnection(stream);
        connection.Start();
        await connection.SendAsync("receiver-0", ConnectionNs, new JsonObject { ["type"] = "CONNECT" }, ct).ConfigureAwait(false);
        return connection;
    }

    private void Start()
    {
        _reader = Task.Run(ReadLoopAsync);
        _heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
                    await SendAsync("receiver-0", HeartbeatNs, new JsonObject { ["type"] = "PING" }, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* the link is gone; the reader reports it */ }
        });
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length <= 0 || length > 1_000_000) throw new IOException("bad message length");
                var record = new byte[length];
                await _stream.ReadExactlyAsync(record, _cts.Token).ConfigureAwait(false);
                var message = CastMessageCodec.Decode(record);
                if (message.Namespace == HeartbeatNs)
                {
                    if (message.Payload.Contains("PING")) await SendAsync(message.Source, HeartbeatNs, new JsonObject { ["type"] = "PONG" }, _cts.Token).ConfigureAwait(false);
                    continue;
                }
                if (message.Namespace == ConnectionNs && message.Payload.Contains("CLOSE")) break;
                if (JsonNode.Parse(message.Payload) is JsonObject json) Deliver(json);
            }
        }
        catch (Exception) { /* closed or broken: handled below */ }
        finally
        {
            lock (_waiters) foreach (var w in _waiters) w.TrySetException(new CastException("The Chromecast closed the connection."));
            if (!_cts.IsCancellationRequested) Closed?.Invoke();
        }
    }

    private void Deliver(JsonObject json)
    {
        lock (_waiters)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
                if (_filters[i](json)) { _waiters[i].TrySetResult(json); _waiters.RemoveAt(i); _filters.RemoveAt(i); }
        }
    }

    private Task<JsonObject> WaitFor(Func<JsonObject, bool> filter, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters) { _waiters.Add(tcs); _filters.Add(filter); }
        return tcs.Task.WaitAsync(timeout, ct);
    }

    private async Task SendAsync(string destination, string ns, JsonObject payload, CancellationToken ct)
    {
        var bytes = CastMessageCodec.Encode(new CastMessage(Sender, destination, ns, payload.ToJsonString()));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _stream.WriteAsync(bytes, ct).ConfigureAwait(false); await _stream.FlushAsync(ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    private int NextId() => Interlocked.Increment(ref _requestId);

    /// <summary>Starts the built-in media player on the Chromecast (whatever was showing is replaced).</summary>
    public async Task LaunchPlayerAsync(CancellationToken ct)
    {
        var id = NextId();
        var status = WaitFor(m => (string?)m["type"] == "RECEIVER_STATUS" && FindApp(m) is not null, TimeSpan.FromSeconds(20), ct);
        await SendAsync("receiver-0", ReceiverNs, new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultMediaReceiver, ["requestId"] = id }, ct).ConfigureAwait(false);
        var app = FindApp(await status.ConfigureAwait(false))!;
        _transportId = (string?)app["transportId"]; _sessionId = (string?)app["sessionId"];
        if (_transportId is null || _sessionId is null) throw new CastException("The Chromecast did not start its media player.");
        await SendAsync(_transportId, ConnectionNs, new JsonObject { ["type"] = "CONNECT" }, ct).ConfigureAwait(false);
    }

    private static JsonObject? FindApp(JsonObject status) =>
        (status["status"]?["applications"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => (string?)a["appId"] == DefaultMediaReceiver && a["transportId"] is not null);

    /// <summary>Asks the player to play <paramref name="url"/> (an HLS playlist of a live stream) and waits until it plays or fails.</summary>
    public async Task PlayLiveAsync(string url, string title, CancellationToken ct, string streamType = "LIVE")
    {
        if (_transportId is null || _sessionId is null) throw new CastException("The media player is not running.");
        var id = NextId();
        var result = WaitFor(m => (string?)m["type"] is "MEDIA_STATUS" or "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST", TimeSpan.FromSeconds(25), ct);
        await SendAsync(_transportId, MediaNs, new JsonObject
        {
            ["type"] = "LOAD", ["requestId"] = id, ["sessionId"] = _sessionId, ["autoplay"] = true,
            ["media"] = new JsonObject
            {
                ["contentId"] = url, ["contentType"] = "application/x-mpegURL", ["streamType"] = streamType,
                ["metadata"] = new JsonObject { ["metadataType"] = 0, ["title"] = title },
            },
        }, ct).ConfigureAwait(false);

        // MEDIA_STATUS arrives repeatedly; the first one with a state decides.
        var answer = await result.ConfigureAwait(false);
        var type = (string?)answer["type"];
        if (type != "MEDIA_STATUS") throw new CastException("The Chromecast could not play the stream.");
        var state = ((answer["status"] as JsonArray)?.FirstOrDefault() as JsonObject);
        if ((string?)state?["playerState"] == "IDLE" && (string?)state?["idleReason"] is "ERROR" or "CANCELLED") throw new CastException("The Chromecast could not play the stream.");
    }

    /// <summary>Stops the media player on the Chromecast, which returns it to what it was showing, and closes the link.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        try
        {
            if (_sessionId is not null)
                await SendAsync("receiver-0", ReceiverNs, new JsonObject { ["type"] = "STOP", ["sessionId"] = _sessionId, ["requestId"] = NextId() }, CancellationToken.None).ConfigureAwait(false);
            await SendAsync("receiver-0", ConnectionNs, new JsonObject { ["type"] = "CLOSE" }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { /* already gone */ }
        _cts.Cancel();
        try { _stream.Dispose(); } catch { }
        try { if (_reader is not null) await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

/// <summary>Finds Chromecasts (and TVs with Chromecast built in) by asking the network, on every local network card.</summary>
public static class CastDiscovery
{
    private static readonly byte[] Question = BuildQuestion();

    private static byte[] BuildQuestion()
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in "_googlecast._tcp.local".Split('.')) { bytes.Add((byte)label.Length); bytes.AddRange(Encoding.ASCII.GetBytes(label)); }
        bytes.AddRange(new byte[] { 0, 0, 12, 0x80, 0x01 }); // type PTR, class IN, "please answer me directly"
        return bytes.ToArray();
    }

    public static async Task<IReadOnlyList<CastDevice>> FindAsync(TimeSpan listenFor, CancellationToken ct)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address).ToList();
        var results = new Dictionary<string, CastDevice>();
        await Task.WhenAll(addresses.Select(async local =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                var multicast = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                for (var i = 0; i < 2; i++) { await udp.SendAsync(Question, multicast, ct).ConfigureAwait(false); await Task.Delay(200, ct).ConfigureAwait(false); }
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stop.CancelAfter(listenFor);
                while (!stop.IsCancellationRequested)
                {
                    var reply = await udp.ReceiveAsync(stop.Token).ConfigureAwait(false);
                    foreach (var device in MdnsParser.ParseCastDevices(reply.Buffer, reply.RemoteEndPoint.Address))
                        lock (results) results[device.Id] = device;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { /* this card is done or unusable */ }
        })).ConfigureAwait(false);
        return results.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
