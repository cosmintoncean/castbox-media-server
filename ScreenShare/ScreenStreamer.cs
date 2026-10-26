using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CastBox.ScreenShare;

/// <summary>Lists the monitors Windows reports, numbered the way FFmpeg's capture numbers them (the order of the displays).</summary>
public static class ScreenLister
{
    public static IReadOnlyList<ScreenInfo> List()
    {
        var all = Screen.AllScreens;
        var result = new List<ScreenInfo>();
        for (var i = 0; i < all.Length; i++)
        {
            var s = all[i];
            result.Add(new ScreenInfo(i, all.Length == 1 ? "Screen" : $"Screen {i + 1}" + (s.Primary ? " (main)" : ""), s.DeviceName, s.Bounds.Width, s.Bounds.Height, s.Primary));
        }
        return result;
    }
}

/// <summary>
/// Turns sound that arrives in uneven bursts (and not at all while the PC is silent) into a steady stream: asking for
/// <c>n</c> bytes gives exactly <c>n</c> bytes, real sound if there is some and silence otherwise. FFmpeg needs that, or the
/// picture and sound drift apart whenever nothing is playing.
/// </summary>
public sealed class SilenceFiller
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly int _maxBytes;
    private int _buffered;
    private int _offset;

    /// <param name="maxBufferedBytes">Sound waiting longer than this is dropped (oldest first), so the sound can't fall behind.</param>
    public SilenceFiller(int maxBufferedBytes) => _maxBytes = maxBufferedBytes;

    public void Add(byte[] data, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(data, 0, copy, 0, count);
        lock (_lock)
        {
            _chunks.Enqueue(copy); _buffered += count;
            while (_buffered - _offset > _maxBytes && _chunks.Count > 1)
            {
                var dropped = _chunks.Dequeue();
                _buffered -= dropped.Length; _offset = 0;
            }
        }
    }

    public byte[] Take(int count)
    {
        var result = new byte[count];
        var filled = 0;
        lock (_lock)
        {
            while (filled < count && _chunks.Count > 0)
            {
                var head = _chunks.Peek();
                var n = Math.Min(count - filled, head.Length - _offset);
                Buffer.BlockCopy(head, _offset, result, filled, n);
                filled += n; _offset += n; _buffered -= n;
                if (_offset >= head.Length) { _chunks.Dequeue(); _offset = 0; }
            }
        }
        return result; // anything not filled stays zero: silence
    }
}

/// <summary>What the PC is playing ("loopback" of the default speakers), delivered as a steady PCM stream.</summary>
public sealed class LoopbackAudio : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly SilenceFiller _filler;
    private readonly int _bytesPerMs;

    private LoopbackAudio(WasapiLoopbackCapture capture)
    {
        _capture = capture;
        var f = capture.WaveFormat;
        Format = new PcmFormat(f.SampleRate, f.Channels);
        _bytesPerMs = f.SampleRate * f.Channels * 4 / 1000;
        _filler = new SilenceFiller(_bytesPerMs * 300);
        capture.DataAvailable += (_, e) => _filler.Add(e.Buffer, e.BytesRecorded);
        capture.StartRecording();
    }

    /// <summary>32-bit float samples; the format is whatever Windows mixes the speakers at.</summary>
    public PcmFormat Format { get; }

    /// <summary>Starts listening, or null if there is no sound device (the stream then has no sound).</summary>
    public static LoopbackAudio? TryStart()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            if (capture.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat && capture.WaveFormat.BitsPerSample != 32) { capture.Dispose(); return null; }
            return new LoopbackAudio(capture);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Writes steady PCM to <paramref name="output"/> (FFmpeg's input) until cancelled or the pipe closes.</summary>
    public async Task PumpAsync(Stream output, CancellationToken ct)
    {
        const int stepMs = 20;
        var clock = Stopwatch.StartNew();
        long sentMs = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Keep up with the wall clock exactly, so the sound neither runs ahead of nor behind the picture.
                var dueMs = clock.ElapsedMilliseconds;
                while (sentMs + stepMs <= dueMs)
                {
                    await output.WriteAsync(_filler.Take(stepMs * _bytesPerMs), ct).ConfigureAwait(false);
                    sentMs += stepMs;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(stepMs, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { /* the stream ended */ }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}

/// <summary>Writes a live HLS playlist of one screen into a folder until cancelled (for Chromecast's built-in player).</summary>
public interface IHlsStreamer
{
    Task RunHlsAsync(int screen, string directory, CancellationToken ct);
}

/// <summary>Runs FFmpeg for one viewer: capture a screen, encode it, and copy the result to the TV's HTTP response.</summary>
public sealed class ScreenStreamer : IScreenStreamer, IHlsStreamer
{
    private readonly string _ffmpeg;
    private readonly Action<string> _log;
    private readonly Lazy<Task<VideoEncoder>> _encoder;

    public ScreenStreamer(string ffmpegPath, Action<string> log)
    {
        _ffmpeg = ffmpegPath; _log = log;
        _encoder = new Lazy<Task<VideoEncoder>>(() => EncoderProbe.PickAsync(RunProbeAsync));
    }

    /// <summary>The encoder this PC will use (decided once, the first time somebody watches).</summary>
    public Task<VideoEncoder> EncoderAsync() => _encoder.Value;

    private async Task<int> RunProbeAsync(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); return process.ExitCode; }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } return -1; }
    }

    /// <summary>Writes an HLS live playlist and its pieces of <paramref name="screen"/> (with sound) into <paramref name="directory"/> until cancelled.</summary>
    public async Task RunHlsAsync(int screen, string directory, CancellationToken ct)
    {
        var encoder = await EncoderAsync().ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        using var audio = LoopbackAudio.TryStart();
        var args = StreamCommand.Arguments(new StreamSettings(screen, encoder, audio?.Format), directory);
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = audio is not null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _log($"Sharing screen {screen + 1} to a Chromecast ({encoder}{(audio is null ? ", no sound" : ", with sound")}).");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var errors = new Queue<string>();
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null) { lock (errors) { errors.Enqueue(line); while (errors.Count > 8) errors.Dequeue(); } }
        });
        _ = process.StandardOutput.ReadToEndAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = audio?.PumpAsync(process.StandardInput.BaseStream, stop.Token);
        try { await process.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* asked to stop */ }
        finally
        {
            stop.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            if (pump is not null) { try { await pump.ConfigureAwait(false); } catch { } }
            string[] last; lock (errors) last = errors.ToArray();
            if (!ct.IsCancellationRequested && last.Length > 0) _log("FFmpeg said: " + string.Join(" | ", last));
        }
    }

    /// <summary>Streams <paramref name="screen"/> to <paramref name="output"/> until the viewer leaves, <paramref name="ct"/> is cancelled or FFmpeg stops.</summary>
    public async Task StreamAsync(int screen, bool withSound, Stream output, CancellationToken ct)
    {
        var encoder = await EncoderAsync().ConfigureAwait(false);
        using var audio = withSound ? LoopbackAudio.TryStart() : null;
        var args = StreamCommand.Arguments(new StreamSettings(screen, encoder, audio?.Format));
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = audio is not null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _log($"Sharing screen {screen + 1} ({encoder}{(audio is null ? ", no sound" : ", with sound")}).");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var errors = new Queue<string>();
        var errorReader = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            { lock (errors) { errors.Enqueue(line); while (errors.Count > 8) errors.Dequeue(); } }
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? pump = audio is null ? null : audio.PumpAsync(process.StandardInput.BaseStream, stop.Token);
        try
        {
            var buffer = new byte[64 * 1024];
            var stdout = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), stop.Token).ConfigureAwait(false);
                await output.FlushAsync(stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { /* the viewer left or the PC ended it */ }
        finally
        {
            stop.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch { }
            if (pump is not null) { try { await pump.ConfigureAwait(false); } catch { } }
            try { await errorReader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            string[] last; lock (errors) last = errors.ToArray();
            if (!ct.IsCancellationRequested && last.Length > 0) _log("FFmpeg said: " + string.Join(" | ", last));
        }
    }
}
