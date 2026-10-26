namespace CastBox.ScreenShare;

/// <summary>The H.264 encoders CastBox can use, best first. A video card's own encoder uses almost none of the CPU.</summary>
public enum VideoEncoder { Nvenc, Qsv, Amf, X264 }

/// <summary>Raw sound CastBox feeds to FFmpeg on its standard input (32-bit float, interleaved).</summary>
public sealed record PcmFormat(int SampleRate, int Channels);

public sealed record StreamSettings(int Screen, VideoEncoder Encoder, PcmFormat? Audio, int MaxWidth = 1920, int Fps = 30, int BitrateKbps = 8000);

public static class StreamCommand
{
    /// <summary>The name of the playlist file in an HLS directory.</summary>
    public const string HlsPlaylist = "stream.m3u8";

    /// <summary>
    /// The FFmpeg arguments that capture monitor <see cref="StreamSettings.Screen"/> (Windows Desktop Duplication), shrink it
    /// to at most <see cref="StreamSettings.MaxWidth"/> pixels wide, encode H.264 with a key frame every second (so a TV can
    /// join quickly) and write an MPEG-TS stream to standard output. Sound, when there is any, arrives as PCM on standard input.
    /// </summary>
    public static IReadOnlyList<string> Arguments(StreamSettings s, string? hlsDirectory = null)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-fflags", "+nobuffer" };
        if (s.Audio is { } pcm)
            args.AddRange(new[] { "-use_wallclock_as_timestamps", "1", "-f", "f32le", "-ar", pcm.SampleRate.ToString(), "-ac", pcm.Channels.ToString(), "-i", "pipe:0" });

        var filter = $"ddagrab=output_idx={s.Screen}:framerate={s.Fps}:draw_mouse=1,hwdownload,format=bgra," +
                     $"scale=w='min({s.MaxWidth},iw)':h=-2:flags=fast_bilinear,format=yuv420p[v]";
        args.AddRange(new[] { "-filter_complex", filter, "-map", "[v]" });
        if (s.Audio is not null) args.AddRange(new[] { "-map", "0:a" });

        var kbps = s.BitrateKbps.ToString() + "k";
        var half = (s.BitrateKbps / 2).ToString() + "k";
        var gop = s.Fps.ToString();
        switch (s.Encoder)
        {
            case VideoEncoder.Nvenc:
                args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-rc", "cbr", "-b:v", kbps, "-g", gop, "-bf", "0", "-forced-idr", "1" });
                break;
            case VideoEncoder.Qsv:
                args.AddRange(new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-b:v", kbps, "-maxrate", kbps, "-g", gop, "-bf", "0", "-look_ahead", "0" });
                break;
            case VideoEncoder.Amf:
                args.AddRange(new[] { "-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr", "-b:v", kbps, "-g", gop, "-bf", "0" });
                break;
            default:
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-b:v", kbps, "-maxrate", kbps, "-bufsize", half,
                    "-g", gop, "-x264-params", $"keyint={gop}:min-keyint={gop}:scenecut=0" });
                break;
        }
        args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        if (s.Audio is not null) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-af", "aresample=async=1:first_pts=0" });
        if (hlsDirectory is null)
            args.AddRange(new[] { "-f", "mpegts", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "-muxpreload", "0", "-flush_packets", "1", "pipe:1" });
        else
            // A live playlist of one-second pieces, the form a Chromecast's built-in player understands. Old pieces are deleted.
            args.AddRange(new[]
            {
                "-f", "hls", "-hls_time", "1", "-hls_list_size", "6", "-hls_allow_cache", "0",
                "-hls_flags", "delete_segments+independent_segments+omit_endlist+temp_file",
                "-hls_segment_filename", Path.Combine(hlsDirectory, "seg%05d.ts"), Path.Combine(hlsDirectory, HlsPlaylist),
            });
        return args;
    }
}

/// <summary>Finds FFmpeg: the copy installed with CastBox first, then any on the PC's PATH.</summary>
public static class FfmpegLocator
{
    public static string? Find(string appDirectory, string? pathVariable)
    {
        var own = Path.Combine(appDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(own)) return own;
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }
}

/// <summary>Chooses the best encoder that really works on this PC by trying each one on a few black frames.</summary>
public static class EncoderProbe
{
    private static readonly (VideoEncoder Encoder, string Name)[] Order =
    {
        (VideoEncoder.Nvenc, "h264_nvenc"), (VideoEncoder.Qsv, "h264_qsv"), (VideoEncoder.Amf, "h264_amf"),
    };

    /// <summary>
    /// <paramref name="run"/> runs FFmpeg with the given arguments and returns its exit code (non-zero or an exception means
    /// "doesn't work here"). Software x264 is the answer when no video card encoder works, so this never fails.
    /// </summary>
    public static async Task<VideoEncoder> PickAsync(Func<IReadOnlyList<string>, Task<int>> run)
    {
        foreach (var (encoder, name) in Order)
        {
            try
            {
                var code = await run(new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30", "-frames:v", "5", "-c:v", name, "-f", "null", "-" }).ConfigureAwait(false);
                if (code == 0) return encoder;
            }
            catch (Exception) { /* try the next one */ }
        }
        return VideoEncoder.X264;
    }
}
