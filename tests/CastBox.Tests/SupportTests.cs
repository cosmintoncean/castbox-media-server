using Xunit;

namespace CastBox.Tests;

public class SupportTests
{
    [Fact]
    public void The_donation_link_is_the_https_Ko_fi_page()
    {
        Assert.Equal("https://ko-fi.com/comaapps", Support.DonateUrl);
    }

    [Fact]
    public void Opening_the_page_hands_the_link_to_the_browser_and_reports_the_result()
    {
        string? opened = null;
        Assert.True(Support.OpenDonatePage(url => { opened = url; return true; }));
        Assert.Equal(Support.DonateUrl, opened);
        Assert.False(Support.OpenDonatePage(_ => false));
    }
}
