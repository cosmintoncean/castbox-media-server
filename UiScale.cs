namespace CastBox;

/// <summary>
/// Sizes in the windows are written for 96 DPI (100% scaling). On a high-DPI screen (Windows 11 laptops are often at 125-200%)
/// text grows with Windows' scaling but pixel sizes do not, so each form scales its own layout when it loads.
/// CASTBOX_UI_SCALE (e.g. 1.5) pretends the screen is scaled, text included: a way to check the layout on a normal screen.
/// </summary>
internal static class UiScale
{
    private static readonly float? Override =
        float.TryParse(Environment.GetEnvironmentVariable("CASTBOX_UI_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f is > 0.5f and < 4f ? f : null;

    /// <summary>1.0 at 100%, 1.5 at 150%.</summary>
    public static float Factor { get; } = Override ?? SystemFactor();

    private static float SystemFactor()
    {
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return Math.Max(1f, g.DpiX / 96f); }
        catch { return 1f; }
    }

    public static int Dp(int pixels) => (int)Math.Round(pixels * Factor);

    /// <summary>Scales <paramref name="form"/> and everything on it once, just before it is first shown.</summary>
    public static void Apply(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.None;
        form.Load += (_, _) =>
        {
            if (Factor > 1.01f)
            {
                if (Override is not null) ScaleFonts(form, Factor); // real high-DPI screens already draw point sizes larger
                form.Scale(new SizeF(Factor, Factor));
            }
            FitToScreen(form);
        };
    }

    /// <summary>A laptop at 150% has fewer usable pixels than the window was designed for: never open larger than the screen.</summary>
    private static void FitToScreen(Form form)
    {
        var area = Screen.FromControl(form).WorkingArea;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
        var size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
        if (size == form.Size) return;
        form.Size = size;
        if (form.StartPosition == FormStartPosition.WindowsDefaultLocation || form.StartPosition == FormStartPosition.CenterScreen)
            form.Location = new Point(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2);
    }

    private static void ScaleFonts(Control root, float factor)
    {
        var all = new List<(Control, Font)>();
        void Collect(Control c) { all.Add((c, c.Font)); foreach (Control child in c.Controls) Collect(child); }
        Collect(root);
        foreach (var (c, font) in all) c.Font = new Font(font.FontFamily, font.Size * factor, font.Style, font.Unit);
    }
}
