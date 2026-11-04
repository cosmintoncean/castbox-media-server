using Xunit;

namespace CastBox.Tests;

public class IntroDetectorTests
{
    // What `ffmpeg -i file.mkv -f ffmetadata -` printed for a test file whose second chapter was named "Intro".
    private const string Sample = @";FFMETADATA1
title=t
encoder=Lavf63.1.102
[CHAPTER]
TIMEBASE=1/1000000000
START=0
END=20000000000
title=Cold open
[CHAPTER]
TIMEBASE=1/1000000000
START=20000000000
END=50000000000
title=Intro
[CHAPTER]
TIMEBASE=1/1000000000
START=50000000000
END=120000000000
title=Part A
";

    private static string Chapter(long start, long end, string title, string timebase = "1/1000") =>
        $"[CHAPTER]\nTIMEBASE={timebase}\nSTART={start}\nEND={end}\ntitle={title}\n";

    [Fact]
    public void Finds_the_chapter_named_Intro_and_converts_its_time_to_milliseconds()
    {
        var intro = IntroChapters.Find(Sample);
        Assert.NotNull(intro);
        Assert.Equal(20_000, intro!.StartMs);
        Assert.Equal(50_000, intro.EndMs);
        Assert.Equal("Intro", intro.Title);
    }

    [Fact]
    public void Understands_other_time_bases_and_Windows_line_endings()
    {
        var text = ";FFMETADATA1\r\n" + Chapter(450_000, 3_150_000, "Opening", "1/90000").Replace("\n", "\r\n");
        var intro = IntroChapters.Find(text);
        Assert.Equal((5000, 35000), (intro!.StartMs, intro.EndMs));
    }

    [Theory]
    [InlineData("Intro")]
    [InlineData("intro")]
    [InlineData("Introduction")]
    [InlineData("Opening")]
    [InlineData("Opening Credits")]
    [InlineData("Opening Theme")]
    [InlineData("OP")]
    [InlineData("OP1")]
    [InlineData("OP 2")]
    [InlineData("Main Titles")]
    [InlineData("Title Sequence")]
    [InlineData("Intro (1)")]
    [InlineData("  Intro - ")]
    public void Recognises_titles_that_name_an_intro(string title)
        => Assert.NotNull(IntroChapters.Find(Chapter(5_000, 95_000, title)));

    [Theory]
    [InlineData("Chapter 01")]
    [InlineData("Opening Scene")]
    [InlineData("Introducing the villain")]
    [InlineData("Operation")]
    [InlineData("Recap")]
    [InlineData("Previously on")]
    [InlineData("Ending")]
    [InlineData("")]
    public void Ignores_titles_that_only_look_similar(string title)
        => Assert.Null(IntroChapters.Find(Chapter(5_000, 95_000, title)));

    [Fact]
    public void Ignores_an_intro_that_is_far_into_the_video_or_an_unbelievable_length()
    {
        Assert.Null(IntroChapters.Find(Chapter(20 * 60_000, 21 * 60_000, "Intro"))); // after 15 minutes
        Assert.Null(IntroChapters.Find(Chapter(0, 5_000, "Intro")));                  // under 10 seconds
        Assert.Null(IntroChapters.Find(Chapter(0, 10 * 60_000, "Intro")));            // over 5 minutes
    }

    [Fact]
    public void Takes_the_first_matching_chapter()
    {
        var text = Chapter(0, 60_000, @"Intro") + Chapter(60_000, 120_000, "OP");
        Assert.Equal(0, IntroChapters.Find(text)!.StartMs);
    }

    [Fact]
    public void Files_without_chapters_or_with_broken_text_have_no_intro()
    {
        Assert.Null(IntroChapters.Find(""));
        Assert.Null(IntroChapters.Find(";FFMETADATA1\ntitle=Movie\n"));
        Assert.Null(IntroChapters.Find("[CHAPTER]\nTIMEBASE=x/y\nSTART=a\nEND=b\ntitle=Intro\n"));
        Assert.Null(IntroChapters.Find("[CHAPTER]\nTIMEBASE=1/0\nSTART=0\nEND=60000\ntitle=Intro\n"));
    }
}

public class IntroProbeTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "nestcast-intro-" + Guid.NewGuid().ToString("N") + ".mkv");

    public IntroProbeTests() => File.WriteAllText(_file, "video");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    private const string WithIntro = "[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=61000\ntitle=Intro\n";

    [Fact]
    public async Task Asks_FFmpeg_once_per_file_while_the_file_is_unchanged()
    {
        var calls = 0;
        var probe = new IntroProbe((_, _) => { calls++; return Task.FromResult<string?>(WithIntro); });
        var first = await probe.FindAsync(_file);
        var second = await probe.FindAsync(_file);
        Assert.Equal((1000, 61000), (first!.StartMs, first.EndMs));
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_file_that_has_no_intro_is_remembered_too()
    {
        var calls = 0;
        var probe = new IntroProbe((_, _) => { calls++; return Task.FromResult<string?>(";FFMETADATA1\n"); });
        Assert.Null(await probe.FindAsync(_file));
        Assert.Null(await probe.FindAsync(_file));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_failed_read_is_not_remembered_so_the_next_request_tries_again()
    {
        var calls = 0;
        var probe = new IntroProbe((_, _) => { calls++; return Task.FromResult<string?>(calls == 1 ? null : WithIntro); });
        Assert.Null(await probe.FindAsync(_file));
        Assert.NotNull(await probe.FindAsync(_file));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_changed_file_is_looked_at_again()
    {
        var calls = 0;
        var probe = new IntroProbe((_, _) => { calls++; return Task.FromResult<string?>(WithIntro); });
        await probe.FindAsync(_file);
        File.WriteAllText(_file, "a longer replacement file");
        await probe.FindAsync(_file);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_missing_file_has_no_intro_and_is_not_probed()
    {
        var calls = 0;
        var probe = new IntroProbe((_, _) => { calls++; return Task.FromResult<string?>(WithIntro); });
        Assert.Null(await probe.FindAsync(_file + ".gone"));
        Assert.Equal(0, calls);
    }
}
