using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace CastBox;

/// <summary>"by ComaApps" in the maker's own look (logo, Space Grotesk, orange to violet); a click opens the ComaApps website.</summary>
internal sealed class ComaAppsBadge : Control
{
    private static readonly Color Orange = Color.FromArgb(255, 138, 61), Violet = Color.FromArgb(139, 92, 246), Tile = Color.FromArgb(7, 7, 12);
    private static PrivateFontCollection? _fonts;
    private bool _hover;

    /// <summary>Space Grotesk Bold, shipped inside the app (SIL Open Font License); falls back to Segoe UI if it can't be loaded.</summary>
    private static FontFamily Family()
    {
        if (_fonts is null)
        {
            _fonts = new PrivateFontCollection();
            try
            {
                using var stream = typeof(ComaAppsBadge).Assembly.GetManifestResourceStream("CastBox.SpaceGrotesk-Bold.ttf");
                if (stream is not null)
                {
                    var data = new byte[stream.Length]; stream.ReadExactly(data);
                    var handle = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                    try { _fonts.AddMemoryFont(handle.AddrOfPinnedObject(), data.Length); } finally { handle.Free(); }
                }
            }
            catch { }
        }
        return _fonts.Families.Length > 0 ? _fonts.Families[0] : new FontFamily("Segoe UI");
    }

    public ComaAppsBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand; Size = new Size(168, 40);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        Click += (_, _) => { if (!Support.OpenWebsite()) MessageBox.Show(FindForm(), $"Couldn't open your browser. You can visit ComaApps at:\n\n{Support.WebsiteUrl}", "ComaApps", MessageBoxButtons.OK, MessageBoxIcon.Information); };
        AccessibleName = "ComaApps website"; AccessibleRole = AccessibleRole.Link;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // The ComaApps mark, scaled from its 64x64 design: a dark tile, a C-shaped arc fading orange to violet, an orange dot.
        float scale = DeviceDpi / 96f;
        int size = (int)(30 * scale);
        float k = size / 64f;
        int top = (Height - size) / 2, left = Width - size;
        using (var tile = RoundedRect(new RectangleF(left, top, size, size), 16 * k))
        using (var fill = new SolidBrush(Tile)) g.FillPath(fill, tile);
        using (var pen = new Pen(new LinearGradientBrush(new RectangleF(left, top, size, size), Orange, Violet, 45f), 7 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, left + (33.42f - 16) * k, top + 16 * k, 32 * k, 32 * k, -48.6f, -262.8f);
        using (var dot = new SolidBrush(Orange)) g.FillEllipse(dot, left + (46 - 4.5f) * k, top + (32 - 4.5f) * k, 9 * k, 9 * k);

        using var by = new Font("Segoe UI", 8.5f);
        using var name = new Font(Family(), 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        var muted = Color.FromArgb(155, 153, 173);
        var nameSize = TextRenderer.MeasureText(g, "ComaApps", name, Size.Empty, TextFormatFlags.NoPadding);
        var bySize = TextRenderer.MeasureText(g, "by", by, Size.Empty, TextFormatFlags.NoPadding);
        int nameX = left - (int)(8 * scale) - nameSize.Width;
        TextRenderer.DrawText(g, "ComaApps", name, new Point(nameX, (Height - nameSize.Height) / 2), _hover ? Orange : Color.FromArgb(241, 240, 247), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "by", by, new Point(nameX - (int)(6 * scale) - bySize.Width, (Height - bySize.Height) / 2 + 1), muted, TextFormatFlags.NoPadding);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2; var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}
