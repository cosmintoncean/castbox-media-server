using CastBox.Chromecast;
using CastBox.ScreenShare;
using Xunit;

namespace CastBox.Tests;

public sealed class ShareTargetTests
{
    private static readonly DateTime Seen = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_Chromecast_with_the_CastBox_app_open_is_one_entry_that_uses_the_instant_route()
    {
        var devices = new[] { new CastDevice("d1", "Chromecast", "Chromecast", "192.168.1.11", 8009) };
        var apps = new[] { new TvInfo("tv1", "Chromecast", Seen, "192.168.1.11") };
        var target = Assert.Single(ShareTarget.Combine(devices, apps));
        Assert.NotNull(target.App);
        Assert.NotNull(target.Device);
        Assert.Contains("instant", target.Route);
    }

    [Fact]
    public void A_Chromecast_without_the_app_open_uses_Google_Cast_and_other_TVs_with_the_app_are_still_listed()
    {
        var devices = new[]
        {
            new CastDevice("d2", "Kitchen", "Chromecast", "192.168.1.20", 8009),
            new CastDevice("d1", "Bedroom", "Chromecast", "192.168.1.11", 8009),
        };
        var apps = new[]
        {
            new TvInfo("shield", "Shield", Seen, "192.168.1.30"),   // not a Chromecast: only reachable through the app
            new TvInfo("old", "Unknown address", Seen, null),
        };
        var list = ShareTarget.Combine(devices, apps);
        Assert.Equal(new[] { "Bedroom", "Kitchen", "Shield", "Unknown address" }, list.Select(t => t.Name));
        Assert.Null(list[0].App); Assert.Contains("Google Cast", list[0].Route);
        Assert.Null(list[2].Device); Assert.NotNull(list[2].App);
    }

    [Fact]
    public void Nothing_found_gives_an_empty_list()
    {
        Assert.Empty(ShareTarget.Combine(Array.Empty<CastDevice>(), Array.Empty<TvInfo>()));
    }
}
