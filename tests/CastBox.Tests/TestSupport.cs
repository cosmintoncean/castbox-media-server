using System.Net;
using System.Text;
using CastBox.MediaLog;

namespace CastBox.Tests;

/// <summary>A clock the tests move by hand.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>Not encryption: just proves the token is run through the protector before it reaches the file.</summary>
public sealed class ReversingProtector : ISecretProtector
{
    public string Protect(string plain) => "enc:" + new string(plain.Reverse().ToArray());
    public string? Unprotect(string protectedText) => protectedText.StartsWith("enc:") ? new string(protectedText[4..].Reverse().ToArray()) : null;
}

public sealed class TempStore : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "castbox-tests-" + Guid.NewGuid().ToString("N"), "medialog.json");
    public MediaLogStore Open() => new(Path, new ReversingProtector());
    public void Dispose() { try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); } catch { } }
}

public sealed class FakeMediaLog : IMediaLogClient
{
    public Queue<LogResult> LogResults { get; } = new();
    public LogResult DefaultResult { get; set; } = new(LogOutcome.Logged);
    public List<WatchRequest> Requests { get; } = new();
    public List<string> Tokens { get; } = new();
    public Queue<PairPoll> Polls { get; } = new();
    public string Label { get; set; } = "CastBox on TESTPC";
    public bool WhoAmIFails { get; set; }
    public bool RevokeFails { get; set; }
    public int Revoked { get; private set; }
    public Discovery Found { get; set; } = new("https://x.supabase.co/functions/v1", "anon");
    public PairStart Pairing { get; set; } = new("device-code", "K7QM-4XD2", 600, 5);
    public List<string> DiscoveredAddresses { get; } = new();

    public Task<Discovery> DiscoverAsync(string address, CancellationToken ct) { DiscoveredAddresses.Add(address); return Task.FromResult(Found); }
    public Task<PairStart> PairStartAsync(Discovery discovery, string label, CancellationToken ct) => Task.FromResult(Pairing);
    public Task<PairPoll> PairPollAsync(Discovery discovery, string deviceCode, CancellationToken ct) =>
        Task.FromResult(Polls.Count > 0 ? Polls.Dequeue() : new PairPoll(PairStatus.Pending, null));
    public Task<string> WhoAmIAsync(Discovery discovery, string token, CancellationToken ct) =>
        WhoAmIFails ? throw new MediaLogException("revoked") : Task.FromResult(Label);
    public Task RevokeAsync(Discovery discovery, string token, CancellationToken ct)
    {
        Revoked++;
        return RevokeFails ? throw new MediaLogException("offline") : Task.CompletedTask;
    }
    public Task<LogResult> LogWatchAsync(Discovery discovery, string token, WatchRequest request, CancellationToken ct)
    {
        Requests.Add(request); Tokens.Add(token);
        return Task.FromResult(LogResults.Count > 0 ? LogResults.Dequeue() : DefaultResult);
    }
}

public sealed class FakeTvMaze : ITvMaze
{
    public Dictionary<string, MediaIds> Shows { get; } = new();
    public bool Fails { get; set; }
    public int Calls { get; private set; }
    public Task<MediaIds?> FindShowAsync(string title, int? year, CancellationToken ct)
    {
        Calls++;
        if (Fails) throw new HttpRequestException("tvmaze down");
        return Task.FromResult(Shows.GetValueOrDefault(TitleParser.Normalize(title)));
    }
}

/// <summary>Answers HttpClient calls from a script, and records what was asked.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);
    public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return Respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, string contentType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, contentType) };
}
