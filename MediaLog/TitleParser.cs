using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CastBox.MediaLog;

public enum WatchKind { Unknown, Episode, Movie }

/// <summary>What a media file's path says it is. <see cref="Year"/> is the movie's year, or a show's year when the name carries one.</summary>
public sealed record ParsedMedia(WatchKind Kind, string Title, int? Year, int? Season, int? Episode);

/// <summary>
/// Works out a show/movie title, year and season/episode from a scene-style file name such as
/// <c>Pam.and.Tommy.S01E01.Drilling.and.Pounding.1080p.DSNP.WEB-DL.DD+5.1.H.264-playWEB.mkv</c>, falling back to the
/// folders above it. The cleaning rules match the Android app's folder-name cleaning (PosterRepo.kt).
/// See docs/medialog-sync-design.md.
/// </summary>
public static class TitleParser
{
    // Anything that ends the title: season/episode markers, a year, and the usual release tags.
    private static readonly Regex Tokens = new(
        @"(?<![A-Za-z0-9])(S\d{1,2}(?:E\d{1,3}(?:[-E]\d{1,3})*)?|\d{1,2}x\d{2,3}|(?:19|20)\d{2}|480p|576p|720p|1080p|2160p|4k|uhd|web[- ]?dl|webrip|bluray|brrip|bdrip|hdtv|dvdrip|x26[45]|h[ .]?26[45]|hevc|dd\+?\d|aac|amzn|dsnp|nf|hmax)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EpisodeMarker = new(
        @"(?<![A-Za-z0-9])(?:S(?<s>\d{1,2})E(?<e>\d{1,3})(?:[-E]\d{1,3})*|(?<s2>\d{1,2})x(?<e2>\d{2,3}))(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Year = new(@"(?<![A-Za-z0-9])((?:19|20)\d{2})(?![A-Za-z0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Describes the file at <paramref name="filePath"/> (only its names are read, never the file).</summary>
    public static ParsedMedia Parse(string filePath)
    {
        var file = Path.GetFileNameWithoutExtension(filePath);
        var folders = new List<string>();
        for (var dir = Path.GetDirectoryName(filePath); !string.IsNullOrEmpty(dir) && folders.Count < 2; dir = Path.GetDirectoryName(dir))
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(name)) folders.Add(name);
        }

        var spaced = Spaced(file);
        var episode = EpisodeMarker.Match(spaced);
        if (episode.Success)
        {
            var season = int.Parse(episode.Groups["s"].Success ? episode.Groups["s"].Value : episode.Groups["s2"].Value, CultureInfo.InvariantCulture);
            var number = int.Parse(episode.Groups["e"].Success ? episode.Groups["e"].Value : episode.Groups["e2"].Value, CultureInfo.InvariantCulture);
            // The title is in the file name; a bare "S01E01.mkv" borrows it from the show/season folder.
            var (title, year) = TitleAndYear(spaced);
            foreach (var folder in folders) { if (title.Length >= 2) break; (title, year) = TitleAndYear(Spaced(folder)); }
            return title.Length >= 2
                ? new ParsedMedia(WatchKind.Episode, title, year, season, number)
                : new ParsedMedia(WatchKind.Unknown, "", null, season, number);
        }

        var (movieTitle, movieYear) = TitleAndYear(spaced);
        if (movieTitle.Length < 2)
            foreach (var folder in folders) { (movieTitle, movieYear) = TitleAndYear(Spaced(folder)); if (movieTitle.Length >= 2) break; }
        if (movieTitle.Length >= 2 && movieYear is not null) return new ParsedMedia(WatchKind.Movie, movieTitle, movieYear, null, null);
        return new ParsedMedia(WatchKind.Unknown, movieTitle, movieYear, null, null);
    }

    /// <summary>"Pam &amp; Tommy" and "pam and tommy" are the same: case, accents, punctuation and ampersands ignored.</summary>
    public static string Normalize(string title)
    {
        var decomposed = title.ToLowerInvariant().Replace("&", "and").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Spaced(string name) => name.Replace('.', ' ').Replace('_', ' ');

    /// <summary>The text before the first tag, and the first year found before any season/episode marker.</summary>
    private static (string Title, int? Year) TitleAndYear(string spaced)
    {
        var first = Tokens.Match(spaced);
        var cut = first.Success ? first.Index : spaced.Length;
        var title = Spaces.Replace(spaced[..cut].Trim(' ', '-', '(', '[', '_'), " ");
        var yearMatch = Year.Match(spaced);
        int? year = yearMatch.Success && (!EpisodeMarker.IsMatch(spaced) || yearMatch.Index < EpisodeMarker.Match(spaced).Index)
            ? int.Parse(yearMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        return (title, year);
    }
}
