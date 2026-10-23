using System.Net;
using System.Text.Json.Nodes;
using CastBox.MediaLog;
using Xunit;

namespace CastBox.Tests;

public class HttpMediaLogClientTests
{
    private static readonly Discovery D = new("https://x.supabase.co/functions/v1", "anon-key");
    private readonly ScriptedHandler _handler = new();
    private readonly HttpMediaLogClient _client;

    public HttpMediaLogClientTests() => _client = new HttpMediaLogClient(new HttpClient(_handler));

    private static WatchRequest Episode(MediaIds? ids = null) =>
        new("event-1", WatchKind.Episode, new DateTime(2026, 10, 23, 20, 15, 0, DateTimeKind.Utc), "Pam and Tommy", 2022, 1, 2, ids);

    // Addresses -----------------------------------------------------------------------------

    [Theory]
    [InlineData("https://thinglog.netlify.app/", "https://thinglog.netlify.app")]
    [InlineData("  thinglog.netlify.app  ", "https://thinglog.netlify.app")]
    [InlineData("https://thinglog.netlify.app/some/path/", "https://thinglog.netlify.app/some/path")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    public void Addresses_are_cleaned_up(string typed, string expected) => Assert.Equal(expected, HttpMediaLogClient.NormalizeAddress(typed));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    public void Unusable_addresses_are_refused_with_a_message(string typed) =>
        Assert.Throws<MediaLogException>(() => HttpMediaLogClient.NormalizeAddress(typed));

    // Discovery ------------------------------------------------------------------------------

    private Task<Discovery> Discover(HttpStatusCode status, string body, string contentType = "application/json")
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(status, body, contentType);
        return _client.DiscoverAsync("https://thinglog.netlify.app", CancellationToken.None);
    }

    [Fact]
    public async Task Discovery_reads_the_sites_well_known_file()
    {
        var discovery = await Discover(HttpStatusCode.OK, """{"version":1,"name":"MediaLog","functionsUrl":"https://x.supabase.co/functions/v1/","anonKey":"k"}""");
        Assert.Equal(new Discovery("https://x.supabase.co/functions/v1", "k"), discovery);
        Assert.Equal("https://thinglog.netlify.app/.well-known/medialog-app.json", _handler.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task The_sites_home_page_answering_200_is_not_mistaken_for_the_file()
    {
        // An unknown path on a MediaLog site returns index.html with 200.
        var error = await Assert.ThrowsAsync<MediaLogException>(() => Discover(HttpStatusCode.OK, "<!doctype html><html></html>", "text/html"));
        Assert.Contains("doesn't look like a MediaLog site", error.Message);
    }

    [Theory]
    [InlineData("""{"version":2,"name":"MediaLog","functionsUrl":"https://x.supabase.co/functions/v1","anonKey":"k"}""")]
    [InlineData("""{"version":1,"name":"Other","functionsUrl":"https://x.supabase.co/functions/v1","anonKey":"k"}""")]
    [InlineData("""{"version":1,"name":"MediaLog","anonKey":"k"}""")]
    [InlineData("""{"version":1,"name":"MediaLog","functionsUrl":"http://x.supabase.co/functions/v1","anonKey":"k"}""")]
    [InlineData("""{"version":1,"name":"MediaLog","functionsUrl":"https://x.supabase.co/functions/v1"}""")]
    [InlineData("[1,2,3]")]
    public async Task A_file_that_is_not_a_complete_MediaLog_file_is_refused(string body) =>
        await Assert.ThrowsAsync<MediaLogException>(() => Discover(HttpStatusCode.OK, body));

    [Fact]
    public async Task A_404_or_server_error_on_discovery_says_which_address_to_check()
    {
        var error = await Assert.ThrowsAsync<MediaLogException>(() => Discover(HttpStatusCode.NotFound, "no"));
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task An_unreachable_site_gives_a_friendly_message()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("dns");
        var error = await Assert.ThrowsAsync<MediaLogException>(() => _client.DiscoverAsync("https://nowhere.invalid", CancellationToken.None));
        Assert.Contains("Couldn't reach", error.Message);
    }

    // Pairing --------------------------------------------------------------------------------

    [Fact]
    public async Task Pair_start_posts_the_label_with_only_the_public_key()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"deviceCode":"dev","userCode":"K7QM-4XD2","expiresIn":600,"interval":5,"connectPath":"/connect"}""");
        var pairing = await _client.PairStartAsync(D, "CastBox on COma", CancellationToken.None);
        Assert.Equal(new PairStart("dev", "K7QM-4XD2", 600, 5), pairing);

        var (request, body) = _handler.Calls.Single();
        Assert.Equal("https://x.supabase.co/functions/v1/apps", request.RequestUri!.ToString());
        Assert.Equal("anon-key", request.Headers.GetValues("apikey").Single());
        Assert.Null(request.Headers.Authorization);
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("pair_start", (string?)json["action"]);
        Assert.Equal("CastBox on COma", (string?)json["label"]);
    }

    [Fact]
    public async Task Pair_start_too_fast_asks_the_person_to_wait()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""");
        var error = await Assert.ThrowsAsync<MediaLogException>(() => _client.PairStartAsync(D, "x", CancellationToken.None));
        Assert.Contains("Wait a minute", error.Message);
    }

    [Theory]
    [InlineData("pending", PairStatus.Pending)]
    [InlineData("denied", PairStatus.Denied)]
    [InlineData("expired", PairStatus.Expired)]
    [InlineData("something-new", PairStatus.Expired)]
    public async Task Pair_poll_reports_the_state_without_a_token(string wire, PairStatus expected)
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.OK, $$"""{"status":"{{wire}}"}""");
        var poll = await _client.PairPollAsync(D, "dev", CancellationToken.None);
        Assert.Equal(new PairPoll(expected, null), poll);
    }

    [Fact]
    public async Task Pair_poll_returns_the_token_when_approved()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"status":"approved","token":"mla_abc"}""");
        Assert.Equal(new PairPoll(PairStatus.Approved, "mla_abc"), await _client.PairPollAsync(D, "dev", CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_token_is_reported_by_whoami_and_revoke_tolerates_it()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""");
        await Assert.ThrowsAsync<MediaLogException>(() => _client.WhoAmIAsync(D, "mla_old", CancellationToken.None));
        await _client.RevokeAsync(D, "mla_old", CancellationToken.None); // already revoked: fine
    }

    [Fact]
    public async Task Whoami_sends_the_token_as_a_bearer_and_returns_the_label()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"label":"CastBox on COma","scopes":["log_watches"]}""");
        Assert.Equal("CastBox on COma", await _client.WhoAmIAsync(D, "mla_abc", CancellationToken.None));
        Assert.Equal("Bearer mla_abc", _handler.Calls.Single().Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Revoke_failing_for_other_reasons_is_reported()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.InternalServerError, "{}");
        await Assert.ThrowsAsync<MediaLogException>(() => _client.RevokeAsync(D, "mla_abc", CancellationToken.None));
    }

    // Logging ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_episode_is_posted_with_snake_case_fields_ids_and_the_bearer_token()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"status":"logged","itemId":"i","episodeName":"Two"}""");
        var result = await _client.LogWatchAsync(D, "mla_abc", Episode(new MediaIds { Imdb = "tt13659418", Tvdb = "400690" }), CancellationToken.None);
        Assert.Equal(LogOutcome.Logged, result.Outcome);

        var (request, body) = _handler.Calls.Single();
        Assert.Equal("Bearer mla_abc", request.Headers.Authorization!.ToString());
        Assert.Equal("anon-key", request.Headers.GetValues("apikey").Single());
        var json = JsonNode.Parse(body!)!;
        Assert.Equal("log_watch", (string?)json["action"]);
        Assert.Equal("event-1", (string?)json["client_event_id"]);
        Assert.Equal("episode", (string?)json["kind"]);
        Assert.Equal("2026-10-23T20:15:00.0000000Z", (string?)json["watched_at"]);
        Assert.Equal(("Pam and Tommy", 2022, 1, 2), ((string?)json["title"], (int?)json["year"], (int?)json["season"], (int?)json["episode"]));
        Assert.Equal("tt13659418", (string?)json["ids"]!["imdb"]);
        Assert.Equal("400690", (string?)json["ids"]!["tvdb"]);
        Assert.Null(json["ids"]!["tmdb"]);
    }

    [Fact]
    public async Task A_movie_has_no_season_or_episode_and_no_ids_object_without_ids()
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(HttpStatusCode.OK, """{"status":"logged"}""");
        await _client.LogWatchAsync(D, "t", new WatchRequest("m1", WatchKind.Movie, DateTime.UtcNow, "Obsession", 2025, null, null, null), CancellationToken.None);
        var json = JsonNode.Parse(_handler.Calls.Single().Body!)!;
        Assert.Equal("movie", (string?)json["kind"]);
        Assert.Null(json["season"]);
        Assert.Null(json["episode"]);
        Assert.Null(json["ids"]);
    }

    private async Task<LogResult> Log(HttpStatusCode status, string json)
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(status, json);
        return await _client.LogWatchAsync(D, "t", Episode(), CancellationToken.None);
    }

    [Fact]
    public async Task Already_logged_is_its_own_outcome() => Assert.Equal(LogOutcome.AlreadyLogged, (await Log(HttpStatusCode.OK, """{"status":"already_logged"}""")).Outcome);

    [Fact]
    public async Task A_409_carries_its_candidates()
    {
        var result = await Log(HttpStatusCode.Conflict, """{"status":"needs_confirmation","candidates":[{"provider":"tmdb_tv","externalId":"1396","title":"Pam & Tommy","year":2022,"poster":"http://img"},{"provider":"tmdb_tv","externalId":"9","title":"Other"}]}""");
        Assert.Equal(LogOutcome.NeedsConfirmation, result.Outcome);
        Assert.Equal(2, result.Candidates!.Count);
        Assert.Equal(("tmdb_tv", "1396", "Pam & Tommy", 2022, "http://img"), (result.Candidates[0].Provider, result.Candidates[0].ExternalId, result.Candidates[0].Title, result.Candidates[0].Year, result.Candidates[0].Poster));
        Assert.Null(result.Candidates[1].Year);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"status":"not_found"}""", LogOutcome.NotFound)]
    [InlineData(HttpStatusCode.NotFound, """{"status":"episode_not_found","itemId":"i"}""", LogOutcome.EpisodeNotFound)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""", LogOutcome.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""", LogOutcome.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_year","message":"year must be a whole number"}""", LogOutcome.Rejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"catalog_unavailable"}""", LogOutcome.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", LogOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, "", LogOutcome.Unavailable)]
    public async Task Every_answer_maps_to_an_outcome(HttpStatusCode status, string json, LogOutcome expected) =>
        Assert.Equal(expected, (await Log(status, json)).Outcome);

    [Fact]
    public async Task A_rejected_request_keeps_MediaLogs_message() =>
        Assert.Equal("year must be a whole number", (await Log(HttpStatusCode.BadRequest, """{"error":"invalid_year","message":"year must be a whole number"}""")).Message);

    [Fact]
    public async Task A_network_failure_is_a_temporary_outcome_not_an_exception()
    {
        _handler.Respond = (_, _) => throw new HttpRequestException("offline");
        var result = await _client.LogWatchAsync(D, "t", Episode(), CancellationToken.None);
        Assert.Equal(LogOutcome.Unavailable, result.Outcome);
    }
}

public class TvMazeClientTests
{
    private readonly ScriptedHandler _handler = new();
    private readonly TvMazeClient _client;

    public TvMazeClientTests() => _client = new TvMazeClient(new HttpClient(_handler));

    private Task<MediaIds?> Find(string title, int? year, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _handler.Respond = (_, _) => ScriptedHandler.Json(status, json);
        return _client.FindShowAsync(title, year, CancellationToken.None);
    }

    private const string PamAndTommy = """{"id":52475,"name":"Pam & Tommy","premiered":"2022-02-02","externals":{"tvrage":null,"thetvdb":400690,"imdb":"tt13659418"}}""";

    [Fact]
    public async Task A_show_with_the_same_title_gives_its_imdb_and_thetvdb_ids()
    {
        var ids = await Find("Pam and Tommy", null, PamAndTommy);
        Assert.Equal(("tt13659418", "400690"), (ids?.Imdb, ids?.Tvdb));
        Assert.Equal("https://api.tvmaze.com/singlesearch/shows?q=Pam%20and%20Tommy", _handler.Calls.Single().Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_different_title_is_not_accepted_just_because_TVmaze_found_something() =>
        Assert.Null(await Find("Pam", null, PamAndTommy));

    [Fact]
    public async Task The_premiere_year_must_be_within_a_year_when_one_is_given()
    {
        Assert.NotNull(await Find("Pam & Tommy", 2021, PamAndTommy));
        Assert.Null(await Find("Pam & Tommy", 2010, PamAndTommy));
    }

    [Fact]
    public async Task A_show_with_no_usable_ids_is_null() =>
        Assert.Null(await Find("Pam & Tommy", null, """{"name":"Pam & Tommy","externals":{"thetvdb":null,"imdb":null}}"""));

    [Fact]
    public async Task No_such_show_is_null() => Assert.Null(await Find("Nothing Here", null, "{}", HttpStatusCode.NotFound));

    [Fact]
    public async Task A_server_error_is_an_exception_so_it_is_not_mistaken_for_no_such_show() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => Find("Pam & Tommy", null, "{}", HttpStatusCode.InternalServerError));
}
