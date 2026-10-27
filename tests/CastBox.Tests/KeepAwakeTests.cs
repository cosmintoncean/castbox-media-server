using Xunit;

namespace CastBox.Tests;

public class KeepAwakeTests
{
    [Theory]
    [InlineData("/api/media/abc123", true)]
    [InlineData("/api/personal/abc123", true)]
    [InlineData("/api/screens/hls/s1/seg1.ts", true)]
    [InlineData("/api/screens/stream", true)]
    [InlineData("/api/personal/abc123/thumb", false)]
    [InlineData("/api/personal/browse", false)]
    [InlineData("/api/personal/roots", false)]
    [InlineData("/api/health", false)]
    [InlineData("/api/library", false)]
    [InlineData("/api/screens/inbox", false)]
    public void Only_requests_that_carry_video_or_pictures_count_as_streaming(string path, bool expected)
        => Assert.Equal(expected, KeepAwake.IsStreamingPath(path));

    [Fact]
    public void Idle_server_lets_the_PC_sleep()
    {
        var keep = new KeepAwake(() => new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal((false, false), keep.Wanted(screenSharing: false));
    }

    [Fact]
    public void An_open_request_keeps_the_PC_awake_but_not_the_display()
    {
        var keep = new KeepAwake(() => new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        keep.Begin();
        Assert.Equal((true, false), keep.Wanted(false));
    }

    [Fact]
    public void The_PC_stays_awake_for_the_grace_period_after_the_last_request_then_may_sleep()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var keep = new KeepAwake(() => now);
        keep.Begin(); keep.End();
        now += KeepAwake.Grace - TimeSpan.FromSeconds(1);
        Assert.True(keep.Wanted(false).System);
        now += TimeSpan.FromSeconds(2);
        Assert.False(keep.Wanted(false).System);
    }

    [Fact]
    public void A_shared_screen_keeps_both_the_PC_and_the_display_awake()
    {
        var keep = new KeepAwake(() => new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal((true, true), keep.Wanted(screenSharing: true));
    }
}
