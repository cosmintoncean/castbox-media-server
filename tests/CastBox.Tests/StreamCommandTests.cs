using CastBox.ScreenShare;
using Xunit;

namespace CastBox.Tests;

public sealed class StreamCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castbox-ffmpeg-" + Guid.NewGuid().ToString("N"));

    public StreamCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Value(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void The_command_captures_the_chosen_monitor_and_writes_a_stream_to_standard_output()
    {
        var args = StreamCommand.Arguments(new StreamSettings(1, VideoEncoder.X264, null));
        var filter = Value(args, "-filter_complex");
        Assert.StartsWith("ddagrab=output_idx=1:framerate=30:draw_mouse=1,hwdownload", filter);
        Assert.Contains("min(1920,iw)", filter);
        Assert.EndsWith("[v]", filter);
        Assert.Equal("[v]", Value(args, "-map"));
        Assert.Equal("mpegts", Value(args, "-f"));
        Assert.Equal("pipe:1", args[^1]);
        Assert.DoesNotContain("pipe:0", args);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void Sound_comes_in_on_standard_input_and_is_mapped_and_encoded()
    {
        var args = StreamCommand.Arguments(new StreamSettings(0, VideoEncoder.X264, new PcmFormat(44100, 2)));
        Assert.Equal("pipe:0", Value(args, "-i"));
        Assert.Equal("f32le", args[args.ToList().IndexOf("-f", args.ToList().IndexOf("-use_wallclock_as_timestamps")) + 1]);
        Assert.Equal("44100", args[args.ToList().IndexOf("-ar") + 1]);
        Assert.Contains("0:a", args);
        Assert.Equal("aac", Value(args, "-c:a"));
    }

    [Theory]
    [InlineData(VideoEncoder.Nvenc, "h264_nvenc")]
    [InlineData(VideoEncoder.Qsv, "h264_qsv")]
    [InlineData(VideoEncoder.Amf, "h264_amf")]
    [InlineData(VideoEncoder.X264, "libx264")]
    public void Each_encoder_is_selected_with_a_key_frame_every_second_and_no_reordering(VideoEncoder encoder, string name)
    {
        var args = StreamCommand.Arguments(new StreamSettings(0, encoder, null, Fps: 30));
        Assert.Equal(name, Value(args, "-c:v"));
        Assert.Equal("30", Value(args, "-g"));
        if (encoder != VideoEncoder.X264) Assert.Equal("0", Value(args, "-bf"));
        Assert.Equal("yuv420p", Value(args, "-pix_fmt"));
    }

    [Fact]
    public void The_Chromecast_form_is_a_live_playlist_of_one_second_pieces_in_a_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hls-test");
        var args = StreamCommand.Arguments(new StreamSettings(0, VideoEncoder.X264, new PcmFormat(48000, 2)), folder);
        Assert.Equal("hls", Value(args, "-f"));
        Assert.Equal("1", Value(args, "-hls_time"));
        Assert.Contains("omit_endlist", Value(args, "-hls_flags"));   // live: never ends by itself
        Assert.Contains("delete_segments", Value(args, "-hls_flags")); // old pieces do not pile up on disk
        Assert.Equal(Path.Combine(folder, "seg%05d.ts"), Value(args, "-hls_segment_filename"));
        Assert.Equal(Path.Combine(folder, "stream.m3u8"), args[^1]);
        Assert.DoesNotContain("pipe:1", args);
        Assert.Equal("pipe:0", Value(args, "-i")); // sound still arrives on standard input
    }

    [Fact]
    public void FFmpeg_installed_with_CastBox_wins_over_one_on_the_path()
    {
        var app = Path.Combine(_dir, "app"); var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(Path.Combine(app, "ffmpeg")); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(app, "ffmpeg", "ffmpeg.exe"), "x");
        File.WriteAllText(Path.Combine(other, "ffmpeg.exe"), "x");
        Assert.Equal(Path.Combine(app, "ffmpeg", "ffmpeg.exe"), FfmpegLocator.Find(app, other));
    }

    [Fact]
    public void Otherwise_FFmpeg_is_looked_for_on_the_path_and_may_not_exist()
    {
        var app = Path.Combine(_dir, "app2"); var other = Path.Combine(_dir, "other2"); Directory.CreateDirectory(app); Directory.CreateDirectory(other);
        Assert.Null(FfmpegLocator.Find(app, other));
        Assert.Null(FfmpegLocator.Find(app, null));
        File.WriteAllText(Path.Combine(other, "ffmpeg.exe"), "x");
        var path = "\"bad|path\"" + Path.PathSeparator + "\"" + other + "\"";
        Assert.Equal(Path.Combine(other, "ffmpeg.exe"), FfmpegLocator.Find(app, path));
    }

    [Fact]
    public async Task The_first_encoder_that_works_is_used_and_software_is_the_last_resort()
    {
        var tried = new List<string>();
        Task<int> Run(IReadOnlyList<string> a) { tried.Add(a[a.ToList().IndexOf("-c:v") + 1]); return Task.FromResult(tried[^1] == "h264_amf" ? 0 : 1); }
        Assert.Equal(VideoEncoder.Amf, await EncoderProbe.PickAsync(Run));
        Assert.Equal(new[] { "h264_nvenc", "h264_qsv", "h264_amf" }, tried);

        Assert.Equal(VideoEncoder.X264, await EncoderProbe.PickAsync(_ => Task.FromResult(1)));
        Assert.Equal(VideoEncoder.X264, await EncoderProbe.PickAsync(_ => throw new InvalidOperationException("no ffmpeg")));
        Assert.Equal(VideoEncoder.Nvenc, await EncoderProbe.PickAsync(_ => Task.FromResult(0)));
    }
}
