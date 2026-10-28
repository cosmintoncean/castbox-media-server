using System.Diagnostics;

namespace CastBox;

/// <summary>Where people can support NestCast. Opening it is the only thing this does: no tracking, no data sent.</summary>
public static class Support
{
    public const string DonateUrl = "https://ko-fi.com/comaapps";

    public const string WebsiteUrl = "https://comaapps.netlify.app/";

    /// <summary>Opens the donation page in the default browser; returns false when no browser could be started.</summary>
    public static bool OpenDonatePage(Func<string, bool>? open = null) => (open ?? OpenInBrowser)(DonateUrl);

    /// <summary>Opens the ComaApps website (the maker of NestCast); returns false when no browser could be started.</summary>
    public static bool OpenWebsite(Func<string, bool>? open = null) => (open ?? OpenInBrowser)(WebsiteUrl);

    private static bool OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
