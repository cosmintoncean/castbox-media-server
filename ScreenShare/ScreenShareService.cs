using System.Security.Cryptography;

namespace CastBox.ScreenShare;

/// <summary>One monitor of this PC, numbered from 0 in the order Windows lists them.</summary>
public sealed record ScreenInfo(int Index, string Name, string DeviceName, int Width, int Height, bool Primary);

/// <summary>A TV that has talked to this PC recently.</summary>
public sealed record TvInfo(string Id, string Name, DateTime LastSeenUtc, string? Address = null);

public enum ShareStatus { Pending, Approved, Denied, Expired }

public enum ConsentChoice { Deny, AllowOnce, AlwaysAllow }

/// <summary>A TV asking to watch a screen. Approved requests carry the token that opens the stream.</summary>
public sealed class ShareRequest
{
    public required string Id { get; init; }
    public required string TvId { get; init; }
    public required string TvName { get; init; }
    public required int Screen { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public ShareStatus Status { get; internal set; } = ShareStatus.Pending;
    public string? Token { get; internal set; }
}

/// <summary>A screen being watched right now. Cancelling <see cref="Cancel"/> ends the stream.</summary>
public sealed class ShareSession
{
    private readonly CancellationTokenSource _cts = new();
    public required string Id { get; init; }
    public required string TvId { get; init; }
    public required string TvName { get; init; }
    public required int Screen { get; init; }
    public required DateTime StartedUtc { get; init; }
    public CancellationToken Ended => _cts.Token;
    internal void Cancel() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
}

/// <summary>A screen the PC is sending to a TV whose app is open (the PC's "Share screen" button).</summary>
public sealed record PushOffer(int Screen, string Token);

/// <summary>
/// Who may watch which screen. A screen shows everything on the PC, so a TV gets a token only after the person at the PC
/// said yes (once, or for good) or chose to send the screen to it; the stream refuses everything else.
/// </summary>
public sealed class ScreenShareService
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan TvRecentWindow = TimeSpan.FromSeconds(45);

    private sealed record Grant(string TvId, string TvName, int Screen, DateTime ExpiresUtc);

    private readonly object _lock = new();
    private readonly Func<IReadOnlyList<ScreenInfo>> _screens;
    private readonly TimeProvider _clock;
    private readonly Action<IReadOnlyDictionary<string, string>> _persistAllowed;
    private readonly Dictionary<string, string> _allowed;                       // tvId -> name, kept in settings
    private readonly Dictionary<string, ShareRequest> _requests = new();
    private readonly Dictionary<string, Grant> _grants = new();                // token -> who and which screen
    private readonly Dictionary<string, TvInfo> _tvs = new();
    private readonly Dictionary<string, PushOffer> _pushes = new();            // tvId -> waiting offer
    private readonly List<ShareSession> _sessions = new();

    public ScreenShareService(Func<IReadOnlyList<ScreenInfo>> screens, IReadOnlyDictionary<string, string>? allowedTvs = null,
        Action<IReadOnlyDictionary<string, string>>? persistAllowed = null, TimeProvider? clock = null)
    {
        _screens = screens;
        _allowed = allowedTvs is null ? new() : new(allowedTvs);
        _persistAllowed = persistAllowed ?? (_ => { });
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Replaces the list of TVs allowed for good (when the settings are read at start). Not saved again.</summary>
    public void LoadAllowed(IReadOnlyDictionary<string, string>? allowed)
    {
        lock (_lock) { _allowed.Clear(); if (allowed is not null) foreach (var kv in allowed) _allowed[kv.Key] = kv.Value; }
    }

    /// <summary>Raised (on any thread) when a TV is waiting for a yes or no.</summary>
    public event Action<ShareRequest>? ApprovalNeeded;
    /// <summary>Raised when requests, sessions or the known TVs changed, so the window can refresh.</summary>
    public event Action? Changed;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<ScreenInfo> Screens => _screens();

    public ScreenInfo? GetScreen(int index) => _screens().FirstOrDefault(s => s.Index == index);

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // TVs --------------------------------------------------------------------------------------------------------

    /// <summary>Notes that a TV just made contact, so the PC can offer it as a place to send a screen.</summary>
    public void Touch(string tvId, string tvName, string? address = null)
    {
        if (string.IsNullOrWhiteSpace(tvId)) return;
        var name = string.IsNullOrWhiteSpace(tvName) ? "TV" : tvName.Trim();
        bool isNew;
        lock (_lock)
        {
            _tvs.TryGetValue(tvId, out var known);
            isNew = known is null || known.Name != name || Now - known.LastSeenUtc > TvRecentWindow || (address is not null && address != known.Address);
            _tvs[tvId] = new TvInfo(tvId, name, Now, address ?? known?.Address);
        }
        if (isNew) Changed?.Invoke();
    }

    /// <summary>TVs seen in the last 45 seconds (a TV with the app open asks every few seconds).</summary>
    public IReadOnlyList<TvInfo> RecentTvs()
    {
        lock (_lock) return _tvs.Values.Where(t => Now - t.LastSeenUtc <= TvRecentWindow).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyDictionary<string, string> AllowedTvs { get { lock (_lock) return new Dictionary<string, string>(_allowed); } }

    /// <summary>Forgets a TV's permission: it has to ask again, and any stream it has open ends.</summary>
    public void Forget(string tvId)
    {
        List<ShareSession> ending;
        lock (_lock)
        {
            _allowed.Remove(tvId);
            foreach (var token in _grants.Where(g => g.Value.TvId == tvId).Select(g => g.Key).ToList()) _grants.Remove(token);
            ending = _sessions.Where(s => s.TvId == tvId).ToList();
            Persist();
        }
        // Cancelling runs the stream's clean-up, which takes the session off the list: so never while holding the lock or iterating it.
        foreach (var s in ending) s.Cancel();
        Changed?.Invoke();
    }

    private void Persist() => _persistAllowed(new Dictionary<string, string>(_allowed));

    // A TV asks (TV button) ----------------------------------------------------------------------------------------

    /// <summary>
    /// A TV asks to watch <paramref name="screen"/>. A TV the person has allowed for good is approved straight away; any
    /// other waits for <see cref="Resolve"/> (the window shows a question). Returns null if there is no such screen.
    /// </summary>
    public ShareRequest? Request(string tvId, string tvName, int screen)
    {
        if (string.IsNullOrWhiteSpace(tvId) || GetScreen(screen) is null) return null;
        Touch(tvId, tvName);
        var name = _tvs[tvId].Name;
        ShareRequest request;
        bool ask;
        lock (_lock)
        {
            Expire();
            request = new ShareRequest { Id = NewToken(), TvId = tvId, TvName = name, Screen = screen, CreatedUtc = Now };
            if (_allowed.ContainsKey(tvId)) { Approve(request); ask = false; }
            else
            {
                // One question per TV at a time: asking again replaces the earlier, still waiting, request.
                foreach (var old in _requests.Values.Where(r => r.TvId == tvId && r.Status == ShareStatus.Pending)) old.Status = ShareStatus.Expired;
                ask = true;
            }
            _requests[request.Id] = request;
        }
        Changed?.Invoke();
        if (ask) ApprovalNeeded?.Invoke(request);
        return request;
    }

    private void Approve(ShareRequest request)
    {
        request.Status = ShareStatus.Approved;
        request.Token = NewToken();
        _grants[request.Token] = new Grant(request.TvId, request.TvName, request.Screen, Now + TokenLifetime);
    }

    /// <summary>The answer to a request, for the TV that is polling for it (null if unknown).</summary>
    public ShareRequest? Poll(string requestId)
    {
        lock (_lock)
        {
            Expire();
            return _requests.GetValueOrDefault(requestId);
        }
    }

    /// <summary>Records what the person at the PC answered.</summary>
    public void Resolve(string requestId, ConsentChoice choice)
    {
        lock (_lock)
        {
            Expire();
            if (!_requests.TryGetValue(requestId, out var request) || request.Status != ShareStatus.Pending) return;
            if (choice == ConsentChoice.Deny) request.Status = ShareStatus.Denied;
            else
            {
                Approve(request);
                if (choice == ConsentChoice.AlwaysAllow) { _allowed[request.TvId] = request.TvName; Persist(); }
            }
        }
        Changed?.Invoke();
    }

    private void Expire()
    {
        foreach (var r in _requests.Values.Where(r => r.Status == ShareStatus.Pending && Now - r.CreatedUtc > RequestLifetime)) r.Status = ShareStatus.Expired;
        foreach (var id in _requests.Where(r => Now - r.Value.CreatedUtc > TimeSpan.FromMinutes(10)).Select(r => r.Key).ToList()) _requests.Remove(id);
        foreach (var token in _grants.Where(g => g.Value.ExpiresUtc < Now).Select(g => g.Key).ToList()) _grants.Remove(token);
    }

    /// <summary>Requests still waiting for an answer.</summary>
    public IReadOnlyList<ShareRequest> PendingRequests()
    {
        lock (_lock) { Expire(); return _requests.Values.Where(r => r.Status == ShareStatus.Pending).OrderBy(r => r.CreatedUtc).ToList(); }
    }

    // The PC sends a screen (PC button) ------------------------------------------------------------------------------

    /// <summary>Offers <paramref name="screen"/> to a TV. The TV's next check for offers picks it up. False if the screen is unknown.</summary>
    public bool Push(string tvId, int screen)
    {
        if (GetScreen(screen) is null) return false;
        lock (_lock)
        {
            if (!_tvs.TryGetValue(tvId, out var tv)) return false;
            var token = NewToken();
            _grants[token] = new Grant(tvId, tv.Name, screen, Now + TokenLifetime);
            _pushes[tvId] = new PushOffer(screen, token);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The offer waiting for a TV, if any (it is handed out once).</summary>
    public PushOffer? TakePush(string tvId)
    {
        lock (_lock) return _pushes.Remove(tvId, out var offer) ? offer : null;
    }

    // Streams ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a stream if <paramref name="token"/> was handed to a TV for exactly this screen and is still valid. The returned
    /// session must be closed with <see cref="Close"/> when the TV disconnects.
    /// </summary>
    public ShareSession? Open(string? token, int screen)
    {
        if (string.IsNullOrEmpty(token)) return null;
        ShareSession session;
        lock (_lock)
        {
            Expire();
            if (!_grants.TryGetValue(token, out var grant) || grant.Screen != screen || GetScreen(screen) is null) return null;
            session = new ShareSession { Id = NewToken(), TvId = grant.TvId, TvName = grant.TvName, Screen = screen, StartedUtc = Now };
            _sessions.Add(session);
        }
        Changed?.Invoke();
        return session;
    }

    /// <summary>
    /// A screen the PC itself is sending to a Chromecast (Google Cast): there is no TV app and no token, but it is listed
    /// under "Being watched now" and ends when <see cref="Stop"/> or <see cref="StopAll"/> is used.
    /// </summary>
    public ShareSession BeginExternal(string deviceName, int screen)
    {
        var session = new ShareSession { Id = NewToken(), TvId = "cast:" + deviceName, TvName = deviceName, Screen = screen, StartedUtc = Now };
        lock (_lock) _sessions.Add(session);
        Changed?.Invoke();
        return session;
    }

    public void Close(ShareSession session)
    {
        lock (_lock) _sessions.Remove(session);
        Changed?.Invoke();
    }

    public IReadOnlyList<ShareSession> Sessions { get { lock (_lock) return _sessions.ToList(); } }

    /// <summary>Ends one stream (the TV sees it stop).</summary>
    public void Stop(string sessionId)
    {
        List<ShareSession> ending;
        lock (_lock) ending = _sessions.Where(s => s.Id == sessionId).ToList();
        foreach (var s in ending) s.Cancel();
    }

    /// <summary>Ends every stream and cancels every permission that was given for one time only.</summary>
    public void StopAll()
    {
        List<ShareSession> ending;
        lock (_lock)
        {
            ending = _sessions.ToList();
            foreach (var token in _grants.Where(g => !_allowed.ContainsKey(g.Value.TvId)).Select(g => g.Key).ToList()) _grants.Remove(token);
        }
        foreach (var s in ending) s.Cancel();
        Changed?.Invoke();
    }
}
