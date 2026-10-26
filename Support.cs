using System.Diagnostics;

namespace CastBox;

/// <summary>Where people can support CastBox. Opening it is the only thing this does: no tracking, no data sent.</summary>
public static class Support
{
    public const string DonateUrl = "https://ko-fi.com/comaapps";

    /// <summary>Opens the donation page in the default browser; returns false when no browser could be started.</summary>
    public static bool OpenDonatePage(Func<string, bool>? open = null)
    {
        open ??= url =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        };
        return open(DonateUrl);
    }
}
