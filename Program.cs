using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Text.Json.Nodes;
using CastBox.MediaLog;
using CastBox.Personal;
using CastBox.Chromecast;
using CastBox.ScreenShare;
using Microsoft.AspNetCore.StaticFiles;

namespace CastBox;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var launchInBackground = Environment.GetCommandLineArgs().Skip(1).Contains("--background", StringComparer.OrdinalIgnoreCase);
        Application.Run(new CastBoxForm(launchInBackground));
    }
}

internal static class RoundedShape
{
    public static System.Drawing.Drawing2D.GraphicsPath Create(Rectangle rect, int diameter)
    {
        diameter = Math.Max(2, Math.Min(diameter, Math.Min(rect.Width, rect.Height)));
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedButton : Button
{
    private static readonly Color Amber = Color.FromArgb(255, 177, 57);
    public bool Primary { get; set; }
    public string Glyph { get; set; } = "";

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = RoundedShape.Create(rect, (int)(rect.Height * 0.6));
        using (var fill = new SolidBrush(BackColor)) g.FillPath(fill, path);
        if (!Primary)
        {
            using var pen = new Pen(Amber, 1.5f);
            g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Color.White, 2f);
            g.DrawPath(ring, path);
        }

        const TextFormatFlags measure = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        using var glyphFont = new Font("Segoe MDL2 Assets", Font.Size + 1.5f);
        var glyphSize = Glyph.Length > 0 ? TextRenderer.MeasureText(g, Glyph, glyphFont, Size.Empty, measure) : Size.Empty;
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, measure);
        int gap = Glyph.Length > 0 ? UiScale.Dp(12) : 0;
        int x = (Width - (glyphSize.Width + gap + textSize.Width)) / 2;
        if (Glyph.Length > 0)
        {
            TextRenderer.DrawText(g, Glyph, glyphFont, new Rectangle(x, 0, glyphSize.Width, Height), ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            x += glyphSize.Width + gap;
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), ForeColor,
            TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class RoundedPanel : Panel
{
    public RoundedPanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedShape.Create(new Rectangle(0, 0, Width - 1, Height - 1), 20);
        using (var fill = new SolidBrush(BackColor)) g.FillPath(fill, path);
        using var pen = new Pen(Color.FromArgb(110, 128, 128, 140), 1.2f);
        g.DrawPath(pen, path);
    }
}

internal sealed class StatusLabel : Label
{
    public StatusLabel() { Padding = new Padding(26, 0, 0, 0); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ForeColor);
        int dot = UiScale.Dp(14);
        e.Graphics.FillEllipse(brush, 2, (Height - dot) / 2, dot, dot);
    }
}

internal sealed class BrandHeader : Control
{
    private readonly Image? _icon;
    private static readonly Color Amber = Color.FromArgb(255, 177, 57);

    public BrandHeader()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("CastBox.CastBox.ico");
            if (stream is not null) { using var icon = new Icon(stream, new Size(256, 256)); _icon = icon.ToBitmap(); }
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int size = Math.Min(Height, UiScale.Dp(112));
        int top = (Height - size) / 2;
        if (_icon is not null) g.DrawImage(_icon, new Rectangle(0, top, size, size));
        int textX = size + UiScale.Dp(14);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        using var wordFont = new Font("Segoe UI", 40f, FontStyle.Bold, GraphicsUnit.Point);
        using var tagFont = new Font("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Point);
        var cast = TextRenderer.MeasureText(g, "Nest", wordFont, Size.Empty, flags);
        var wordHeight = cast.Height;
        var tagHeight = TextRenderer.MeasureText(g, "Your home media server", tagFont, Size.Empty, flags).Height;
        int y = top + (size - (wordHeight + tagHeight)) / 2;
        TextRenderer.DrawText(g, "Nest", wordFont, new Point(textX, y), ForeColor, flags);
        TextRenderer.DrawText(g, "Cast", wordFont, new Point(textX + cast.Width - 6, y), Amber, flags);
        var muted = Color.FromArgb((ForeColor.R * 7 + BackColor.R * 3) / 10, (ForeColor.G * 7 + BackColor.G * 3) / 10, (ForeColor.B * 7 + BackColor.B * 3) / 10);
        TextRenderer.DrawText(g, "Your home media server", tagFont, new Point(textX + 4, y + wordHeight - 4), muted, flags);
    }

    protected override void Dispose(bool disposing) { if (disposing) _icon?.Dispose(); base.Dispose(disposing); }
}

internal sealed class CastBoxForm : Form
{
    // CASTBOX_DATA_DIR lets a test copy of NestCast keep its settings, MediaLog connection and thumbnails apart from the real ones.
    private readonly string _dataDir = Environment.GetEnvironmentVariable("CASTBOX_DATA_DIR") is { Length: > 0 } custom
        ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastBox");
    private readonly string _configPath;
    private readonly ListBox _folders = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    // Pictures and videos that are shown on their own on the TV (no poster lookup, no MediaLog).
    private readonly ListBox _personal = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _status = new StatusLabel { Text = "Server stopped" };
    private readonly Label _address = new() { AutoSize = false, Text = "Not running", Font = new Font("Segoe UI Semibold", 16), Padding = new Padding(18, 0, 0, 0) };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = 5078, Width = 100, Font = new Font("Segoe UI", 11), BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox _startWithWindows = new() { Text = "Start NestCast when I sign in to Windows", AutoSize = true };
    private readonly RoundedButton _startStop = new() { Text = "Start server", Glyph = "\uE768" };
    private readonly RoundedButton _firewall = new() { Text = "Configure firewall", Glyph = "\uEA18" };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private WebApplication? _server;
    private bool _loading;
    private string _serverId = "";
    private readonly bool _launchInBackground;
    private readonly NotifyIcon _tray = new();
    private static readonly Color BrandAmber = Color.FromArgb(255, 177, 57);
    private bool _darkMode = true;
    private MenuStrip? _mainMenu;
    // MediaLog sync (docs/medialog-sync-design.md): marks what the TV finishes as watched in MediaLog.
    private readonly MediaLogStore _mediaLogStore;
    private readonly WatchSyncService _mediaLogService;
    private readonly MediaLogConnector _mediaLogConnector;
    private readonly CancellationTokenSource _syncCts = new();
    private ToolStripMenuItem? _mediaLogMenu, _mlStatus, _mlConnect, _mlConfirm, _mlForget, _mlDisconnect;
    // Screen sharing: a TV may watch one of this PC's screens only after the person here said yes (ScreenShare/).
    private readonly ScreenShareService _screenShare;
    // Keeps this PC from sleeping while video, pictures or a shared screen are going to a TV.
    private readonly KeepAwake _keepAwake = new();
    private readonly System.Windows.Forms.Timer _keepAwakeTimer = new() { Interval = 30_000 };
    private RoundedButton? _shareButton;
    private CastSessionManager? _castManager;
    private bool _ffmpegFound;

    public CastBoxForm(bool launchInBackground)
    {
        UiScale.Apply(this);
        _launchInBackground = launchInBackground;
        _configPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NestCast/" + CurrentVersion);
        var mediaLogClient = new HttpMediaLogClient(http);
        _mediaLogStore = new MediaLogStore(Path.Combine(_dataDir, "medialog.json"), new DpapiProtector());
        _mediaLogService = new WatchSyncService(_mediaLogStore, mediaLogClient, new TvMazeClient(http), log: LogAny);
        _mediaLogConnector = new MediaLogConnector(_mediaLogStore, mediaLogClient, _mediaLogService);
        _mediaLogService.Changed += () => { if (IsHandleCreated && !IsDisposed) { try { BeginInvoke(new Action(UpdateMediaLogMenu)); } catch (ObjectDisposedException) { } } };
        Text = "NestCast Server";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; } catch { Icon = SystemIcons.Application; }
        Width = 1040; Height = 960; MinimumSize = new Size(900, 840);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(25, 27, 32); ForeColor = Color.FromArgb(245, 241, 230);
        Font = new Font("Segoe UI", 10);
        _screenShare = new ScreenShareService(ScreenLister.List, null, _ => SaveSettings(quiet: true));
        _keepAwakeTimer.Tick += (_, _) => KeepAwake.Apply(_keepAwake.Wanted(_screenShare.Sessions.Count > 0));
        _keepAwakeTimer.Start();
        _screenShare.Changed += () => { if (IsHandleCreated && !IsDisposed) { try { BeginInvoke(new Action(UpdateShareButton)); } catch (ObjectDisposedException) { } } };
        _screenShare.ApprovalNeeded += request => { if (IsHandleCreated && !IsDisposed) { try { BeginInvoke(new Action(() => AskShareApproval(request))); } catch (ObjectDisposedException) { } } };
        BuildLayout(); LoadSettings(); ApplyTheme();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open NestCast", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        trayMenu.Items.Add("Stop server", null, async (_, _) => await StopServerAsync());
        trayMenu.Items.Add("Tip jar on Ko-fi...", null, (_, _) => OpenDonatePage());
        trayMenu.Items.Add("Exit", null, async (_, _) => { _tray.Visible = false; await StopServerAsync(); Close(); });
        _tray.Text = "NestCast Server"; _tray.Icon = Icon; _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += async (_, e) => { if (e.CloseReason == CloseReason.UserClosing && _server is not null) { e.Cancel = true; Hide(); _tray.Visible = true; _tray.ShowBalloonTip(1500, "NestCast is running", "The media server continues in the background.", ToolTipIcon.Info); } else { _syncCts.Cancel(); _keepAwakeTimer.Stop(); KeepAwake.Release(); _screenShare.StopAll(); _tray.Visible = false; await StopServerAsync(); } };
        Shown += async (_, _) => { _ = Task.Run(() => _mediaLogService.RunAsync(_syncCts.Token)); UpdateMediaLogMenu(); if (_startWithWindows.Checked || _launchInBackground) await StartServerAsync(); if (_launchInBackground && _server is not null) { Hide(); _tray.Visible = true; } };
    }

    private void BuildLayout()
    {
        var menu = new MenuStrip { BackColor = BackColor, ForeColor = ForeColor, Dock = DockStyle.Top, Padding = new Padding(6, 4, 0, 4) };
        _mainMenu = menu;
        var view = new ToolStripMenuItem("View");
        var theme = new ToolStripMenuItem("Dark mode") { CheckOnClick = true, Checked = true };
        theme.CheckedChanged += (_, _) =>
        {
            _darkMode = theme.Checked;
            theme.Text = _darkMode ? "Dark mode" : "Light mode";
            ApplyTheme();
        };
        var logs = new ToolStripMenuItem("Server log");
        logs.Click += (_, _) => ShowLogWindow();
        view.DropDownItems.Add(theme);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(logs);
        menu.Items.Add(view);
        var updates = new ToolStripMenuItem("Updates");
        var checkUpdates = new ToolStripMenuItem("Check for updates...");
        checkUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        updates.DropDownItems.Add(checkUpdates);
        menu.Items.Add(updates);
        BuildMediaLogMenu(menu);
        var help = new ToolStripMenuItem("Support");
        help.DropDownItems.Add(new ToolStripMenuItem("Tip jar on Ko-fi...", null, (_, _) => OpenDonatePage()));
        help.DropDownItems.Add(new ToolStripMenuItem("ComaApps website...", null, (_, _) => { if (!Support.OpenWebsite()) MessageBox.Show(this, $"Couldn't open your browser. You can visit ComaApps at:\n\n{Support.WebsiteUrl}", "ComaApps", MessageBoxButtons.OK, MessageBoxIcon.Information); }));
        menu.Items.Add(help);
        MainMenuStrip = menu;
        Controls.Add(menu);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(20, 10, 20, 18), BackColor = BackColor,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        Controls.Add(root);
        menu.BringToFront();

        var header = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Margin = new Padding(0) };
        var brand = new BrandHeader { Dock = DockStyle.Fill, BackColor = BackColor, ForeColor = ForeColor };
        header.Controls.Add(new ComaAppsBadge { Dock = DockStyle.Right, Width = 190, BackColor = BackColor });
        header.Controls.Add(brand); brand.BringToFront();
        root.Controls.Add(header, 0, 0);

        var foldersPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Margin = new Padding(0) };
        foldersPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        foldersPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        foldersPanel.Controls.Add(BuildFolderSection("Folders included in your TV library", "Add folder", "Remove folder", _folders, "Choose a folder containing movies or TV episodes"), 0, 0);
        foldersPanel.Controls.Add(BuildFolderSection("Personal folders (pictures and videos, shown separately on the TV)", "Add personal folder", "Remove personal folder", _personal, "Choose a folder with your own pictures or videos"), 0, 1);
        root.Controls.Add(foldersPanel, 0, 1);

        var setup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        setup.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setup.Controls.Add(new Label { Text = "Port", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 11) }, 0, 0);
        _port.Dock = DockStyle.None; _port.Anchor = AnchorStyles.Left | AnchorStyles.Right; _port.Margin = new Padding(0, 0, 8, 0); setup.Controls.Add(_port, 1, 0);
        var save = MakeButton("Save settings"); StyleButton(save, true); save.Dock = DockStyle.None; save.Anchor = AnchorStyles.Left | AnchorStyles.Right; save.Margin = new Padding(0, 0, 20, 0);
        save.Click += (_, _) => { SaveSettings(); ApplyStartupSetting(); }; setup.Controls.Add(save, 2, 0);
        _startWithWindows.Dock = DockStyle.Fill; _startWithWindows.CheckAlign = ContentAlignment.MiddleLeft; _startWithWindows.TextAlign = ContentAlignment.MiddleLeft; _startWithWindows.Margin = new Padding(0, 0, 0, 0); setup.Controls.Add(_startWithWindows, 3, 0);
        root.Controls.Add(setup, 0, 2);

        var serverPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        serverPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        StyleButton(_startStop, true); _startStop.Dock = DockStyle.None; _startStop.Anchor = AnchorStyles.Left | AnchorStyles.Right; _startStop.Margin = new Padding(0, 0, 14, 0);
        _startStop.Click += async (_, _) => { if (_server is null) await StartServerAsync(); else await StopServerAsync(); };
        StyleButton(_firewall, false); _firewall.Dock = DockStyle.None; _firewall.Anchor = AnchorStyles.Left | AnchorStyles.Right; _firewall.Margin = new Padding(0, 0, 14, 0); _firewall.Click += (_, _) => ConfigureFirewall();
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.Margin = new Padding(2, 0, 0, 0); _status.Font = new Font("Segoe UI Semibold", 12);
        _shareButton = MakeButton("Share screen"); StyleButton(_shareButton, false); _shareButton.Glyph = "\uE7F4"; _shareButton.Dock = DockStyle.None; _shareButton.Anchor = AnchorStyles.Left | AnchorStyles.Right; _shareButton.Margin = new Padding(0, 0, 14, 0);
        _shareButton.Click += (_, _) => OpenShareDialog();
        serverPanel.Controls.Add(_startStop, 0, 0); serverPanel.Controls.Add(_firewall, 1, 0); serverPanel.Controls.Add(_shareButton, 2, 0); serverPanel.Controls.Add(_status, 3, 0);
        root.Controls.Add(serverPanel, 0, 3);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Margin = new Padding(0, 4, 0, 0) };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(new Label { Text = "Connection address for your NestCast TV app", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 12) }, 0, 0);
        var addr = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        addr.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        addr.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); addr.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        var addrBox = new RoundedPanel { Dock = DockStyle.None, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 52, Margin = new Padding(0, 0, 14, 4), Padding = new Padding(2), BackColor = PanelSurface };
        _address.ForeColor = BrandAmber; _address.Dock = DockStyle.Fill; _address.TextAlign = ContentAlignment.MiddleLeft; _address.Margin = new Padding(0);
        addrBox.Controls.Add(_address);
        var copy = MakeButton("Copy address"); StyleButton(copy, true); copy.Glyph = "\uE8C8"; copy.Dock = DockStyle.None; copy.Anchor = AnchorStyles.Left | AnchorStyles.Right; copy.MinimumSize = new Size(90, 52); copy.Height = 52; copy.Margin = new Padding(0, 0, 0, 4);
        copy.Click += (_, _) => { if (_address.Text.StartsWith("http")) Clipboard.SetText(_address.Text); };
        addr.Controls.Add(addrBox, 0, 0); addr.Controls.Add(copy, 1, 0); bottom.Controls.Add(addr, 0, 1);
        root.Controls.Add(bottom, 0, 4);
    }

    private Color Surface => _darkMode ? Color.FromArgb(25, 27, 32) : Color.FromArgb(247, 247, 244);
    private Color PanelSurface => _darkMode ? Color.FromArgb(32, 34, 40) : Color.White;
    private Color TextPrimary => _darkMode ? Color.FromArgb(245, 241, 230) : Color.FromArgb(35, 37, 42);
    private Color ButtonSurface => _darkMode ? Color.FromArgb(50, 38, 22) : Color.FromArgb(235, 235, 230);

    private RoundedButton MakeButton(string text)
    {
        var b = new RoundedButton { Text = text, AutoSize = false, Height = 34, MinimumSize = new Size(90, 32),
            FlatStyle = FlatStyle.Flat, BackColor = ButtonSurface, ForeColor = BrandAmber,
            Font = new Font("Segoe UI Semibold", 9.5f), Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(4), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        b.Primary = false;
        return b;
    }

    private void StyleButton(Button b, bool primary)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = primary ? BrandAmber : ButtonSurface;
        b.ForeColor = primary ? Color.FromArgb(25, 27, 32) : BrandAmber;
        b.FlatAppearance.BorderSize = 0;
        b.Padding = new Padding(10, 4, 10, 4);
        b.Height = 34; b.MinimumSize = new Size(90, 34);
        if (b is RoundedButton rb) rb.Primary = primary;
    }

    private void ApplyTheme()
    {
        BackColor = Surface;
        ForeColor = TextPrimary;
        ApplyThemeToControls(this);
        if (_mainMenu is not null)
        {
            _mainMenu.BackColor = Surface;
            _mainMenu.ForeColor = TextPrimary;
            foreach (ToolStripItem item in _mainMenu.Items) ApplyThemeToMenuItem(item);
        }
        _folders.BackColor = PanelSurface;
        _folders.ForeColor = TextPrimary;
        _personal.BackColor = PanelSurface;
        _personal.ForeColor = TextPrimary;
        _log.BackColor = PanelSurface;
        _log.ForeColor = TextPrimary;
        _address.ForeColor = BrandAmber;
        UpdateMediaLogMenu();
        Invalidate(true);
    }

    private void ApplyThemeToControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.BackColor = Surface;
            control.ForeColor = TextPrimary;
            if (control is TableLayoutPanel or Panel or GroupBox) control.BackColor = Surface;
            if (control is ListBox or TextBox or RoundedPanel || control.Parent is RoundedPanel) control.BackColor = PanelSurface;
            if (control is RoundedButton button)
            {
                button.BackColor = button.Primary ? BrandAmber : ButtonSurface;
                button.ForeColor = button.Primary ? Color.FromArgb(25, 27, 32) : BrandAmber;
            }
            else if (control is Button regular)
            {
                regular.BackColor = ButtonSurface;
                regular.ForeColor = BrandAmber;
            }
            if (control is PictureBox picture) picture.BackColor = Surface;
            ApplyThemeToControls(control);
        }
    }

    private void ApplyThemeToMenuItem(ToolStripItem item)
    {
        item.BackColor = Surface;
        item.ForeColor = TextPrimary;
        if (item is ToolStripMenuItem menuItem)
            foreach (ToolStripItem child in menuItem.DropDownItems) ApplyThemeToMenuItem(child);
    }

    /// <summary>A titled list of folders with Add and Remove buttons; used for the movie folders and the personal folders.</summary>
    private Control BuildFolderSection(string title, string addText, string removeText, ListBox list, string pickerText)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = BackColor, Margin = new Padding(0, 4, 0, 8) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 8), Font = new Font("Segoe UI Semibold", 12) }, 0, 0);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var add = MakeButton(addText.Replace(" personal", "")); add.Glyph = "\uE710"; add.Dock = DockStyle.Bottom; add.Margin = new Padding(4, 0, 0, 6);
        add.Click += (_, _) => AddFolder(list, pickerText);
        var remove = MakeButton("Remove"); remove.Glyph = "\uE738"; remove.Dock = DockStyle.Bottom; remove.Margin = new Padding(4, 0, 0, 6);
        remove.Click += (_, _) => { if (list.SelectedItem is string selected) { list.Items.Remove(selected); SaveSettings(); Log(removeText + ": " + selected); } };
        buttons.Controls.Add(add, 0, 0); buttons.Controls.Add(remove, 1, 0);
        panel.Controls.Add(buttons, 1, 0);
        var box = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2), Padding = new Padding(16, 10, 16, 10), BackColor = Color.FromArgb(32, 34, 40) };
        list.Dock = DockStyle.Fill; list.Font = new Font("Segoe UI", 11);
        list.BackColor = Color.FromArgb(32, 34, 40); list.ForeColor = ForeColor; list.BorderStyle = BorderStyle.None;
        box.Controls.Add(list);
        panel.Controls.Add(box, 0, 1); panel.SetColumnSpan(box, 2);
        return panel;
    }

    private void AddFolder(ListBox list, string pickerText)
    {
        using var dialog = new FolderBrowserDialog { Description = pickerText, UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // A folder can be in one list only, so the TV never shows the same files twice.
        _folders.Items.Remove(_folders.Items.Cast<string>().FirstOrDefault(x => string.Equals(x, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase)) ?? "");
        _personal.Items.Remove(_personal.Items.Cast<string>().FirstOrDefault(x => string.Equals(x, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase)) ?? "");
        list.Items.Add(dialog.SelectedPath);
        SaveSettings(); Log((ReferenceEquals(list, _personal) ? "Added personal folder: " : "Added folder: ") + dialog.SelectedPath);
    }

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            if (File.Exists(_configPath))
            {
                var cfg = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_configPath)) ?? new AppSettings();
                _serverId = cfg.ServerId ?? "";
                _port.Value = Math.Clamp(cfg.Port, (int)_port.Minimum, (int)_port.Maximum);
                // Keep folders that aren't there right now (a drive that isn't ready yet at sign-in, a network share): they are
                // skipped while missing but must not be forgotten, or the next save would erase them from the settings.
                foreach (var f in cfg.MediaFolders) { _folders.Items.Add(f); if (!Directory.Exists(f)) Log("Folder not available yet: " + f); }
                foreach (var f in cfg.PersonalFolders) { _personal.Items.Add(f); if (!Directory.Exists(f)) Log("Personal folder not available yet: " + f); }
                _screenShare.LoadAllowed(cfg.AllowedTvs);
                _startWithWindows.Checked = IsStartupEnabled();
            }
            else _startWithWindows.Checked = IsStartupEnabled();
        }
        catch (Exception ex) { Log("Could not load settings: " + ex.Message); }
        finally { _loading = false; }
        // A permanent ID lets the TV app find this PC again after its network address changes.
        if (string.IsNullOrEmpty(_serverId)) { _serverId = Guid.NewGuid().ToString("N"); SaveSettings(); }
    }
    private void SaveSettings(bool quiet = false)
    {
        if (_loading) return;
        try
        {
            var cfg = new AppSettings { Port = (int)_port.Value, ServerId = _serverId, MediaFolders = _folders.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(), PersonalFolders = _personal.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(), AllowedTvs = _screenShare.AllowedTvs.ToDictionary(a => a.Key, a => a.Value) };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            if (quiet) return;
            Log("Settings saved.");
            if (_server is not null) Log("Restart the server to apply the changed port or folder list.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't save settings: " + ex.Message, "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task StartServerAsync()
    {
        if (_server is not null) return;
        SaveSettings();
        if (_folders.Items.Count == 0 && _personal.Items.Count == 0) { MessageBox.Show(this, "Add at least one folder first.", "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try
        {
            var port = (int)_port.Value;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(CastBoxForm).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, port));
            builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));
            var app = builder.Build(); app.UseCors();
            app.Use(async (ctx, next) =>
            {
                if (!KeepAwake.IsStreamingPath(ctx.Request.Path)) { await next(); return; }
                _keepAwake.Begin();
                try { await next(); } finally { _keepAwake.End(); }
            });
            var configuredFolders = _folders.Items.Cast<string>().ToList();
            var personalFolders = _personal.Items.Cast<string>().ToList();
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".mpeg", ".mpg", ".ts" };
            var types = new FileExtensionContentTypeProvider();
            string MakeId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24].ToLowerInvariant();
            string? FindFile(string id)
            {
                foreach (var root in configuredFolders)
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                            if (supported.Contains(Path.GetExtension(file)) && string.Equals(MakeId(file), id, StringComparison.OrdinalIgnoreCase)) return file;
                    }
                    catch { }
                }
                return null;
            }
            app.MapGet("/api/health", () => Results.Ok(new { status = "ok", name = "NestCast", folders = configuredFolders.Count, personalFolders = personalFolders.Count, serverId = _serverId }));
            // Which MediaLog account this PC reports to, so the TV can ask "who's watching?" when PCs differ.
            app.MapGet("/api/medialog", () =>
            {
                var s = _mediaLogService.Snapshot();
                return Results.Ok(new { connected = s.Connected && !s.NeedsReconnect, accountId = s.AccountId, username = s.Username, label = s.Label });
            });
            app.MapGet("/api/library", () =>
            {
                var files = new List<object>();
                foreach (var root in configuredFolders)
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            if (!supported.Contains(Path.GetExtension(file))) continue;
                            var info = new FileInfo(file); var id = MakeId(file);
                            // rootName/relativeFolder let clients rebuild the folder tree without the PC's absolute paths.
                            var rootName = Path.GetFileName(root.TrimEnd('\\', '/')); if (rootName.Length == 0) rootName = root.TrimEnd('\\', '/');
                            var relativeFolder = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? root).Replace('\\', '/'); if (relativeFolder == ".") relativeFolder = "";
                            files.Add(new { id, title = Path.GetFileNameWithoutExtension(file), fileName = info.Name, extension = Path.GetExtension(file).TrimStart('.').ToLowerInvariant(), folder = Path.GetDirectoryName(file), rootName, relativeFolder, sizeBytes = info.Length, modifiedUtc = info.LastWriteTimeUtc, url = $"http://{GetLanIp()}:{port}/api/media/{id}" });
                        }
                    }
                    catch { }
                }
                return Results.Ok(files);
            });
            app.MapGet("/api/media/{id}", async (string id, HttpContext ctx) =>
            {
                foreach (var root in configuredFolders)
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        {
                            if (!supported.Contains(Path.GetExtension(file)) || !string.Equals(MakeId(file), id, StringComparison.OrdinalIgnoreCase)) continue;
                            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            if (!types.TryGetContentType(file, out var mime)) mime = "application/octet-stream";
                            if (Path.GetExtension(file).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) mime = "video/x-matroska";
                            return Results.File(stream, mime, enableRangeProcessing: true);
                        }
                    }
                    catch { }
                }
                return Results.NotFound();
            });
            // Personal folders: the person's own pictures and videos, served separately from the movie library. A library can
            // hold hundreds of thousands of pictures, so the TV browses one folder at a time from an index kept here.
            var imageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
            var thumbDir = Path.Combine(_dataDir, "thumbs");
            var indexLock = new object();
            Task<PersonalIndex>? indexTask = null;
            DateTime indexStartedUtc = DateTime.MinValue;
            Task<PersonalIndex> StartIndexBuild()
            {
                indexStartedUtc = DateTime.UtcNow;
                return Task.Run(() =>
                {
                    var built = PersonalIndex.Build(personalFolders, MakeId, imageTypes, supported);
                    LogAny($"Personal folders indexed: {built.Pictures:N0} pictures, {built.Videos:N0} videos.");
                    return built;
                });
            }
            // The first request waits for the index; later ones reuse it and refresh it in the background every few minutes.
            Task<PersonalIndex> GetIndex(bool allowRefresh)
            {
                lock (indexLock)
                {
                    if (indexTask is null) indexTask = StartIndexBuild();
                    else if (allowRefresh && indexTask.IsCompleted && DateTime.UtcNow - indexStartedUtc > TimeSpan.FromMinutes(3))
                    {
                        var old = indexTask;
                        var fresh = StartIndexBuild();
                        indexTask = fresh.ContinueWith(t => t.IsFaulted ? old.Result : t.Result);
                    }
                    return indexTask;
                }
            }
            if (personalFolders.Count > 0) GetIndex(false); // start reading the folders now rather than at the first request
            // Looking up the network address takes ~0.2 s, too slow to do for every page of thumbnails.
            var lanCache = ("", DateTime.MinValue);
            string Lan()
            {
                var cached = lanCache;
                if (cached.Item1.Length > 0 && DateTime.UtcNow - cached.Item2 < TimeSpan.FromSeconds(30)) return cached.Item1;
                var fresh = GetLanIp(); lanCache = (fresh, DateTime.UtcNow); return fresh;
            }
            object FileJson(PersonalFile f, string lan)
            {
                var url = $"http://{lan}:{port}/api/personal/{f.Id}";
                return new { id = f.Id, kind = f.IsImage ? "image" : "video", title = f.Title, fileName = f.Name, extension = f.Extension, sizeBytes = f.Size, modifiedUtc = f.ModifiedUtc, url, thumbUrl = f.IsImage ? url + "/thumb" : null };
            }
            string? CoverUrl(PersonalDir d, string lan) => d.Cover is { } c ? $"http://{lan}:{port}/api/personal/{c.Id}/thumb" : null;
            // The folders shared and how much is in them, enough for the TV's home screen and top level.
            app.MapGet("/api/personal/roots", async () =>
            {
                var index = await GetIndex(true);
                var lan = Lan();
                return Results.Ok(new
                {
                    pictures = index.Pictures, videos = index.Videos,
                    roots = index.Roots.Select((r, i) => new { index = i, name = r.Name, items = r.TotalItems, coverUrl = CoverUrl(r, lan) }).ToList()
                });
            });
            // One folder: its sub-folders, and a page of the pictures and videos directly inside it.
            app.MapGet("/api/personal/browse", async (int? root, string? path, int? offset, int? limit) =>
            {
                var index = await GetIndex(false);
                var dir = index.Resolve(root ?? 0, path ?? "");
                if (dir is null) return Results.NotFound(new { error = "unknown_folder" });
                var lan = Lan();
                var from = Math.Max(0, offset ?? 0); var take = Math.Clamp(limit ?? 120, 1, 500);
                var page = dir.Files.Skip(from).Take(take).Select(f => FileJson(f, lan)).ToList();
                return Results.Ok(new
                {
                    name = dir.Name, items = dir.TotalItems, totalFiles = dir.Files.Count, offset = from, hasMore = from + page.Count < dir.Files.Count,
                    folders = from == 0 ? dir.Dirs.Select(d => new { name = d.Name, items = d.TotalItems, coverUrl = CoverUrl(d, lan) }).Cast<object>().ToList() : new List<object>(),
                    files = page
                });
            });
            async Task<string?> FindPersonalAsync(string id)
            {
                if (personalFolders.Count == 0) return null;
                var file = (await GetIndex(false)).Find(id);
                return file is not null && File.Exists(file.FullPath) ? file.FullPath : null;
            }
            app.MapGet("/api/personal/{id}", async (string id) =>
            {
                var file = await FindPersonalAsync(id);
                if (file is null) return Results.NotFound();
                try
                {
                    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (!types.TryGetContentType(file, out var mime)) mime = "application/octet-stream";
                    if (Path.GetExtension(file).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) mime = "video/x-matroska";
                    return Results.File(stream, mime, enableRangeProcessing: true);
                }
                catch { return Results.NotFound(); }
            });
            app.MapGet("/api/personal/{id}/thumb", async (string id, int? w, HttpContext ctx) =>
            {
                var file = await FindPersonalAsync(id);
                if (file is null || !imageTypes.Contains(Path.GetExtension(file))) return Results.NotFound();
                var side = Math.Clamp(w ?? 480, 120, 1280);
                try
                {
                    var cached = Path.Combine(thumbDir, $"{id}-{side}-{File.GetLastWriteTimeUtc(file).Ticks}.jpg");
                    if (!File.Exists(cached)) await Task.Run(() => Thumbnails.Create(file, cached, side));
                    ctx.Response.Headers.CacheControl = "private, max-age=86400";
                    return Results.File(new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.Read), "image/jpeg");
                }
                catch
                {
                    // A picture System.Drawing can't read (some WebP files, say): the TV can still show the original.
                    return Results.Redirect($"/api/personal/{id}");
                }
            });
            // Screen sharing: a TV watches one of this PC's screens (capture + encode with FFmpeg) once the person here allowed it.
            var ffmpegPath = FfmpegLocator.Find(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH"));
            _ffmpegFound = ffmpegPath is not null;
            var streamer = ffmpegPath is null ? null : new ScreenStreamer(ffmpegPath, LogAny);
            if (streamer is null) LogAny("Screen sharing is off: FFmpeg was not found (see the ffmpeg folder next to CastBox.exe).");
            // Casting to a Chromecast through Google Cast: FFmpeg writes an HLS playlist that this server serves to it.
            _castManager = streamer is null ? null : new CastSessionManager(_screenShare, streamer, Lan, () => port,
                async (address, castPort, ct) => await CastConnection.ConnectAsync(address, castPort, ct), Path.Combine(Path.GetTempPath(), "castbox-hls"), LogAny);
            var castManager = _castManager;
            ScreenEndpoints.Map(app, _screenShare, streamer, Lan, port, LogAny, (session, name) =>
            {
                try { BeginInvoke(new Action(() => _tray.ShowBalloonTip(2500, "NestCast screen sharing", $"{session.TvName} is watching {name}.", ToolTipIcon.Info))); } catch (ObjectDisposedException) { }
            }, hlsFile: castManager is null ? null : castManager.FileFor);
            // The TV reports that it finished a file; MediaLog gets it in the background (MediaLog/WatchSyncService.cs).
            app.MapPost("/api/watched", async (HttpContext ctx) =>
            {
                if (ctx.Request.ContentLength is > 4096) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                JsonNode? body;
                try
                {
                    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync();
                    if (text.Length > 4096) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    body = JsonNode.Parse(text);
                }
                catch (JsonException) { return Results.BadRequest(new { error = "invalid_json" }); }
                if (body is not JsonObject o) return Results.BadRequest(new { error = "invalid_json" });
                if ((string?)o["event"] != "completed") return Results.BadRequest(new { error = "unsupported_event" });
                var mediaId = (string?)o["id"]; var eventId = (string?)o["clientEventId"];
                if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(eventId) || eventId.Length > 100) return Results.BadRequest(new { error = "invalid_request" });
                var at = DateTimeOffset.TryParse((string?)o["at"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : DateTime.UtcNow;
                if (at > DateTime.UtcNow.AddMinutes(5)) at = DateTime.UtcNow;
                // A file from another PC (the person chose this PC's account) comes with its path, used only to work out the title.
                var file = FindFile(mediaId) ?? ((string?)o["path"] is { Length: > 0 and <= 600 } hint ? hint : null);
                if (file is null) return Results.NotFound(new { error = "unknown_media" });
                try
                {
                    var result = _mediaLogService.Enqueue(mediaId, file, at, eventId);
                    return result == EnqueueResult.Invalid
                        ? Results.BadRequest(new { error = "invalid_request" })
                        : Results.Accepted(value: new { status = result == EnqueueResult.Accepted ? "queued" : "ignored" });
                }
                catch (Exception ex) { LogAny("Couldn't queue a watch: " + ex.Message); return Results.StatusCode(StatusCodes.Status500InternalServerError); }
            });
            await app.StartAsync(); _server = app;
            _status.Text = "Running"; _status.ForeColor = Color.LightGreen; _startStop.Text = "Stop server"; _startStop.Glyph = "\u25A0"; StyleButton(_startStop, false);
            _address.Text = $"http://{GetLanIp()}:{port}"; Log("Server started at " + _address.Text);
        }
        catch (Exception ex) { _server = null; MessageBox.Show(this, "Couldn't start the server. Check whether the port is already in use.\n\n" + ex.Message, "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Error); Log("Start failed: " + ex); }
    }

    private async Task StopServerAsync()
    {
        if (_server is null) return;
        _screenShare.StopAll();
        if (_castManager is not null) await _castManager.StopAllAsync();
        try { await _server.StopAsync(); await _server.DisposeAsync(); } catch { }
        _server = null; _status.Text = "Server stopped"; _status.ForeColor = ForeColor; _startStop.Text = "Start server"; _startStop.Glyph = "\uE768"; StyleButton(_startStop, true); _address.Text = "Not running"; Log("Server stopped.");
    }

    // Change this to your GitHub owner/repository before publishing releases.
    private const string GitHubRepository = "cosmintoncean/castbox-media-server";
    private void OpenDonatePage()
    {
        if (!Support.OpenDonatePage())
            MessageBox.Show(this, $"Couldn't open your browser. You can support NestCast at:\n\n{Support.DonateUrl}", "Support NestCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private const string CurrentVersion = "0.8.5";

    private async Task CheckForUpdatesAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NestCast-Updater/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            var api = $"https://api.github.com/repos/{GitHubRepository}/releases/latest";
            using var response = await http.GetAsync(api);
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show(this,
                    response.StatusCode == HttpStatusCode.NotFound
                        ? "No published release was found. Check that the GitHub repository is public and has a release, and update GitHubRepository in Program.cs."
                        : $"GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}). Try again later.",
                    "NestCast updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString()?.TrimStart('v', 'V') : null;
            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : "";
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag, out var latest))
            {
                MessageBox.Show(this, "The latest GitHub release has no valid version tag. Use tags such as v0.1.1.", "NestCast updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Version.TryParse(CurrentVersion, out var current)) current = new Version(0, 1, 0);
            if (latest <= current)
            {
                MessageBox.Show(this, $"You're up to date. Installed version: {CurrentVersion}.", "NestCast updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string? installerUrl = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                    if (asset.TryGetProperty("name", out var name) && string.Equals(name.GetString(), "CastBox-Setup.exe", StringComparison.OrdinalIgnoreCase) && asset.TryGetProperty("browser_download_url", out var url))
                    { installerUrl = url.GetString(); break; }

            if (string.IsNullOrWhiteSpace(installerUrl))
            {
                if (MessageBox.Show(this, $"Version {tag} is available, but the release doesn't contain CastBox-Setup.exe.\n\nOpen the release page to download it manually?", "NestCast updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo($"https://github.com/{GitHubRepository}/releases/latest") { UseShellExecute = true });
                return;
            }

            var prompt = $"NestCast {tag} is available (installed: {CurrentVersion}).\n\n{notes}\n\nDownload and launch the installer? NestCast will close during installation.";
            if (MessageBox.Show(this, prompt, "NestCast update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var tempInstaller = Path.Combine(Path.GetTempPath(), "CastBox-Setup.exe");
            using (var download = await http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();
                await using var destination = new FileStream(tempInstaller, FileMode.Create, FileAccess.Write, FileShare.None);
                await download.Content.CopyToAsync(destination);
            }
            var start = new ProcessStartInfo(tempInstaller, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true };
            Process.Start(start);
            await StopServerAsync();
            _tray.Visible = false;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't check or download updates. Check your internet connection and GitHub release configuration.\n\n" + ex.Message, "NestCast updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log("Update check failed: " + ex.Message);
        }
    }

    private void ConfigureFirewall()
    {
        try
        {
            var exe = Application.ExecutablePath;
            var command = $"advfirewall firewall add rule name=\"NestCast Media Server TCP {(int)_port.Value}\" dir=in action=allow protocol=TCP localport={(int)_port.Value} profile=private";
            var psi = new ProcessStartInfo("netsh.exe", command) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            Log("Requested an administrator approval to add the private-network firewall rule for TCP " + (int)_port.Value + ".");
            MessageBox.Show(this, "Windows may ask for administrator approval. Allow the NestCast port on private networks only.", "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Firewall setup couldn't be started: " + ex.Message, "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private bool IsStartupEnabled()
    {
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("CastBox") is string; } catch { return false; }
    }
    private void ApplyStartupSetting()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true) ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_startWithWindows.Checked) key.SetValue("CastBox", "\"" + Application.ExecutablePath + "\" --background"); else key.DeleteValue("CastBox", false);
            Log(_startWithWindows.Checked ? "NestCast will start when you sign in to Windows." : "Automatic startup disabled.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't update startup setting: " + ex.Message, "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private static string GetLanIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) && (ua.Address.ToString().StartsWith("192.168.") || ua.Address.ToString().StartsWith("10.") || ua.Address.ToString().StartsWith("172."))) return ua.Address.ToString();
        } catch { }
        return "127.0.0.1";
    }
    /// <summary>Log from any thread (the MediaLog worker runs in the background).</summary>
    private void LogAny(string line)
    {
        if (IsDisposed) return;
        if (!InvokeRequired) { Log(line); return; }
        try { BeginInvoke(new Action(() => Log(line))); } catch (ObjectDisposedException) { }
    }

    private void BuildMediaLogMenu(MenuStrip menu)
    {
        _mediaLogMenu = new ToolStripMenuItem("MediaLog");
        _mlStatus = new ToolStripMenuItem("Not connected") { Enabled = false };
        _mlConnect = new ToolStripMenuItem("Connect to MediaLog...");
        _mlConnect.Click += (_, _) => { using var dialog = new MediaLogConnectDialog(_mediaLogConnector, _mediaLogStore.Read(s => s.Address)); dialog.ShowDialog(this); UpdateMediaLogMenu(); };
        _mlConfirm = new ToolStripMenuItem("Needs confirmation...");
        _mlConfirm.Click += (_, _) => { using var dialog = new MediaLogConfirmDialog(_mediaLogService); dialog.ShowDialog(this); UpdateMediaLogMenu(); };
        _mlForget = new ToolStripMenuItem("Forget saved choices");
        _mlForget.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Forget which MediaLog titles you confirmed and the show lookups NestCast saved? They will be worked out again next time.", "NestCast", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) _mediaLogService.ForgetSavedChoices();
        };
        _mlDisconnect = new ToolStripMenuItem("Disconnect");
        _mlDisconnect.Click += async (_, _) => await DisconnectMediaLogAsync();
        _mediaLogMenu.DropDownItems.AddRange(new ToolStripItem[] { _mlStatus, new ToolStripSeparator(), _mlConnect, _mlConfirm, _mlForget, new ToolStripSeparator(), _mlDisconnect });
        menu.Items.Add(_mediaLogMenu);
    }

    /// <summary>Shows the question when a TV asks to watch a screen. The TV waits for the answer.</summary>
    private void AskShareApproval(ShareRequest request)
    {
        if (_screenShare.Poll(request.Id)?.Status != ShareStatus.Pending) return;
        if (!Visible || WindowState == FormWindowState.Minimized) _tray.ShowBalloonTip(3000, "NestCast screen sharing", $"{request.TvName} wants to watch a screen of this PC.", ToolTipIcon.Info);
        using var dialog = new ShareApprovalDialog(_screenShare, request);
        dialog.ShowDialog(this);
        _screenShare.Resolve(request.Id, dialog.DialogResult == DialogResult.OK ? dialog.Choice : ConsentChoice.Deny);
        Log($"{request.TvName} asked to watch a screen: {(dialog.DialogResult == DialogResult.OK ? dialog.Choice.ToString() : "not allowed")}.");
    }

    private void OpenShareDialog()
    {
        using var dialog = new ShareScreenDialog(_screenShare, _ffmpegFound || FfmpegLocator.Find(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH")) is not null,
            ct => CastDiscovery.FindAsync(TimeSpan.FromSeconds(2.5), ct),
            (device, screen, ct) => _castManager is { } manager && _server is not null ? manager.StartAsync(device, screen, ct)
                : throw new CastException("Start the server first (the Start server button), then share."));
        dialog.ShowDialog(this);
        UpdateShareButton();
    }

    private void UpdateShareButton()
    {
        if (_shareButton is null) return;
        var watching = _screenShare.Sessions.Count;
        _shareButton.Text = watching > 0 ? $"Sharing ({watching})" : "Share screen";
    }

    private async Task DisconnectMediaLogAsync()
    {
        if (MessageBox.Show(this, "Disconnect NestCast from MediaLog? Watches still waiting to be sent are forgotten. You can connect again any time.", "NestCast", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _mediaLogConnector.DisconnectAsync(CancellationToken.None); }
        catch (MediaLogException ex) { MessageBox.Show(this, "NestCast is disconnected here, but MediaLog couldn't be told: " + ex.Message, "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        UpdateMediaLogMenu();
    }

    /// <summary>Status line and badge: how many watches wait for you, or that MediaLog needs reconnecting.</summary>
    private void UpdateMediaLogMenu()
    {
        if (_mediaLogMenu is null || _mlStatus is null || _mlConnect is null || _mlConfirm is null || _mlDisconnect is null) return;
        var s = _mediaLogService.Snapshot();
        _mlStatus.Text = !s.Connected ? "Not connected"
            : s.NeedsReconnect ? "MediaLog no longer accepts this PC: reconnect"
            : $"Connected: {(s.Username is { } u ? "@" + u : s.Label ?? "MediaLog")}  ·  {s.SentToday} sent today  ·  {s.Waiting} waiting";
        _mlConnect.Text = s.NeedsReconnect ? "Reconnect..." : s.Connected ? "Connect again..." : "Connect to MediaLog...";
        _mlConfirm.Text = s.NeedsConfirmation > 0 ? $"Needs confirmation ({s.NeedsConfirmation})..." : "Needs confirmation...";
        _mlConfirm.Enabled = s.Connected; _mlDisconnect.Enabled = s.Connected;
        var attention = s.NeedsConfirmation > 0 || s.NeedsReconnect;
        _mediaLogMenu.Text = s.NeedsConfirmation > 0 ? $"MediaLog ({s.NeedsConfirmation})" : s.NeedsReconnect ? "MediaLog (!)" : "MediaLog";
        _mediaLogMenu.ForeColor = attention ? BrandAmber : TextPrimary;
    }

    private void Log(string line) { if (_log.IsDisposed) return; _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); }
    private void ShowLogWindow()
    {
        var f = new Form { Text = "NestCast server log", Width = 700, Height = 400, StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor };
        var copy = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Text = _log.Text, BackColor = Color.FromArgb(18, 20, 24), ForeColor = ForeColor };
        UiScale.Apply(f); f.Controls.Add(copy); f.Show(this);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); _log.Dispose(); }
    private sealed class AppSettings { public int Port { get; set; } = 5078; public string? ServerId { get; set; } public List<string> MediaFolders { get; set; } = new(); public List<string> PersonalFolders { get; set; } = new(); public Dictionary<string, string> AllowedTvs { get; set; } = new(); }
}
