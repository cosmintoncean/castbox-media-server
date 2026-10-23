using CastBox.MediaLog;
using Xunit;

namespace CastBox.Tests;

public sealed class WatchSyncServiceTests : IDisposable
{
    private const string PamEpisode1 = @"D:\TV\Pam.and.Tommy.S01.1080p.WEB-DL\Pam.and.Tommy.S01E01.Drilling.and.Pounding.1080p.mkv";
    private const string PamEpisode2 = @"D:\TV\Pam.and.Tommy.S01.1080p.WEB-DL\Pam.and.Tommy.S01E02.I.Love.You.Tommy.1080p.mkv";
    private const string Movie = @"D:\Movies\Obsession.2025.1080p.BluRay.x264.mkv";

    private readonly TempStore _temp = new();
    private readonly ManualClock _clock = new();
    private readonly FakeMediaLog _mediaLog = new();
    private readonly FakeTvMaze _tvMaze = new();
    private readonly List<string> _log = new();
    private MediaLogStore _store;
    private WatchSyncService _service;

    public WatchSyncServiceTests()
    {
        _store = _temp.Open();
        _store.Update(s => { s.FunctionsUrl = "https://x.supabase.co/functions/v1"; s.AnonKey = "anon"; s.Label = "CastBox on TESTPC"; });
        _store.SetToken("mla_secret_token");
        _service = NewService(_store);
        _tvMaze.Shows["pamandtommy"] = new MediaIds { Imdb = "tt13659418", Tvdb = "400690" };
    }

    private WatchSyncService NewService(MediaLogStore store) => new(store, _mediaLog, _tvMaze, _clock, _log.Add);

    public void Dispose() => _temp.Dispose();

    private EnqueueResult Add(string file, string eventId, int minutesAgo = 5) =>
        _service.Enqueue("media-" + eventId, file, _clock.GetUtcNow().UtcDateTime.AddMinutes(-minutesAgo), eventId);

    private Task Process() => _service.ProcessDueAsync(CancellationToken.None);

    // Intake ------------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_queued_when_not_connected()
    {
        _store.SetToken(null);
        Assert.Equal(EnqueueResult.Ignored, Add(PamEpisode1, "e1"));
        Assert.Empty(_store.Read(s => s.Queue));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_event_needs_an_id(string eventId) => Assert.Equal(EnqueueResult.Invalid, Add(PamEpisode1, eventId));

    [Fact]
    public void An_event_id_longer_than_100_characters_is_invalid() => Assert.Equal(EnqueueResult.Invalid, Add(PamEpisode1, new string('x', 101)));

    [Fact]
    public async Task The_same_event_id_is_accepted_twice_but_queued_and_sent_once()
    {
        Assert.Equal(EnqueueResult.Accepted, Add(PamEpisode1, "e1"));
        Assert.Equal(EnqueueResult.Accepted, Add(PamEpisode1, "e1"));
        Assert.Single(_store.Read(s => s.Queue));
        await Process();
        Assert.Equal(EnqueueResult.Accepted, Add(PamEpisode1, "e1")); // already sent: still just "accepted"
        await Process();
        Assert.Single(_mediaLog.Requests);
    }

    // Sending -----------------------------------------------------------------------------

    [Fact]
    public async Task An_episode_is_sent_with_the_show_ids_from_TVmaze_and_the_time_it_finished()
    {
        Add(PamEpisode1, "e1", minutesAgo: 7);
        await Process();

        var request = Assert.Single(_mediaLog.Requests);
        Assert.Equal((WatchKind.Episode, "Pam and Tommy", 1, 1), (request.Kind, request.Title, request.Season, request.Episode));
        Assert.Equal("tt13659418", request.Ids?.Imdb);
        Assert.Equal("400690", request.Ids?.Tvdb);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(-7), request.WatchedAtUtc);
        Assert.Equal("mla_secret_token", Assert.Single(_mediaLog.Tokens));
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.Equal(1, _service.Snapshot().SentToday);
    }

    [Fact]
    public async Task A_shows_ids_are_looked_up_once()
    {
        Add(PamEpisode1, "e1"); Add(PamEpisode2, "e2");
        await Process();
        Assert.Equal(2, _mediaLog.Requests.Count);
        Assert.Equal(1, _tvMaze.Calls);
    }

    [Fact]
    public async Task Episodes_are_sent_oldest_first()
    {
        Add(PamEpisode2, "later", minutesAgo: 1); Add(PamEpisode1, "earlier", minutesAgo: 30);
        await Process();
        Assert.Equal(new[] { "earlier", "later" }, _mediaLog.Requests.Select(r => r.EventId));
    }

    [Fact]
    public async Task When_TVmaze_is_down_the_episode_is_still_sent_by_title_and_nothing_is_cached()
    {
        _tvMaze.Fails = true;
        Add(PamEpisode1, "e1");
        await Process();
        var request = Assert.Single(_mediaLog.Requests);
        Assert.Null(request.Ids);
        Assert.Empty(_store.Read(s => s.ShowIds));
    }

    [Fact]
    public async Task A_movie_is_sent_by_title_and_year_without_asking_TVmaze()
    {
        Add(Movie, "m1");
        await Process();
        var request = Assert.Single(_mediaLog.Requests);
        Assert.Equal((WatchKind.Movie, "Obsession", 2025), (request.Kind, request.Title, request.Year));
        Assert.Equal(0, _tvMaze.Calls);
    }

    [Fact]
    public async Task An_already_watched_answer_counts_as_done()
    {
        _mediaLog.LogResults.Enqueue(new LogResult(LogOutcome.AlreadyLogged));
        Add(PamEpisode1, "e1");
        await Process();
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.Equal("already watched", _store.Read(s => s.Recent.Single().Outcome));
    }

    // Needs confirmation --------------------------------------------------------------------

    private static Candidate Cand(string id, string title, int year) => new() { Provider = "tmdb_tv", ExternalId = id, Title = title, Year = year };

    [Fact]
    public async Task An_ambiguous_title_waits_for_the_person_with_its_candidates_and_is_not_retried()
    {
        _mediaLog.LogResults.Enqueue(new LogResult(LogOutcome.NeedsConfirmation, new[] { Cand("1", "Pam & Tommy", 2022), Cand("2", "Pam & Tommy Redux", 2023) }));
        Add(PamEpisode1, "e1");
        await Process();
        await Process();

        var snapshot = _service.Snapshot();
        Assert.Equal((0, 1), (snapshot.Waiting, snapshot.NeedsConfirmation));
        Assert.Equal(2, snapshot.NeedsConfirmationItems[0].Candidates.Count);
        Assert.Equal("More than one title could match.", snapshot.NeedsConfirmationItems[0].Reason);
        Assert.Single(_mediaLog.Requests);
    }

    [Fact]
    public async Task Confirming_a_candidate_saves_the_choice_and_resends_with_its_id()
    {
        _mediaLog.LogResults.Enqueue(new LogResult(LogOutcome.NeedsConfirmation, new[] { Cand("1396", "Pam & Tommy", 2022) }));
        Add(PamEpisode1, "e1");
        await Process();

        _service.Confirm("e1", Cand("1396", "Pam & Tommy", 2022));
        await Process();

        Assert.Equal(2, _mediaLog.Requests.Count);
        Assert.Equal("1396", _mediaLog.Requests[1].Ids?.Tmdb);
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.Equal("Pam & Tommy (2022)", _store.Read(s => s.Mappings["tv:pamandtommy"].Display));
    }

    [Fact]
    public async Task A_confirmed_show_uses_its_saved_ids_for_later_episodes_without_asking_TVmaze_again()
    {
        _tvMaze.Shows.Clear();
        _mediaLog.LogResults.Enqueue(new LogResult(LogOutcome.NeedsConfirmation, new[] { Cand("1396", "Pam & Tommy", 2022) }));
        Add(PamEpisode1, "e1");
        await Process();
        _service.Confirm("e1", Cand("1396", "Pam & Tommy", 2022));
        await Process();
        var tvMazeCalls = _tvMaze.Calls;

        Add(PamEpisode2, "e2");
        await Process();
        Assert.Equal("1396", _mediaLog.Requests.Last().Ids?.Tmdb);
        Assert.Equal(tvMazeCalls, _tvMaze.Calls);
    }

    [Fact]
    public async Task Confirming_one_episode_releases_every_waiting_episode_of_that_show()
    {
        _mediaLog.DefaultResult = new LogResult(LogOutcome.NeedsConfirmation, new[] { Cand("1396", "Pam & Tommy", 2022) });
        Add(PamEpisode1, "e1"); Add(PamEpisode2, "e2");
        await Process();
        Assert.Equal(2, _service.Snapshot().NeedsConfirmation);

        _mediaLog.DefaultResult = new LogResult(LogOutcome.Logged);
        _service.Confirm("e1", Cand("1396", "Pam & Tommy", 2022));
        await Process();
        Assert.Equal(0, _service.Snapshot().NeedsConfirmation);
        Assert.Empty(_store.Read(s => s.Queue));
    }

    [Theory]
    [InlineData(LogOutcome.NotFound, "MediaLog couldn't find this title.")]
    [InlineData(LogOutcome.EpisodeNotFound, "MediaLog doesn't have this episode (yet).")]
    public async Task Not_found_outcomes_wait_for_the_person_too(LogOutcome outcome, string reason)
    {
        _mediaLog.LogResults.Enqueue(new LogResult(outcome));
        Add(PamEpisode1, "e1");
        await Process();
        var item = Assert.Single(_service.Snapshot().NeedsConfirmationItems);
        Assert.Equal(reason, item.Reason);
        Assert.Empty(item.Candidates);
    }

    [Fact]
    public async Task A_rejected_event_waits_with_MediaLogs_message()
    {
        _mediaLog.LogResults.Enqueue(new LogResult(LogOutcome.Rejected, Message: "year must be a whole number"));
        Add(Movie, "m1");
        await Process();
        Assert.Equal("year must be a whole number", _service.Snapshot().NeedsConfirmationItems.Single().Reason);
    }

    [Fact]
    public void A_file_with_no_readable_title_waits_without_being_sent()
    {
        Assert.Equal(EnqueueResult.Accepted, Add(@"D:\Home videos\holiday clip.mp4", "u1"));
        Assert.Equal(1, _service.Snapshot().NeedsConfirmation);
        Assert.Empty(_mediaLog.Requests);
    }

    [Fact]
    public async Task The_person_can_say_what_an_unrecognised_file_is()
    {
        Add(@"D:\Home videos\holiday clip.mp4", "u1");
        _service.SetManual("u1", WatchKind.Movie, "Holiday", 2024, null, null);
        await Process();
        var request = Assert.Single(_mediaLog.Requests);
        Assert.Equal(("Holiday", 2024, WatchKind.Movie), (request.Title, request.Year, request.Kind));
    }

    [Fact]
    public void An_episode_needs_a_season_and_an_episode_to_be_released_manually()
    {
        Add(@"D:\Home videos\holiday clip.mp4", "u1");
        _service.SetManual("u1", WatchKind.Episode, "Some Show", null, null, null);
        Assert.Equal(1, _service.Snapshot().NeedsConfirmation);
    }

    [Fact]
    public async Task Dont_sync_this_drops_only_that_watch()
    {
        _mediaLog.LogResults.Enqueue(new LogResult(LogOutcome.NotFound));
        Add(PamEpisode1, "e1");
        await Process();
        _service.DontSyncThis("e1");
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.Equal(EnqueueResult.Accepted, Add(PamEpisode2, "e2"));
    }

    [Fact]
    public async Task Never_sync_a_title_drops_what_waits_and_ignores_later_watches_of_it()
    {
        _mediaLog.DefaultResult = new LogResult(LogOutcome.NotFound);
        Add(PamEpisode1, "e1"); Add(PamEpisode2, "e2");
        await Process();
        _service.DontSyncTitle("e1");
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.Equal(EnqueueResult.Ignored, Add(@"D:\TV\Pam.and.Tommy.S01.1080p\Pam.and.Tommy.S01E03.1080p.mkv", "e3"));
    }

    [Fact]
    public async Task Forgetting_saved_choices_clears_mappings_and_show_lookups()
    {
        Add(PamEpisode1, "e1");
        await Process();
        Assert.NotEmpty(_store.Read(s => s.ShowIds));
        _service.ForgetSavedChoices();
        Assert.Empty(_store.Read(s => s.ShowIds));
        Assert.Empty(_store.Read(s => s.Mappings));
    }

    // Failure handling -----------------------------------------------------------------------------

    [Fact]
    public async Task A_revoked_token_pauses_everything_and_keeps_the_queue_until_reconnected()
    {
        _mediaLog.DefaultResult = new LogResult(LogOutcome.Unauthorized);
        Add(PamEpisode1, "e1"); Add(PamEpisode2, "e2");
        await Process();
        Assert.True(_service.Snapshot().NeedsReconnect);
        Assert.Single(_mediaLog.Requests); // stopped at the first
        Assert.Equal(2, _service.Snapshot().Waiting);

        await Process(); // paused: nothing more is attempted
        Assert.Single(_mediaLog.Requests);

        _mediaLog.DefaultResult = new LogResult(LogOutcome.Logged);
        _store.SetToken("mla_new_token");
        _service.ConnectionChanged();
        await Process();
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.False(_service.Snapshot().NeedsReconnect);
        Assert.Equal("mla_new_token", _mediaLog.Tokens.Last());
    }

    [Theory]
    [InlineData(LogOutcome.Unavailable)]
    [InlineData(LogOutcome.RateLimited)]
    public async Task Temporary_failures_retry_later_with_a_growing_delay_without_blocking_other_watches(LogOutcome failure)
    {
        _mediaLog.LogResults.Enqueue(new LogResult(failure, Message: "down"));
        Add(PamEpisode1, "first", minutesAgo: 30); Add(PamEpisode2, "second", minutesAgo: 10);
        await Process();

        // A failure ends the cycle (so a service that is down isn't hammered); the second isn't tried yet.
        Assert.Single(_mediaLog.Requests);
        var queued = _store.Read(s => s.Queue.Select(q => (q.EventId, q.Attempts, q.NextAttemptUtc)).ToList());
        Assert.Equal(("first", 1), (queued[0].EventId, queued[0].Attempts));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(1), queued[0].NextAttemptUtc);

        // The next cycle: the first is still waiting out its delay, the second is due and goes out.
        await Process();
        Assert.Equal(new[] { "first", "second" }, _mediaLog.Requests.Select(r => r.EventId));

        _clock.Advance(TimeSpan.FromMinutes(1));
        await Process();
        Assert.Equal(new[] { "first", "second", "first" }, _mediaLog.Requests.Select(r => r.EventId));
        Assert.Empty(_store.Read(s => s.Queue));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(50, 60)]
    public void Backoff_doubles_from_a_minute_up_to_an_hour(int attempts, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), WatchSyncService.Backoff(attempts));

    [Fact]
    public async Task A_saved_dont_sync_choice_removes_an_already_queued_watch_without_sending_it()
    {
        Add(PamEpisode1, "e1");
        _store.Update(s => s.Mappings["tv:pamandtommy"] = new Mapping { DontSync = true });
        await Process();
        Assert.Empty(_mediaLog.Requests);
        Assert.Empty(_store.Read(s => s.Queue));
    }

    // Persistence -----------------------------------------------------------------------------------

    [Fact]
    public void The_queue_survives_a_restart_and_the_token_is_never_in_the_file_in_plain_text()
    {
        Add(PamEpisode1, "e1");
        var file = File.ReadAllText(_temp.Path);
        Assert.DoesNotContain("mla_secret_token", file);
        Assert.DoesNotContain("secret_token", file);

        var reopened = NewService(_temp.Open());
        var snapshot = reopened.Snapshot();
        Assert.True(snapshot.Connected);
        Assert.Equal(1, snapshot.Waiting);
    }

    [Fact]
    public void A_damaged_file_does_not_stop_the_app_and_is_kept_for_diagnosis()
    {
        File.WriteAllText(_temp.Path, "{ not json");
        var store = _temp.Open();
        Assert.Null(store.GetToken());
        Assert.True(File.Exists(_temp.Path + ".damaged"));
    }

    [Fact]
    public async Task The_worker_sends_a_queued_watch_without_being_asked()
    {
        using var cts = new CancellationTokenSource();
        var worker = _service.RunAsync(cts.Token);
        Add(PamEpisode1, "e1");
        for (var i = 0; i < 100 && _mediaLog.Requests.Count == 0; i++) await Task.Delay(20);
        cts.Cancel();
        await worker;
        Assert.Single(_mediaLog.Requests);
    }
}
