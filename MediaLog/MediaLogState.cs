using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CastBox.MediaLog;

/// <summary>External ids MediaLog can match a show or movie by exactly.</summary>
public sealed class MediaIds
{
    public string? Imdb { get; set; }
    public string? Tvdb { get; set; }
    public string? Tmdb { get; set; }
    [JsonIgnore] public bool Any => Imdb is not null || Tvdb is not null || Tmdb is not null;
}

/// <summary>A confirmed choice for a show or movie: use these ids every time, or never sync it.</summary>
public sealed class Mapping
{
    public MediaIds Ids { get; set; } = new();
    public bool DontSync { get; set; }
    /// <summary>What the person picked, for showing in the app (not used for matching).</summary>
    public string? Display { get; set; }
}

/// <summary>One of up to five titles MediaLog offered when it couldn't be sure.</summary>
public sealed class Candidate
{
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? Poster { get; set; }
}

public enum WatchState { Pending, NeedsConfirmation }

/// <summary>A watch waiting to be sent to MediaLog, or waiting for the person to settle what it is.</summary>
public sealed class QueuedWatch
{
    /// <summary>Our idempotency key; also MediaLog's client_event_id.</summary>
    public string EventId { get; set; } = "";
    public string MediaId { get; set; } = "";
    public DateTime WatchedAtUtc { get; set; }
    public WatchKind Kind { get; set; }
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public WatchState State { get; set; } = WatchState.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
    /// <summary>Why it needs the person: ambiguous, not found, or not recognisable from the file name.</summary>
    public string? Reason { get; set; }
    public List<Candidate> Candidates { get; set; } = new();
    /// <summary>Ids chosen by the person for this item (after confirming a candidate).</summary>
    public MediaIds? ChosenIds { get; set; }

    [JsonIgnore]
    public string Description => Kind == WatchKind.Episode && Season is not null && Episode is not null
        ? $"{Title} · S{Season}E{Episode}"
        : Kind == WatchKind.Movie && Year is not null ? $"{Title} ({Year})" : string.IsNullOrWhiteSpace(Title) ? "Unrecognised file" : Title;
}

/// <summary>A watch that reached MediaLog, kept briefly so the app can say what was sent today.</summary>
public sealed class SentWatch
{
    public string EventId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class MediaLogState
{
    /// <summary>The person's MediaLog site address, e.g. https://thinglog.netlify.app</summary>
    public string? Address { get; set; }
    public string? FunctionsUrl { get; set; }
    public string? AnonKey { get; set; }
    /// <summary>The app token, encrypted for the current Windows user (DPAPI). Never stored in plain text.</summary>
    public string? TokenProtected { get; set; }
    /// <summary>What MediaLog calls this connection, e.g. "CastBox on COma".</summary>
    public string? Label { get; set; }
    /// <summary>The MediaLog username of the connected account (null if the person hasn't chosen one) and its opaque id.</summary>
    public string? Username { get; set; }
    public string? AccountId { get; set; }
    public Dictionary<string, Mapping> Mappings { get; set; } = new();
    /// <summary>TVmaze answers by normalised show title, so each show is looked up once.</summary>
    public Dictionary<string, MediaIds> ShowIds { get; set; } = new();
    public List<QueuedWatch> Queue { get; set; } = new();
    public List<SentWatch> Recent { get; set; } = new();
}

/// <summary>Encrypts the token at rest.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string? Unprotect(string protectedText);
}

/// <summary>Windows DPAPI, scoped to the signed-in user: only that user on this PC can decrypt it.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CastBox.MediaLog.token.v1");

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedText)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}

/// <summary>
/// The MediaLog settings, mappings and event queue in one JSON file (<c>medialog.json</c>), written atomically
/// (temp file, then replace) so a crash or power cut never leaves half a file. All access is serialised.
/// </summary>
public sealed class MediaLogStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _lock = new();
    private MediaLogState _state;

    public MediaLogStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        _state = Load();
    }

    /// <summary>Runs <paramref name="change"/> on the state under the lock and saves it; returns its result.</summary>
    public T Update<T>(Func<MediaLogState, T> change)
    {
        lock (_lock)
        {
            var result = change(_state);
            Save();
            return result;
        }
    }

    public void Update(Action<MediaLogState> change) => Update<object?>(state => { change(state); return null; });

    /// <summary>Reads the state under the lock without saving.</summary>
    public T Read<T>(Func<MediaLogState, T> read) { lock (_lock) return read(_state); }

    public string? GetToken() => Read(s => s.TokenProtected is null ? null : _protector.Unprotect(s.TokenProtected));

    public void SetToken(string? token) => Update(s => s.TokenProtected = token is null ? null : _protector.Protect(token));

    private MediaLogState Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<MediaLogState>(File.ReadAllText(_path), Json) ?? new MediaLogState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged file must not stop the app; keep a copy for diagnosis and start clean.
            try { File.Copy(_path, _path + ".damaged", overwrite: true); } catch { }
        }
        return new MediaLogState();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
        // Windows antivirus and the search indexer briefly hold a file they just saw written, which makes the swap
        // fail with "unable to remove the file to be replaced"; it clears within milliseconds, so retry.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
                return;
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(15 * attempt);
            }
        }
    }
}
