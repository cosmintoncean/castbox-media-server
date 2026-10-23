namespace CastBox.MediaLog;

public enum EnqueueResult
{
    /// <summary>Queued, or already known (the TV retried). Either way the TV is done.</summary>
    Accepted,
    /// <summary>Not queued because MediaLog isn't connected, or the person chose not to sync this show.</summary>
    Ignored,
    Invalid,
}

public sealed record SyncSnapshot(
    bool Connected, bool NeedsReconnect, string? Label, string? Address, string? Username, string? AccountId,
    int Waiting, int NeedsConfirmation, int SentToday, IReadOnlyList<QueuedWatch> NeedsConfirmationItems);

/// <summary>
/// Turns "the TV finished this file" into a watch in MediaLog: parse the file name, find the show's ids, send, and
/// keep what can't be settled automatically for the person. Events are queued in a file first, so nothing is lost if
/// the PC restarts or the internet is down. See docs/medialog-sync-design.md.
/// </summary>
public sealed class WatchSyncService
{
    private const int MaxRecent = 100;
    private readonly MediaLogStore _store;
    private readonly IMediaLogClient _mediaLog;
    private readonly ITvMaze _tvMaze;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private volatile bool _needsReconnect;

    public WatchSyncService(MediaLogStore store, IMediaLogClient mediaLog, ITvMaze tvMaze, TimeProvider? clock = null, Action<string>? log = null)
    {
        _store = store;
        _mediaLog = mediaLog;
        _tvMaze = tvMaze;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (_ => { });
    }

    /// <summary>Raised (on any thread) whenever counts or the needs-confirmation list change.</summary>
    public event Action? Changed;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // Connection state ---------------------------------------------------------------

    public bool IsConnected => _store.GetToken() is not null;

    /// <summary>Called after a new token was saved (or cleared).</summary>
    public void ConnectionChanged()
    {
        _needsReconnect = false;
        Wake();
        Changed?.Invoke();
    }

    public SyncSnapshot Snapshot()
    {
        var connected = IsConnected;
        return _store.Read(s =>
        {
            var today = Now.ToLocalTime().Date;
            var confirm = s.Queue.Where(q => q.State == WatchState.NeedsConfirmation).Select(Clone).ToList();
            return new SyncSnapshot(connected, _needsReconnect, s.Label, s.Address, s.Username, s.AccountId,
                s.Queue.Count(q => q.State == WatchState.Pending), confirm.Count,
                s.Recent.Count(r => r.SentAtUtc.ToLocalTime().Date == today), confirm);
        });
    }

    // Events from the TV --------------------------------------------------------------

    public EnqueueResult Enqueue(string mediaId, string filePath, DateTime watchedAtUtc, string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || eventId.Length > 100 || string.IsNullOrWhiteSpace(mediaId)) return EnqueueResult.Invalid;
        if (!IsConnected) return EnqueueResult.Ignored;

        var parsed = TitleParser.Parse(filePath);
        var now = Now;
        var result = _store.Update(s =>
        {
            if (s.Queue.Any(q => q.EventId == eventId) || s.Recent.Any(r => r.EventId == eventId)) return EnqueueResult.Accepted;
            var item = new QueuedWatch
            {
                EventId = eventId, MediaId = mediaId, WatchedAtUtc = watchedAtUtc.ToUniversalTime(),
                Kind = parsed.Kind, Title = parsed.Title, Year = parsed.Year, Season = parsed.Season, Episode = parsed.Episode,
                NextAttemptUtc = now,
            };
            if (parsed.Kind == WatchKind.Unknown)
            {
                item.State = WatchState.NeedsConfirmation;
                item.Reason = "CastBox couldn't tell what this file is from its name.";
            }
            else if (s.Mappings.TryGetValue(MappingKey(item)!, out var mapping) && mapping.DontSync) return EnqueueResult.Ignored;
            s.Queue.Add(item);
            return EnqueueResult.Accepted;
        });
        if (result == EnqueueResult.Accepted) { Wake(); Changed?.Invoke(); }
        return result;
    }

    // The worker ------------------------------------------------------------------------

    /// <summary>Sends due items until the queue is empty, MediaLog can't be reached, or the token stops working.</summary>
    public async Task ProcessDueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_needsReconnect)
        {
            var token = _store.GetToken();
            if (token is null) return;
            var (item, discovery) = _store.Read(s =>
            {
                var due = s.Queue.Where(q => q.State == WatchState.Pending && q.NextAttemptUtc <= Now).OrderBy(q => q.WatchedAtUtc).FirstOrDefault();
                var d = s.FunctionsUrl is { } f && s.AnonKey is { } a ? new Discovery(f, a) : null;
                return (due is null ? null : Clone(due), d);
            });
            if (item is null) return;
            if (discovery is null) { _needsReconnect = true; Changed?.Invoke(); return; }
            if (!await SendAsync(item, token, discovery, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>Runs until cancelled: processes the queue now, whenever something changes, and every 30 seconds.</summary>
    /// <summary>Learns (or re-learns) which MediaLog account this connection belongs to, e.g. after an update or a username change. Failures are ignored; the sync path notices a revoked token itself.</summary>
    public async Task RefreshAccountAsync(CancellationToken ct)
    {
        var token = _store.GetToken();
        var discovery = _store.Read(s => s.FunctionsUrl is { } f && s.AnonKey is { } a ? new Discovery(f, a) : null);
        if (token is null || discovery is null) return;
        WhoAmI who;
        try { who = await _mediaLog.WhoAmIAsync(discovery, token, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is MediaLogException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return; }
        var changed = _store.Update(s =>
        {
            if (s.TokenProtected is null || (s.Username == who.Username && s.AccountId == who.AccountId && s.Label == who.Label)) return false;
            s.Label = who.Label; s.Username = who.Username; s.AccountId = who.AccountId;
            return true;
        });
        if (changed) Changed?.Invoke();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await RefreshAccountAsync(ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessDueAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log("MediaLog sync error: " + ex.Message); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }

    /// <returns>false when processing should stop for now (transient failure or reconnect needed).</returns>
    private async Task<bool> SendAsync(QueuedWatch item, string token, Discovery discovery, CancellationToken ct)
    {
        var key = MappingKey(item);
        var mapping = key is not null ? _store.Read(s => s.Mappings.GetValueOrDefault(key)) : null;
        if (mapping is { DontSync: true }) { Remove(item.EventId); return true; }

        var ids = item.ChosenIds ?? (mapping?.Ids.Any == true ? mapping.Ids : null);
        if (ids is null && item.Kind == WatchKind.Episode) ids = await ShowIdsAsync(item, ct).ConfigureAwait(false);

        var request = new WatchRequest(item.EventId, item.Kind, item.WatchedAtUtc, item.Title, item.Year, item.Season, item.Episode, ids);
        var result = await _mediaLog.LogWatchAsync(discovery, token, request, ct).ConfigureAwait(false);

        switch (result.Outcome)
        {
            case LogOutcome.Logged or LogOutcome.AlreadyLogged:
                _store.Update(s =>
                {
                    s.Queue.RemoveAll(q => q.EventId == item.EventId);
                    s.Recent.Add(new SentWatch { EventId = item.EventId, Description = item.Description, SentAtUtc = Now, Outcome = result.Outcome == LogOutcome.Logged ? "logged" : "already watched" });
                    if (s.Recent.Count > MaxRecent) s.Recent.RemoveRange(0, s.Recent.Count - MaxRecent);
                });
                _log($"MediaLog: {item.Description} → {(result.Outcome == LogOutcome.Logged ? "marked watched" : "already watched")}");
                Changed?.Invoke();
                return true;

            case LogOutcome.NeedsConfirmation or LogOutcome.NotFound or LogOutcome.EpisodeNotFound or LogOutcome.Rejected:
                var reason = result.Outcome switch
                {
                    LogOutcome.NeedsConfirmation => result.Candidates is { Count: > 0 } ? "More than one title could match." : "MediaLog couldn't be sure which title this is.",
                    LogOutcome.EpisodeNotFound => "MediaLog doesn't have this episode (yet).",
                    LogOutcome.Rejected => result.Message ?? "MediaLog rejected this event.",
                    _ => "MediaLog couldn't find this title.",
                };
                _store.Update(s =>
                {
                    var q = s.Queue.FirstOrDefault(x => x.EventId == item.EventId);
                    if (q is null) return;
                    q.State = WatchState.NeedsConfirmation; q.Reason = reason; q.LastError = null;
                    q.Candidates = result.Candidates?.ToList() ?? new List<Candidate>();
                });
                _log($"MediaLog: {item.Description} needs confirmation ({reason})");
                Changed?.Invoke();
                return true;

            case LogOutcome.Unauthorized:
                _needsReconnect = true;
                _log("MediaLog no longer accepts this connection; reconnect to continue.");
                Changed?.Invoke();
                return false;

            default: // rate limited, or MediaLog/network unavailable: try again later, keeping order
                _store.Update(s =>
                {
                    var q = s.Queue.FirstOrDefault(x => x.EventId == item.EventId);
                    if (q is null) return;
                    q.Attempts++;
                    q.LastError = result.Message;
                    q.NextAttemptUtc = Now + Backoff(q.Attempts);
                });
                _log($"MediaLog: couldn't send {item.Description} ({result.Message ?? result.Outcome.ToString()}); will retry.");
                Changed?.Invoke();
                return false;
        }
    }

    /// <summary>1 minute, doubling each time, at most 1 hour.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts - 1, 0, 10))));

    private async Task<MediaIds?> ShowIdsAsync(QueuedWatch item, CancellationToken ct)
    {
        var key = TitleParser.Normalize(item.Title) + (item.Year is { } y ? ":" + y : "");
        var cached = _store.Read(s => s.ShowIds.GetValueOrDefault(key));
        if (cached is not null) return cached;
        try
        {
            var found = await _tvMaze.FindShowAsync(item.Title, item.Year, ct).ConfigureAwait(false);
            if (found is not null) _store.Update(s => s.ShowIds[key] = found);
            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // TVmaze being down only costs us the exact-id shortcut: MediaLog can still match by title.
            _log("TVmaze lookup failed: " + ex.Message);
            return null;
        }
    }

    // The person's choices ------------------------------------------------------------------

    /// <summary>The person picked one of MediaLog's candidates: remember it for this show or movie and retry everything that waits on it.</summary>
    public void Confirm(string eventId, Candidate candidate)
    {
        _store.Update(s =>
        {
            var item = s.Queue.FirstOrDefault(q => q.EventId == eventId);
            if (item is null) return;
            var ids = new MediaIds { Tmdb = candidate.ExternalId };
            var key = MappingKey(item);
            if (key is not null)
                s.Mappings[key] = new Mapping { Ids = ids, Display = candidate.Year is { } y ? $"{candidate.Title} ({y})" : candidate.Title };
            foreach (var q in s.Queue.Where(q => q.EventId == eventId || (key is not null && q.State == WatchState.NeedsConfirmation && MappingKey(q) == key)))
                Retry(q, ids);
        });
        Wake();
        Changed?.Invoke();
    }

    /// <summary>For files CastBox couldn't read a title from: the person says what it is.</summary>
    public void SetManual(string eventId, WatchKind kind, string title, int? year, int? season, int? episode)
    {
        if (kind == WatchKind.Unknown || string.IsNullOrWhiteSpace(title)) return;
        if (kind == WatchKind.Episode && (season is null || episode is null)) return;
        _store.Update(s =>
        {
            var item = s.Queue.FirstOrDefault(q => q.EventId == eventId);
            if (item is null) return;
            item.Kind = kind; item.Title = title.Trim(); item.Year = year;
            item.Season = kind == WatchKind.Episode ? season : null; item.Episode = kind == WatchKind.Episode ? episode : null;
            Retry(item, null);
        });
        Wake();
        Changed?.Invoke();
    }

    /// <summary>Drops this one watch; later episodes of the show are still synced.</summary>
    public void DontSyncThis(string eventId)
    {
        Remove(eventId);
        Changed?.Invoke();
    }

    /// <summary>Never sync this show or movie: drops what waits and ignores future watches of it.</summary>
    public void DontSyncTitle(string eventId)
    {
        _store.Update(s =>
        {
            var item = s.Queue.FirstOrDefault(q => q.EventId == eventId);
            var key = item is null ? null : MappingKey(item);
            if (key is not null)
            {
                s.Mappings[key] = new Mapping { DontSync = true, Display = item!.Title };
                s.Queue.RemoveAll(q => MappingKey(q) == key);
            }
            else s.Queue.RemoveAll(q => q.EventId == eventId);
        });
        Changed?.Invoke();
    }

    /// <summary>Forgets every saved choice and cached show lookup (after a wrong confirmation).</summary>
    public void ForgetSavedChoices()
    {
        _store.Update(s => { s.Mappings.Clear(); s.ShowIds.Clear(); });
        Changed?.Invoke();
    }

    /// <summary>Forgets the connection and what was waiting for it.</summary>
    public void Disconnected()
    {
        _store.Update(s => { s.TokenProtected = null; s.Label = null; s.Username = null; s.AccountId = null; s.FunctionsUrl = null; s.AnonKey = null; s.Queue.Clear(); });
        _needsReconnect = false;
        Changed?.Invoke();
    }

    // Helpers ---------------------------------------------------------------------------------

    private void Retry(QueuedWatch item, MediaIds? ids)
    {
        item.State = WatchState.Pending; item.Reason = null; item.Candidates = new List<Candidate>();
        item.ChosenIds = ids; item.Attempts = 0; item.LastError = null; item.NextAttemptUtc = Now;
    }

    private void Remove(string eventId) => _store.Update(s => s.Queue.RemoveAll(q => q.EventId == eventId));

    /// <summary>Which show or movie a watch belongs to, for remembering the person's choice; null when unknown.</summary>
    public static string? MappingKey(QueuedWatch item)
    {
        if (item.Kind == WatchKind.Unknown || string.IsNullOrWhiteSpace(item.Title)) return null;
        var title = TitleParser.Normalize(item.Title);
        return item.Kind == WatchKind.Episode ? "tv:" + title : "movie:" + title + ":" + item.Year;
    }

    private static QueuedWatch Clone(QueuedWatch q) => new()
    {
        EventId = q.EventId, MediaId = q.MediaId, WatchedAtUtc = q.WatchedAtUtc, Kind = q.Kind, Title = q.Title, Year = q.Year,
        Season = q.Season, Episode = q.Episode, State = q.State, Attempts = q.Attempts, NextAttemptUtc = q.NextAttemptUtc,
        LastError = q.LastError, Reason = q.Reason, ChosenIds = q.ChosenIds,
        Candidates = q.Candidates.Select(c => new Candidate { Provider = c.Provider, ExternalId = c.ExternalId, Title = c.Title, Year = c.Year, Poster = c.Poster }).ToList(),
    };
}

/// <summary>The pairing flow behind "Connect to MediaLog": discover, show a code, wait for approval, keep the token.</summary>
public sealed class MediaLogConnector
{
    private readonly MediaLogStore _store;
    private readonly IMediaLogClient _client;
    private readonly WatchSyncService _service;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MediaLogConnector(MediaLogStore store, IMediaLogClient client, WatchSyncService service, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _store = store;
        _client = client;
        _service = service;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>Checks the address is a MediaLog site and starts a pairing. Show <see cref="PairStart.UserCode"/> and open <c>address/connect/code</c>.</summary>
    public async Task<(Discovery Discovery, PairStart Pairing, string ConnectUrl)> BeginAsync(string address, string label, CancellationToken ct)
    {
        var normalized = HttpMediaLogClient.NormalizeAddress(address);
        var discovery = await _client.DiscoverAsync(normalized, ct).ConfigureAwait(false);
        var pairing = await _client.PairStartAsync(discovery, label, ct).ConfigureAwait(false);
        _store.Update(s => s.Address = normalized);
        return (discovery, pairing, $"{normalized}/connect/{pairing.UserCode}");
    }

    /// <summary>Polls until the person approves, refuses or it expires. On approval the token is saved (encrypted) and syncing starts.</summary>
    public async Task<PairStatus> WaitAsync(Discovery discovery, PairStart pairing, CancellationToken ct)
    {
        var deadline = TimeSpan.FromSeconds(pairing.ExpiresInSeconds);
        var waited = TimeSpan.Zero;
        while (waited <= deadline)
        {
            var poll = await _client.PairPollAsync(discovery, pairing.DeviceCode, ct).ConfigureAwait(false);
            if (poll.Status == PairStatus.Approved && poll.Token is not null)
            {
                var who = await SafeWhoAmIAsync(discovery, poll.Token, ct).ConfigureAwait(false);
                _store.Update(s => { s.FunctionsUrl = discovery.FunctionsUrl; s.AnonKey = discovery.AnonKey; s.Label = who.Label; s.Username = who.Username; s.AccountId = who.AccountId; });
                _store.SetToken(poll.Token);
                _service.ConnectionChanged();
                return PairStatus.Approved;
            }
            if (poll.Status is PairStatus.Denied or PairStatus.Expired) return poll.Status;

            var wait = TimeSpan.FromSeconds(pairing.IntervalSeconds);
            await _delay(wait, ct).ConfigureAwait(false);
            waited += wait;
        }
        return PairStatus.Expired;
    }

    private async Task<WhoAmI> SafeWhoAmIAsync(Discovery discovery, string token, CancellationToken ct)
    {
        try { return await _client.WhoAmIAsync(discovery, token, ct).ConfigureAwait(false); }
        catch (MediaLogException) { return new WhoAmI("CastBox", null, null); }
    }

    /// <summary>Tells MediaLog to revoke the token (best effort), then forgets it here.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        var token = _store.GetToken();
        var discovery = _store.Read(s => s.FunctionsUrl is { } f && s.AnonKey is { } a ? new Discovery(f, a) : null);
        string? warning = null;
        if (token is not null && discovery is not null)
        {
            try { await _client.RevokeAsync(discovery, token, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is MediaLogException or HttpRequestException or TaskCanceledException) { warning = ex.Message; }
        }
        _service.Disconnected();
        if (warning is not null) throw new MediaLogException(warning);
    }
}
