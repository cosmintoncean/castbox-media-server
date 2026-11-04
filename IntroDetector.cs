using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CastBox;

/// <summary>Where a video's intro runs, taken from a chapter the file itself names (Intro, Opening, OP...).</summary>
public sealed record IntroRange(long StartMs, long EndMs, string Title);

/// <summary>Finds the intro in the chapter list that FFmpeg prints for a file (<c>-f ffmetadata</c>).</summary>
public static class IntroChapters
{
    private static readonly TimeSpan MaxStart = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinLength = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

    // Whole-title matches only: "Opening Scene" or "Introducing the villain" are story chapters, not an intro.
    private static readonly Regex IntroTitle = new(
        @"^(intro(duction)?|opening( (credits?|titles?|theme|sequence|song))?|op|main titles?|title sequence|theme song)( ?[\(\[]?\d{1,2}[\)\]]?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record Chapter(long StartMs, long EndMs, string Title);

    /// <summary>The first chapter titled like an intro that starts early and is of a believable length, or null.</summary>
    public static IntroRange? Find(string ffmetadata)
    {
        foreach (var chapter in Parse(ffmetadata))
        {
            var length = chapter.EndMs - chapter.StartMs;
            if (chapter.StartMs < 0 || chapter.StartMs > MaxStart.TotalMilliseconds) continue;
            if (length < MinLength.TotalMilliseconds || length > MaxLength.TotalMilliseconds) continue;
            if (!IntroTitle.IsMatch(Normalize(chapter.Title))) continue;
            return new IntroRange(chapter.StartMs, chapter.EndMs, chapter.Title);
        }
        return null;
    }

    private static string Normalize(string title) => Regex.Replace(title, @"\s+", " ").Trim(' ', '-', '_', '.', ':');

    private static IEnumerable<Chapter> Parse(string text)
    {
        long num = 1, den = 1000, start = 0, end = 0;
        string title = "";
        var inChapter = false;
        bool Complete() => inChapter && den > 0 && end > start;
        Chapter Build() => new(ToMs(start, num, den), ToMs(end, num, den), title);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('['))
            {
                if (Complete()) yield return Build();
                inChapter = line.Equals("[CHAPTER]", StringComparison.OrdinalIgnoreCase);
                num = 1; den = 1000; start = 0; end = 0; title = "";
                continue;
            }
            if (!inChapter) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToUpperInvariant();
            var value = line[(eq + 1)..];
            switch (key)
            {
                case "TIMEBASE":
                    var parts = value.Split('/');
                    if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var d)) { num = n; den = d; }
                    else den = 0;
                    break;
                case "START": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start); break;
                case "END": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out end); break;
                case "TITLE": title = value; break;
            }
        }
        if (Complete()) yield return Build();
    }

    private static long ToMs(long ticks, long num, long den) => (long)Math.Round(ticks * 1000.0 * num / den);
}

/// <summary>
/// Looks up intros with FFmpeg and remembers the answers. Reading chapters only opens the file's header, but it is a new
/// process per file, so at most two run at once and each file is asked about once until it changes.
/// </summary>
public sealed class IntroProbe
{
    private readonly Func<string, CancellationToken, Task<string?>> _readMetadata;
    private readonly ConcurrentDictionary<string, IntroRange?> _known = new();
    private readonly SemaphoreSlim _gate = new(2);

    /// <param name="readMetadata">Returns FFmpeg's metadata dump for a file, or null when it could not be read.</param>
    public IntroProbe(Func<string, CancellationToken, Task<string?>> readMetadata) => _readMetadata = readMetadata;

    public static IntroProbe ForFfmpeg(string ffmpegPath) => new((file, ct) => RunFfmpegAsync(ffmpegPath, file, ct));

    public async Task<IntroRange?> FindAsync(string file, CancellationToken ct = default)
    {
        string key;
        try { var info = new FileInfo(file); key = $"{file}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"; }
        catch { return null; }
        if (_known.TryGetValue(key, out var cached)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_known.TryGetValue(key, out cached)) return cached;
            var metadata = await _readMetadata(file, ct);
            if (metadata is null) return null; // unreadable right now: ask again next time
            var found = IntroChapters.Find(metadata);
            _known[key] = found;
            return found;
        }
        finally { _gate.Release(); }
    }

    private static async Task<string?> RunFfmpegAsync(string ffmpegPath, string file, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-v", "error", "-i", file, "-f", "ffmetadata", "-" }) start.ArgumentList.Add(a);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token); // drained so FFmpeg never blocks on a full pipe
            await process.WaitForExitAsync(timeout.Token);
            var text = await output; await errors;
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
    }
}
