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
        int gap = Glyph.Length > 0 ? 12 : 0;
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
        e.Graphics.FillEllipse(brush, 2, (Height - 14) / 2, 14, 14);
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
            if (stream is not null) { using var icon = new Icon(stream, new Size(256, 256)); _icon = KeyOutDarkBackground(icon.ToBitmap()); }
        }
        catch { }
    }

    // The app icon sits on a near-black tile; fade those pixels out so the artwork blends into any theme.
    private static Bitmap KeyOutDarkBackground(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                var c = source.GetPixel(x, y);
                double keep = Math.Clamp((Math.Max(c.R, Math.Max(c.G, c.B)) - 24) / 48.0, 0, 1);
                result.SetPixel(x, y, Color.FromArgb((int)(c.A * keep), c.R, c.G, c.B));
            }
        source.Dispose();
        return result;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        int size = Math.Min(Height, 112);
        int top = (Height - size) / 2;
        if (_icon is not null) g.DrawImage(_icon, new Rectangle(0, top, size, size));
        int textX = size + 14;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        using var wordFont = new Font("Segoe UI", 40f, FontStyle.Bold, GraphicsUnit.Point);
        using var tagFont = new Font("Segoe UI Semibold", 16f, FontStyle.Regular, GraphicsUnit.Point);
        var cast = TextRenderer.MeasureText(g, "Cast", wordFont, Size.Empty, flags);
        var wordHeight = cast.Height;
        var tagHeight = TextRenderer.MeasureText(g, "Your home media server", tagFont, Size.Empty, flags).Height;
        int y = top + (size - (wordHeight + tagHeight)) / 2;
        TextRenderer.DrawText(g, "Cast", wordFont, new Point(textX, y), ForeColor, flags);
        TextRenderer.DrawText(g, "Box", wordFont, new Point(textX + cast.Width - 6, y), Amber, flags);
        var muted = Color.FromArgb((ForeColor.R * 7 + BackColor.R * 3) / 10, (ForeColor.G * 7 + BackColor.G * 3) / 10, (ForeColor.B * 7 + BackColor.B * 3) / 10);
        TextRenderer.DrawText(g, "Your home media server", tagFont, new Point(textX + 4, y + wordHeight - 4), muted, flags);
    }

    protected override void Dispose(bool disposing) { if (disposing) _icon?.Dispose(); base.Dispose(disposing); }
}

internal sealed class CastBoxForm : Form
{
    private readonly string _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CastBox");
    private readonly string _configPath;
    private readonly ListBox _folders = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _status = new StatusLabel { Text = "Server stopped" };
    private readonly Label _address = new() { AutoSize = false, Text = "Not running", Font = new Font("Segoe UI Semibold", 16), Padding = new Padding(18, 0, 0, 0) };
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535, Value = 5078, Width = 100, Font = new Font("Segoe UI", 11), BorderStyle = BorderStyle.FixedSingle };
    private readonly CheckBox _startWithWindows = new() { Text = "Start CastBox when I sign in to Windows", AutoSize = true };
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

    public CastBoxForm(bool launchInBackground)
    {
        _launchInBackground = launchInBackground;
        _configPath = Path.Combine(_dataDir, "settings.json");
        Directory.CreateDirectory(_dataDir);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CastBox/" + CurrentVersion);
        var mediaLogClient = new HttpMediaLogClient(http);
        _mediaLogStore = new MediaLogStore(Path.Combine(_dataDir, "medialog.json"), new DpapiProtector());
        _mediaLogService = new WatchSyncService(_mediaLogStore, mediaLogClient, new TvMazeClient(http), log: LogAny);
        _mediaLogConnector = new MediaLogConnector(_mediaLogStore, mediaLogClient, _mediaLogService);
        _mediaLogService.Changed += () => { if (IsHandleCreated && !IsDisposed) { try { BeginInvoke(new Action(UpdateMediaLogMenu)); } catch (ObjectDisposedException) { } } };
        Text = "CastBox Server";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; } catch { Icon = SystemIcons.Application; }
        Width = 1040; Height = 820; MinimumSize = new Size(900, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(25, 27, 32); ForeColor = Color.FromArgb(245, 241, 230);
        Font = new Font("Segoe UI", 10);
        BuildLayout(); LoadSettings(); ApplyTheme();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open CastBox", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        trayMenu.Items.Add("Stop server", null, async (_, _) => await StopServerAsync());
        trayMenu.Items.Add("Exit", null, async (_, _) => { _tray.Visible = false; await StopServerAsync(); Close(); });
        _tray.Text = "CastBox Server"; _tray.Icon = Icon; _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        FormClosing += async (_, e) => { if (e.CloseReason == CloseReason.UserClosing && _server is not null) { e.Cancel = true; Hide(); _tray.Visible = true; _tray.ShowBalloonTip(1500, "CastBox is running", "The media server continues in the background.", ToolTipIcon.Info); } else { _syncCts.Cancel(); _tray.Visible = false; await StopServerAsync(); } };
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
        header.Controls.Add(new BrandHeader { Dock = DockStyle.Fill, BackColor = BackColor, ForeColor = ForeColor });
        root.Controls.Add(header, 0, 0);

        var foldersPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = BackColor, Margin = new Padding(0, 4, 0, 8) };
        foldersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foldersPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        foldersPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        foldersPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foldersPanel.Controls.Add(new Label { Text = "Folders included in your TV library", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 10), Font = new Font("Segoe UI Semibold", 12) }, 0, 0);
        var folderButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        folderButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); folderButtons.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var add = MakeButton("Add folder"); add.Glyph = "\uE710"; add.Dock = DockStyle.Fill; add.Margin = new Padding(4, 2, 0, 2); add.Click += (_, _) => AddFolder();
        var remove = MakeButton("Remove folder"); remove.Glyph = "\uE738"; remove.Dock = DockStyle.Fill; remove.Margin = new Padding(4, 2, 0, 2);
        remove.Click += (_, _) => { if (_folders.SelectedItem is string selected) { _folders.Items.Remove(selected); SaveSettings(); Log("Removed folder: " + selected); } };
        folderButtons.Controls.Add(add, 0, 0); folderButtons.Controls.Add(remove, 0, 1);
        foldersPanel.Controls.Add(folderButtons, 1, 0);
        var folderBox = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 2), Padding = new Padding(16, 12, 16, 12), BackColor = Color.FromArgb(32, 34, 40) };
        _folders.Dock = DockStyle.Fill; _folders.Font = new Font("Segoe UI", 11);
        _folders.BackColor = Color.FromArgb(32, 34, 40); _folders.ForeColor = ForeColor; _folders.BorderStyle = BorderStyle.None;
        folderBox.Controls.Add(_folders);
        foldersPanel.Controls.Add(folderBox, 0, 1); foldersPanel.SetColumnSpan(folderBox, 2);
        root.Controls.Add(foldersPanel, 0, 1);

        var setup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        setup.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        setup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        setup.Controls.Add(new Label { Text = "Port", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 11) }, 0, 0);
        _port.Dock = DockStyle.None; _port.Anchor = AnchorStyles.Left | AnchorStyles.Right; _port.Margin = new Padding(0, 22, 8, 0); setup.Controls.Add(_port, 1, 0);
        var save = MakeButton("Save settings"); StyleButton(save, true); save.Dock = DockStyle.Fill; save.Margin = new Padding(0, 8, 20, 8);
        save.Click += (_, _) => { SaveSettings(); ApplyStartupSetting(); }; setup.Controls.Add(save, 2, 0);
        _startWithWindows.Dock = DockStyle.Fill; _startWithWindows.Margin = new Padding(0, 0, 0, 0); setup.Controls.Add(_startWithWindows, 3, 0);
        root.Controls.Add(setup, 0, 2);

        var serverPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        serverPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        serverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        StyleButton(_startStop, true); _startStop.Dock = DockStyle.Fill; _startStop.Margin = new Padding(0, 8, 14, 8);
        _startStop.Click += async (_, _) => { if (_server is null) await StartServerAsync(); else await StopServerAsync(); };
        StyleButton(_firewall, false); _firewall.Dock = DockStyle.Fill; _firewall.Margin = new Padding(0, 8, 14, 8); _firewall.Click += (_, _) => ConfigureFirewall();
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.Margin = new Padding(2, 0, 0, 0); _status.Font = new Font("Segoe UI Semibold", 12);
        serverPanel.Controls.Add(_startStop, 0, 0); serverPanel.Controls.Add(_firewall, 1, 0); serverPanel.Controls.Add(_status, 2, 0);
        root.Controls.Add(serverPanel, 0, 3);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Margin = new Padding(0, 4, 0, 0) };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(new Label { Text = "Connection address for your CastBox TV app", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 12) }, 0, 0);
        var addr = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        addr.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        addr.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); addr.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        var addrBox = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 14, 6), Padding = new Padding(2), BackColor = PanelSurface };
        _address.ForeColor = BrandAmber; _address.Dock = DockStyle.Fill; _address.TextAlign = ContentAlignment.MiddleLeft; _address.Margin = new Padding(0);
        addrBox.Controls.Add(_address);
        var copy = MakeButton("Copy address"); StyleButton(copy, true); copy.Glyph = "\uE8C8"; copy.Dock = DockStyle.Fill; copy.Margin = new Padding(0, 2, 0, 6);
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
        var b = new RoundedButton { Text = text, AutoSize = false, Height = 46, MinimumSize = new Size(100, 42),
            FlatStyle = FlatStyle.Flat, BackColor = ButtonSurface, ForeColor = BrandAmber,
            Font = new Font("Segoe UI Semibold", 10), Padding = new Padding(12, 7, 12, 7),
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
        b.Padding = new Padding(12, 7, 12, 7);
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

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder containing movies or TV episodes", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!_folders.Items.Cast<string>().Any(x => string.Equals(x, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase))) _folders.Items.Add(dialog.SelectedPath);
        SaveSettings(); Log("Added folder: " + dialog.SelectedPath);
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
                foreach (var f in cfg.MediaFolders.Where(Directory.Exists)) _folders.Items.Add(f);
                _startWithWindows.Checked = IsStartupEnabled();
            }
            else _startWithWindows.Checked = IsStartupEnabled();
        }
        catch (Exception ex) { Log("Could not load settings: " + ex.Message); }
        finally { _loading = false; }
        // A permanent ID lets the TV app find this PC again after its network address changes.
        if (string.IsNullOrEmpty(_serverId)) { _serverId = Guid.NewGuid().ToString("N"); SaveSettings(); }
    }
    private void SaveSettings()
    {
        if (_loading) return;
        try
        {
            var cfg = new AppSettings { Port = (int)_port.Value, ServerId = _serverId, MediaFolders = _folders.Items.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            Log("Settings saved.");
            if (_server is not null) Log("Restart the server to apply the changed port or folder list.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't save settings: " + ex.Message, "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task StartServerAsync()
    {
        if (_server is not null) return;
        SaveSettings();
        if (_folders.Items.Count == 0) { MessageBox.Show(this, "Add at least one movie folder first.", "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try
        {
            var port = (int)_port.Value;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(CastBoxForm).Assembly.FullName, ContentRootPath = AppContext.BaseDirectory, Args = Array.Empty<string>() });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, port));
            builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));
            var app = builder.Build(); app.UseCors();
            var configuredFolders = _folders.Items.Cast<string>().ToList();
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
            app.MapGet("/api/health", () => Results.Ok(new { status = "ok", name = "CastBox", folders = configuredFolders.Count, serverId = _serverId }));
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
        catch (Exception ex) { _server = null; MessageBox.Show(this, "Couldn't start the server. Check whether the port is already in use.\n\n" + ex.Message, "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Error); Log("Start failed: " + ex); }
    }

    private async Task StopServerAsync()
    {
        if (_server is null) return;
        try { await _server.StopAsync(); await _server.DisposeAsync(); } catch { }
        _server = null; _status.Text = "Server stopped"; _status.ForeColor = ForeColor; _startStop.Text = "Start server"; _startStop.Glyph = "\uE768"; StyleButton(_startStop, true); _address.Text = "Not running"; Log("Server stopped.");
    }

    // Change this to your GitHub owner/repository before publishing releases.
    private const string GitHubRepository = "cosmintoncean/castbox-media-server";
    private const string CurrentVersion = "0.3.0";

    private async Task CheckForUpdatesAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CastBox-Updater/1.0");
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
                    "CastBox updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString()?.TrimStart('v', 'V') : null;
            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : "";
            if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag, out var latest))
            {
                MessageBox.Show(this, "The latest GitHub release has no valid version tag. Use tags such as v0.1.1.", "CastBox updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Version.TryParse(CurrentVersion, out var current)) current = new Version(0, 1, 0);
            if (latest <= current)
            {
                MessageBox.Show(this, $"You're up to date. Installed version: {CurrentVersion}.", "CastBox updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string? installerUrl = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                    if (asset.TryGetProperty("name", out var name) && string.Equals(name.GetString(), "CastBox-Setup.exe", StringComparison.OrdinalIgnoreCase) && asset.TryGetProperty("browser_download_url", out var url))
                    { installerUrl = url.GetString(); break; }

            if (string.IsNullOrWhiteSpace(installerUrl))
            {
                if (MessageBox.Show(this, $"Version {tag} is available, but the release doesn't contain CastBox-Setup.exe.\n\nOpen the release page to download it manually?", "CastBox updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo($"https://github.com/{GitHubRepository}/releases/latest") { UseShellExecute = true });
                return;
            }

            var prompt = $"CastBox {tag} is available (installed: {CurrentVersion}).\n\n{notes}\n\nDownload and launch the installer? CastBox will close during installation.";
            if (MessageBox.Show(this, prompt, "CastBox update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

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
            MessageBox.Show(this, "Couldn't check or download updates. Check your internet connection and GitHub release configuration.\n\n" + ex.Message, "CastBox updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Log("Update check failed: " + ex.Message);
        }
    }

    private void ConfigureFirewall()
    {
        try
        {
            var exe = Application.ExecutablePath;
            var command = $"advfirewall firewall add rule name=\"CastBox Media Server TCP {(int)_port.Value}\" dir=in action=allow protocol=TCP localport={(int)_port.Value} profile=private";
            var psi = new ProcessStartInfo("netsh.exe", command) { UseShellExecute = true, Verb = "runas" };
            Process.Start(psi);
            Log("Requested an administrator approval to add the private-network firewall rule for TCP " + (int)_port.Value + ".");
            MessageBox.Show(this, "Windows may ask for administrator approval. Allow the CastBox port on private networks only.", "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Firewall setup couldn't be started: " + ex.Message, "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
            Log(_startWithWindows.Checked ? "CastBox will start when you sign in to Windows." : "Automatic startup disabled.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't update startup setting: " + ex.Message, "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
            if (MessageBox.Show(this, "Forget which MediaLog titles you confirmed and the show lookups CastBox saved? They will be worked out again next time.", "CastBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) _mediaLogService.ForgetSavedChoices();
        };
        _mlDisconnect = new ToolStripMenuItem("Disconnect");
        _mlDisconnect.Click += async (_, _) => await DisconnectMediaLogAsync();
        _mediaLogMenu.DropDownItems.AddRange(new ToolStripItem[] { _mlStatus, new ToolStripSeparator(), _mlConnect, _mlConfirm, _mlForget, new ToolStripSeparator(), _mlDisconnect });
        menu.Items.Add(_mediaLogMenu);
    }

    private async Task DisconnectMediaLogAsync()
    {
        if (MessageBox.Show(this, "Disconnect CastBox from MediaLog? Watches still waiting to be sent are forgotten. You can connect again any time.", "CastBox", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await _mediaLogConnector.DisconnectAsync(CancellationToken.None); }
        catch (MediaLogException ex) { MessageBox.Show(this, "CastBox is disconnected here, but MediaLog couldn't be told: " + ex.Message, "CastBox", MessageBoxButtons.OK, MessageBoxIcon.Information); }
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
        var f = new Form { Text = "CastBox server log", Width = 700, Height = 400, StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor };
        var copy = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Text = _log.Text, BackColor = Color.FromArgb(18, 20, 24), ForeColor = ForeColor };
        f.Controls.Add(copy); f.Show(this);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { base.OnFormClosed(e); _log.Dispose(); }
    private sealed class AppSettings { public int Port { get; set; } = 5078; public string? ServerId { get; set; } public List<string> MediaFolders { get; set; } = new(); }
}
