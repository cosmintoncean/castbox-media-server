using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CastBox.MediaLog;

/// <summary>Finds a show's IMDb and TheTVDB ids so MediaLog can match it exactly.</summary>
public interface ITvMaze
{
    /// <summary>
    /// The ids for the show whose name equals <paramref name="title"/> (punctuation and case ignored; its premiere
    /// year within one of <paramref name="year"/> when given), or null if there is no such show.
    /// Throws <see cref="HttpRequestException"/> when TVmaze can't be reached, so callers don't treat that as "no such show".
    /// </summary>
    Task<MediaIds?> FindShowAsync(string title, int? year, CancellationToken ct);
}

/// <summary>
/// TVmaze (api.tvmaze.com): free, no key. Only the cleaned show title is sent. Its data is licensed CC BY-SA;
/// NestCast uses the ids only and shows none of it.
/// </summary>
public sealed class TvMazeClient : ITvMaze
{
    private readonly HttpClient _http;

    public TvMazeClient(HttpClient http) => _http = http;

    public async Task<MediaIds?> FindShowAsync(string title, int? year, CancellationToken ct)
    {
        var url = "https://api.tvmaze.com/singlesearch/shows?q=" + Uri.EscapeDataString(title);
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        JsonNode? node;
        try { node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); } catch (JsonException) { return null; }
        if (node is not JsonObject show) return null;

        if (TitleParser.Normalize((string?)show["name"] ?? "") != TitleParser.Normalize(title)) return null;
        if (year is not null && int.TryParse(((string?)show["premiered"])?[..Math.Min(4, ((string?)show["premiered"])!.Length)], out var premiered) && Math.Abs(premiered - year.Value) > 1) return null;

        var externals = show["externals"] as JsonObject;
        var ids = new MediaIds { Imdb = (string?)externals?["imdb"], Tvdb = externals?["thetvdb"]?.ToString() is { Length: > 0 } t && t != "null" ? t : null };
        return ids.Any ? ids : null;
    }
}
