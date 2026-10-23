using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CastBox.MediaLog;

/// <summary>Something the person can act on (a wrong address, a code that expired), with a message fit to show.</summary>
public sealed class MediaLogException : Exception
{
    public MediaLogException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record Discovery(string FunctionsUrl, string AnonKey);
public sealed record PairStart(string DeviceCode, string UserCode, int ExpiresInSeconds, int IntervalSeconds);
public enum PairStatus { Pending, Approved, Denied, Expired }
public sealed record PairPoll(PairStatus Status, string? Token);

public sealed record WatchRequest(
    string EventId, WatchKind Kind, DateTime WatchedAtUtc, string? Title, int? Year, int? Season, int? Episode, MediaIds? Ids);

public enum LogOutcome { Logged, AlreadyLogged, NeedsConfirmation, NotFound, EpisodeNotFound, Rejected, Unauthorized, RateLimited, Unavailable }

/// <summary>Who a connection belongs to. <see cref="AccountId"/> is MediaLog's opaque stable id for the account.</summary>
public sealed record WhoAmI(string Label, string? Username, string? AccountId);

public sealed record LogResult(LogOutcome Outcome, IReadOnlyList<Candidate>? Candidates = null, string? Message = null);

/// <summary>The MediaLog `apps` function (docs/medialog-sync-design.md, MediaLog ADR 0011).</summary>
public interface IMediaLogClient
{
    /// <summary>Reads the site's discovery file; throws <see cref="MediaLogException"/> if it isn't a MediaLog address.</summary>
    Task<Discovery> DiscoverAsync(string address, CancellationToken ct);
    Task<PairStart> PairStartAsync(Discovery discovery, string label, CancellationToken ct);
    Task<PairPoll> PairPollAsync(Discovery discovery, string deviceCode, CancellationToken ct);
    /// <summary>The connection's label, or throws <see cref="MediaLogException"/> if the token was revoked.</summary>
    Task<WhoAmI> WhoAmIAsync(Discovery discovery, string token, CancellationToken ct);
    Task RevokeAsync(Discovery discovery, string token, CancellationToken ct);
    Task<LogResult> LogWatchAsync(Discovery discovery, string token, WatchRequest request, CancellationToken ct);
}

public sealed class HttpMediaLogClient : IMediaLogClient
{
    private readonly HttpClient _http;

    public HttpMediaLogClient(HttpClient http) => _http = http;

    /// <summary>Accepts "thinglog.netlify.app", with or without scheme and trailing slash; plain http only for localhost.</summary>
    public static string NormalizeAddress(string input)
    {
        var text = input.Trim().TrimEnd('/');
        if (text.Length == 0) throw new MediaLogException("Enter your MediaLog address, for example https://yourname.netlify.app");
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new MediaLogException("That doesn't look like a web address. Use the https:// address of your MediaLog site.");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }

    public async Task<Discovery> DiscoverAsync(string address, CancellationToken ct)
    {
        var url = NormalizeAddress(address) + "/.well-known/medialog-app.json";
        string body;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new MediaLogException($"{url} answered {(int)response.StatusCode}. Check the address.");
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new MediaLogException("Couldn't reach that address. Check it and your internet connection.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new MediaLogException("That address took too long to answer.", ex); }

        // An unknown path on a MediaLog site answers 200 with its home page, so check what came back, not just the status.
        const string notMediaLog = "That address doesn't look like a MediaLog site (it has no connection details). Check it, or update MediaLog.";
        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch (JsonException) { throw new MediaLogException(notMediaLog); }
        if (node is not JsonObject obj || (int?)obj["version"] != 1 || (string?)obj["name"] != "MediaLog") throw new MediaLogException(notMediaLog);
        var functions = (string?)obj["functionsUrl"];
        var anon = (string?)obj["anonKey"];
        if (string.IsNullOrWhiteSpace(functions) || string.IsNullOrWhiteSpace(anon) || !Uri.TryCreate(functions, UriKind.Absolute, out var f) || f.Scheme != Uri.UriSchemeHttps)
            throw new MediaLogException(notMediaLog);
        return new Discovery(functions.TrimEnd('/'), anon);
    }

    public async Task<PairStart> PairStartAsync(Discovery discovery, string label, CancellationToken ct)
    {
        var (status, json) = await PostAsync(discovery, null, new JsonObject { ["action"] = "pair_start", ["label"] = label }, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.TooManyRequests) throw new MediaLogException("Too many connection attempts. Wait a minute and try again.");
        if (status != HttpStatusCode.OK || json is null) throw Failure(status, json, "start connecting");
        return new PairStart((string)json["deviceCode"]!, (string)json["userCode"]!, (int?)json["expiresIn"] ?? 600, Math.Max(2, (int?)json["interval"] ?? 5));
    }

    public async Task<PairPoll> PairPollAsync(Discovery discovery, string deviceCode, CancellationToken ct)
    {
        var (status, json) = await PostAsync(discovery, null, new JsonObject { ["action"] = "pair_poll", ["deviceCode"] = deviceCode }, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || json is null) throw Failure(status, json, "check the connection");
        var kind = (string?)json["status"] switch
        {
            "pending" => PairStatus.Pending,
            "approved" => PairStatus.Approved,
            "denied" => PairStatus.Denied,
            _ => PairStatus.Expired,
        };
        return new PairPoll(kind, kind == PairStatus.Approved ? (string?)json["token"] : null);
    }

    public async Task<WhoAmI> WhoAmIAsync(Discovery discovery, string token, CancellationToken ct)
    {
        var (status, json) = await PostAsync(discovery, token, new JsonObject { ["action"] = "whoami" }, ct).ConfigureAwait(false);
        if (status == HttpStatusCode.Unauthorized) throw new MediaLogException("MediaLog no longer accepts this connection. Connect again.");
        if (status != HttpStatusCode.OK || json is null) throw Failure(status, json, "check the connection");
        var username = (string?)json["username"];
        return new WhoAmI((string?)json["label"] ?? "CastBox", string.IsNullOrWhiteSpace(username) ? null : username, (string?)json["accountId"]);
    }

    public async Task RevokeAsync(Discovery discovery, string token, CancellationToken ct)
    {
        var (status, _) = await PostAsync(discovery, token, new JsonObject { ["action"] = "revoke" }, ct).ConfigureAwait(false);
        // Already revoked is as good as revoked.
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Unauthorized)) throw new MediaLogException("MediaLog couldn't be reached to disconnect. You can remove CastBox under Connected apps in MediaLog.");
    }

    public async Task<LogResult> LogWatchAsync(Discovery discovery, string token, WatchRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["action"] = "log_watch",
            ["client_event_id"] = request.EventId,
            ["kind"] = request.Kind == WatchKind.Movie ? "movie" : "episode",
            ["watched_at"] = request.WatchedAtUtc.ToUniversalTime().ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(request.Title)) body["title"] = request.Title;
        if (request.Year is not null) body["year"] = request.Year;
        if (request.Kind == WatchKind.Episode) { body["season"] = request.Season; body["episode"] = request.Episode; }
        if (request.Ids is { Any: true } ids)
        {
            var o = new JsonObject();
            if (ids.Imdb is not null) o["imdb"] = ids.Imdb;
            if (ids.Tvdb is not null) o["tvdb"] = ids.Tvdb;
            if (ids.Tmdb is not null) o["tmdb"] = ids.Tmdb;
            body["ids"] = o;
        }

        HttpStatusCode status; JsonNode? json;
        try { (status, json) = await PostAsync(discovery, token, body, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new LogResult(LogOutcome.Unavailable, Message: "MediaLog couldn't be reached.");
        }

        var message = (string?)json?["message"];
        switch (status)
        {
            case HttpStatusCode.OK:
                return new LogResult((string?)json?["status"] == "already_logged" ? LogOutcome.AlreadyLogged : LogOutcome.Logged);
            case HttpStatusCode.Conflict:
                var candidates = new List<Candidate>();
                foreach (var c in (json?["candidates"] as JsonArray) ?? new JsonArray())
                {
                    if (c is not JsonObject co) continue;
                    candidates.Add(new Candidate
                    {
                        Provider = (string?)co["provider"] ?? "", ExternalId = (string?)co["externalId"] ?? "",
                        Title = (string?)co["title"] ?? "", Year = (int?)co["year"], Poster = (string?)co["poster"],
                    });
                }
                return new LogResult(LogOutcome.NeedsConfirmation, candidates);
            case HttpStatusCode.NotFound:
                return new LogResult((string?)json?["status"] == "episode_not_found" ? LogOutcome.EpisodeNotFound : LogOutcome.NotFound);
            case HttpStatusCode.Unauthorized: return new LogResult(LogOutcome.Unauthorized, Message: message);
            case HttpStatusCode.TooManyRequests: return new LogResult(LogOutcome.RateLimited, Message: message);
            case HttpStatusCode.BadRequest: return new LogResult(LogOutcome.Rejected, Message: message ?? "MediaLog rejected the request.");
            default: return new LogResult(LogOutcome.Unavailable, Message: message ?? $"MediaLog answered {(int)status}.");
        }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Json)> PostAsync(Discovery d, string? token, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, d.FunctionsUrl + "/apps")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("apikey", d.AnonKey);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? json = null;
        try { if (text.Length > 0) json = JsonNode.Parse(text); } catch (JsonException) { }
        return (response.StatusCode, json);
    }

    private static MediaLogException Failure(HttpStatusCode status, JsonNode? json, string doing) =>
        new((string?)json?["message"] is { Length: > 0 } m ? m : $"Couldn't {doing} (MediaLog answered {(int)status}).");
}
