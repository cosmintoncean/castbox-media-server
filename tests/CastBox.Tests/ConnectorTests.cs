using CastBox.MediaLog;
using Xunit;

namespace CastBox.Tests;

public sealed class ConnectorTests : IDisposable
{
    private readonly TempStore _temp = new();
    private readonly FakeMediaLog _mediaLog = new();
    private readonly MediaLogStore _store;
    private readonly WatchSyncService _service;
    private readonly MediaLogConnector _connector;
    private readonly List<TimeSpan> _waits = new();
    private int _changed;

    public ConnectorTests()
    {
        _store = _temp.Open();
        _service = new WatchSyncService(_store, _mediaLog, new FakeTvMaze(), new ManualClock());
        _service.Changed += () => _changed++;
        _connector = new MediaLogConnector(_store, _mediaLog, _service, (span, _) => { _waits.Add(span); return Task.CompletedTask; });
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Beginning_checks_the_address_starts_a_pairing_and_gives_the_page_to_open()
    {
        var (discovery, pairing, url) = await _connector.BeginAsync("thinglog.netlify.app/", "CastBox on COma", CancellationToken.None);
        Assert.Equal(_mediaLog.Found, discovery);
        Assert.Equal("K7QM-4XD2", pairing.UserCode);
        Assert.Equal("https://thinglog.netlify.app/connect/K7QM-4XD2", url);
        Assert.Equal(new[] { "https://thinglog.netlify.app" }, _mediaLog.DiscoveredAddresses);
        Assert.Equal("https://thinglog.netlify.app", _store.Read(s => s.Address));
        Assert.False(_service.IsConnected);
    }

    [Fact]
    public async Task A_bad_address_stops_before_anything_is_started()
    {
        await Assert.ThrowsAsync<MediaLogException>(() => _connector.BeginAsync("http://example.com", "x", CancellationToken.None));
        Assert.Empty(_mediaLog.DiscoveredAddresses);
    }

    [Fact]
    public async Task Approval_saves_the_token_encrypted_with_the_label_and_starts_syncing()
    {
        var (discovery, pairing, _) = await _connector.BeginAsync("https://thinglog.netlify.app", "CastBox on COma", CancellationToken.None);
        _mediaLog.Polls.Enqueue(new PairPoll(PairStatus.Pending, null));
        _mediaLog.Polls.Enqueue(new PairPoll(PairStatus.Pending, null));
        _mediaLog.Polls.Enqueue(new PairPoll(PairStatus.Approved, "mla_secret"));

        var status = await _connector.WaitAsync(discovery, pairing, CancellationToken.None);

        Assert.Equal(PairStatus.Approved, status);
        Assert.True(_service.IsConnected);
        Assert.Equal("mla_secret", _store.GetToken());
        Assert.DoesNotContain("mla_secret", File.ReadAllText(_temp.Path));
        Assert.Equal("CastBox on TESTPC", _store.Read(s => s.Label));
        Assert.Equal(_mediaLog.Found.FunctionsUrl, _store.Read(s => s.FunctionsUrl));
        Assert.Equal(new[] { 5.0, 5.0 }, _waits.Select(w => w.TotalSeconds)); // waited the server's interval between polls
        Assert.True(_changed > 0);
    }

    [Fact]
    public async Task Approval_remembers_which_account_the_connection_belongs_to()
    {
        await ConnectNow();
        var snapshot = _service.Snapshot();
        Assert.Equal("ana", snapshot.Username);
        Assert.Equal("acc-ana", snapshot.AccountId);
    }

    [Fact]
    public async Task Refreshing_picks_up_a_username_chosen_later_and_disconnecting_forgets_the_account()
    {
        _mediaLog.Username = null;
        await ConnectNow();
        Assert.Null(_service.Snapshot().Username);

        _mediaLog.Username = "ana_r";
        var before = _changed;
        await _service.RefreshAccountAsync(CancellationToken.None);
        Assert.Equal("ana_r", _service.Snapshot().Username);
        Assert.True(_changed > before);

        await _service.RefreshAccountAsync(CancellationToken.None); // nothing new: no change notification
        Assert.Equal(before + 1, _changed);

        await _connector.DisconnectAsync(CancellationToken.None);
        Assert.Null(_service.Snapshot().AccountId);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_what_was_known_and_an_unconnected_one_does_nothing()
    {
        await _service.RefreshAccountAsync(CancellationToken.None); // not connected: no call, no error
        await ConnectNow();
        _mediaLog.WhoAmIFails = true;
        await _service.RefreshAccountAsync(CancellationToken.None);
        Assert.Equal("ana", _service.Snapshot().Username);
    }

    [Fact]
    public async Task A_missing_label_does_not_undo_a_successful_connection()
    {
        _mediaLog.WhoAmIFails = true;
        var (discovery, pairing, _) = await _connector.BeginAsync("https://thinglog.netlify.app", "x", CancellationToken.None);
        _mediaLog.Polls.Enqueue(new PairPoll(PairStatus.Approved, "mla_secret"));
        Assert.Equal(PairStatus.Approved, await _connector.WaitAsync(discovery, pairing, CancellationToken.None));
        Assert.Equal("CastBox", _store.Read(s => s.Label));
    }

    [Theory]
    [InlineData(PairStatus.Denied)]
    [InlineData(PairStatus.Expired)]
    public async Task Refusal_or_expiry_ends_the_wait_without_connecting(PairStatus final)
    {
        var (discovery, pairing, _) = await _connector.BeginAsync("https://thinglog.netlify.app", "x", CancellationToken.None);
        _mediaLog.Polls.Enqueue(new PairPoll(final, null));
        Assert.Equal(final, await _connector.WaitAsync(discovery, pairing, CancellationToken.None));
        Assert.False(_service.IsConnected);
    }

    [Fact]
    public async Task Waiting_past_the_pairings_lifetime_reports_expired()
    {
        _mediaLog.Pairing = new PairStart("dev", "ABCD-2345", 20, 5);
        var (discovery, pairing, _) = await _connector.BeginAsync("https://thinglog.netlify.app", "x", CancellationToken.None);
        Assert.Equal(PairStatus.Expired, await _connector.WaitAsync(discovery, pairing, CancellationToken.None));
        Assert.Equal(5, _waits.Count); // polls at 0, 5, 10, 15 and 20 seconds of a 20 second pairing
    }

    [Fact]
    public async Task Cancelling_the_wait_stops_it()
    {
        var (discovery, pairing, _) = await _connector.BeginAsync("https://thinglog.netlify.app", "x", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var connector = new MediaLogConnector(_store, _mediaLog, _service, (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.WaitAsync(discovery, pairing, cts.Token));
        Assert.False(_service.IsConnected);
    }

    private async Task ConnectNow()
    {
        var (discovery, pairing, _) = await _connector.BeginAsync("https://thinglog.netlify.app", "x", CancellationToken.None);
        _mediaLog.Polls.Enqueue(new PairPoll(PairStatus.Approved, "mla_secret"));
        await _connector.WaitAsync(discovery, pairing, CancellationToken.None);
    }

    [Fact]
    public async Task Disconnecting_revokes_forgets_the_token_and_drops_what_was_waiting_but_keeps_saved_choices()
    {
        await ConnectNow();
        _service.Enqueue("m1", @"D:\TV\Show.S01E01.mkv", DateTime.UtcNow, "e1");
        _store.Update(s => s.Mappings["tv:show"] = new Mapping { Ids = new MediaIds { Tmdb = "1" } });

        await _connector.DisconnectAsync(CancellationToken.None);

        Assert.Equal(1, _mediaLog.Revoked);
        Assert.False(_service.IsConnected);
        Assert.Empty(_store.Read(s => s.Queue));
        Assert.Null(_store.Read(s => s.Label));
        Assert.Single(_store.Read(s => s.Mappings));
        Assert.Equal("https://thinglog.netlify.app", _store.Read(s => s.Address)); // handy for connecting again
    }

    [Fact]
    public async Task If_MediaLog_cant_be_told_the_connection_is_still_forgotten_locally_and_the_person_is_warned()
    {
        await ConnectNow();
        _mediaLog.RevokeFails = true;
        var error = await Assert.ThrowsAsync<MediaLogException>(() => _connector.DisconnectAsync(CancellationToken.None));
        Assert.Contains("offline", error.Message);
        Assert.False(_service.IsConnected);
    }
}
