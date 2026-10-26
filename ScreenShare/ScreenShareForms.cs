using CastBox.Chromecast;
using CastBox.MediaLog;

namespace CastBox.ScreenShare;

/// <summary>The question shown on the PC when a TV asks to watch a screen. Allowing shows the whole screen to whoever is in front of the TV, so it is never answered for the person.</summary>
internal sealed class ShareApprovalDialog : Form
{
    private readonly ScreenShareService _service;
    private readonly ShareRequest _request;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public ConsentChoice Choice { get; private set; } = ConsentChoice.Deny;

    public ShareApprovalDialog(ScreenShareService service, ShareRequest request)
    {
        _service = service; _request = request;
        var screen = service.GetScreen(request.Screen);
        MediaLogUi.Style(this, "CastBox screen sharing", new Size(540, 250));
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;

        var what = screen is null ? "a screen" : $"{screen.Name} ({screen.Width}×{screen.Height})";
        var title = MediaLogUi.Label($"“{request.TvName}” wants to watch {what} of this PC", bold: true); title.SetBounds(24, 20, 492, 54);
        var body = MediaLogUi.Label("Whoever is watching sees everything on that screen, including notifications and anything private. Only allow a TV you recognise.", muted: true); body.SetBounds(24, 80, 492, 64);
        var once = MediaLogUi.Button("Allow once", true, (_, _) => Answer(ConsentChoice.AllowOnce)); once.SetBounds(24, 168, 130, 44);
        var always = MediaLogUi.Button("Always allow this TV", false, (_, _) => Answer(ConsentChoice.AlwaysAllow)); always.SetBounds(166, 168, 200, 44);
        var deny = MediaLogUi.Button("Don't allow", false, (_, _) => Answer(ConsentChoice.Deny)); deny.SetBounds(378, 168, 138, 44);
        Controls.AddRange(new Control[] { title, body, once, always, deny });
        AcceptButton = once; CancelButton = deny;

        // The TV gives up after a minute; the question goes away with it (or when answered elsewhere).
        _timer.Tick += (_, _) => { if (_service.Poll(_request.Id)?.Status != ShareStatus.Pending) { Choice = ConsentChoice.Deny; DialogResult = DialogResult.Cancel; Close(); } };
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Answer(ConsentChoice choice) { Choice = choice; DialogResult = DialogResult.OK; Close(); }
}

/// <summary>One place a screen can be sent: a Chromecast found on the network, and/or a TV with the CastBox app open.</summary>
public sealed record ShareTarget(string Name, CastDevice? Device, TvInfo? App)
{
    /// <summary>With the CastBox app open the screen is pushed to it (low delay); otherwise it goes through Google Cast.</summary>
    public string Route => App is not null ? "CastBox is open: instant, low delay" : "Google Cast: a few seconds' delay";
    public override string ToString() => $"{Name}      {Route}";

    /// <summary>Chromecasts first, matched with an open CastBox app by network address; then other TVs that have the app open.</summary>
    public static IReadOnlyList<ShareTarget> Combine(IReadOnlyList<CastDevice> devices, IReadOnlyList<TvInfo> apps)
    {
        var list = new List<ShareTarget>();
        var used = new HashSet<string>();
        foreach (var d in devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var app = apps.FirstOrDefault(a => a.Address is not null && a.Address == d.Address);
            if (app is not null) used.Add(app.Id);
            list.Add(new ShareTarget(d.Name, d, app));
        }
        foreach (var a in apps.Where(a => !used.Contains(a.Id)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) list.Add(new ShareTarget(a.Name, null, a));
        return list;
    }
}

/// <summary>"Share screen": send one of this PC's screens to a Chromecast or a TV with CastBox open, see what is being watched, stop it, and forget TVs.</summary>
internal sealed class ShareScreenDialog : Form
{
    private readonly ScreenShareService _service;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CastDevice>>> _discover;
    private readonly Func<CastDevice, int, CancellationToken, Task> _castTo;
    private readonly ListBox _screens = Make();
    private readonly ListBox _targets = Make();
    private readonly ListBox _sessions = Make();
    private readonly ListBox _allowed = Make();
    private readonly Label _status = MediaLogUi.Label("", muted: true);
    private readonly Label _searching = MediaLogUi.Label("Looking for Chromecasts…", muted: true);
    private readonly RoundedButton _share;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<CastDevice> _devices = Array.Empty<CastDevice>();
    private IReadOnlyList<ShareTarget> _targetList = Array.Empty<ShareTarget>();
    private IReadOnlyList<ShareSession> _sessionList = Array.Empty<ShareSession>();
    private List<KeyValuePair<string, string>> _allowedList = new();
    private DateTime _lastDiscovery = DateTime.MinValue;
    private bool _discovering, _busy;

    private static ListBox Make() => new()
    {
        BackColor = MediaLogUi.Panel, ForeColor = MediaLogUi.Text, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = new Font("Segoe UI", 10.5f),
    };

    public ShareScreenDialog(ScreenShareService service, bool ffmpegAvailable,
        Func<CancellationToken, Task<IReadOnlyList<CastDevice>>> discover, Func<CastDevice, int, CancellationToken, Task> castTo)
    {
        _service = service; _discover = discover; _castTo = castTo;
        MediaLogUi.Style(this, "Share screen", new Size(720, 660));

        var l1 = MediaLogUi.Label("Screen to share", bold: true); l1.SetBounds(24, 16, 300, 24);
        _screens.SetBounds(24, 44, 290, 100);
        var l2 = MediaLogUi.Label("Send it to", bold: true); l2.SetBounds(338, 16, 200, 24);
        _searching.SetBounds(520, 18, 176, 22); _searching.TextAlign = ContentAlignment.MiddleRight;
        _targets.SetBounds(338, 44, 358, 100);
        var hint = MediaLogUi.Label(ffmpegAvailable
            ? "Chromecasts on your network are listed. If CastBox is open on one, the screen opens there instantly; otherwise it is sent through Google Cast, which works with nothing open but has a few seconds' delay."
            : "FFmpeg was not found, so this PC can't share its screen yet. Reinstall CastBox or put ffmpeg.exe in the CastBox folder's ffmpeg subfolder.", muted: ffmpegAvailable);
        hint.SetBounds(24, 152, 672, 46);
        _share = MediaLogUi.Button("Share", true, async (_, _) => await ShareAsync()); _share.SetBounds(24, 204, 150, 42); _share.Enabled = ffmpegAvailable;
        _status.SetBounds(186, 206, 510, 40);

        var l3 = MediaLogUi.Label("Being watched now", bold: true); l3.SetBounds(24, 262, 300, 24);
        _sessions.SetBounds(24, 290, 672, 80);
        var stop = MediaLogUi.Button("Stop selected", false, (_, _) => { if (_sessions.SelectedIndex >= 0 && _sessions.SelectedIndex < _sessionList.Count) _service.Stop(_sessionList[_sessions.SelectedIndex].Id); Refresh2(); }); stop.SetBounds(24, 378, 150, 40);
        var stopAll = MediaLogUi.Button("Stop everything", false, (_, _) => { _service.StopAll(); Refresh2(); }); stopAll.SetBounds(186, 378, 160, 40);

        var l4 = MediaLogUi.Label("TVs you always allow (they ask from the TV app)", bold: true); l4.SetBounds(24, 438, 500, 24);
        _allowed.SetBounds(24, 466, 672, 90);
        var forget = MediaLogUi.Button("Forget selected TV", false, (_, _) =>
        {
            if (_allowed.SelectedIndex >= 0 && _allowed.SelectedIndex < _allowedList.Count) _service.Forget(_allowedList[_allowed.SelectedIndex].Key);
            Refresh2();
        });
        forget.SetBounds(24, 566, 190, 40);
        var close = MediaLogUi.Button("Close", false, (_, _) => Close()); close.SetBounds(566, 566, 130, 40);

        Controls.AddRange(new Control[] { l1, _screens, l2, _searching, _targets, hint, _share, _status, l3, _sessions, stop, stopAll, l4, _allowed, forget, close });
        CancelButton = close;
        Refresh2();
        if (_screens.Items.Count > 0) _screens.SelectedIndex = Math.Max(0, _service.Screens.ToList().FindIndex(s => s.Primary));
        _timer.Tick += (_, _) => Refresh2();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Dispose(); _cts.Cancel(); };
        Shown += (_, _) => Refresh2();
    }

    private static void Fill(ListBox box, IEnumerable<string> lines, string whenEmpty)
    {
        var selected = box.SelectedIndex;
        box.BeginUpdate(); box.Items.Clear();
        var any = false;
        foreach (var line in lines) { box.Items.Add(line); any = true; }
        if (!any) box.Items.Add(whenEmpty);
        if (selected >= 0 && selected < box.Items.Count) box.SelectedIndex = selected;
        box.EndUpdate();
    }

    private void Refresh2()
    {
        // Look for Chromecasts now and then (it takes a couple of seconds, so never on the window's own thread).
        if (!_discovering && DateTime.UtcNow - _lastDiscovery > TimeSpan.FromSeconds(10))
        {
            _discovering = true; _lastDiscovery = DateTime.UtcNow; _searching.Visible = true;
            _ = Task.Run(async () =>
            {
                IReadOnlyList<CastDevice> found;
                try { found = await _discover(_cts.Token); } catch (Exception) { found = _devices; }
                try { BeginInvoke(new Action(() => { _devices = found; _discovering = false; _searching.Visible = false; Refresh2(); })); } catch (Exception) { }
            });
        }
        var screens = _service.Screens;
        Fill(_screens, screens.Select(s => $"{s.Name}   {s.Width}×{s.Height}"), "No screens found");
        _targetList = ShareTarget.Combine(_devices, _service.RecentTvs());
        Fill(_targets, _targetList.Select(t => t.ToString()), _discovering ? "Looking…" : "No Chromecast found. Check it is on and on this network.");
        _sessionList = _service.Sessions;
        Fill(_sessions, _sessionList.Select(s => $"{s.TvName} is watching {_service.GetScreen(s.Screen)?.Name ?? "a screen"}" + (s.TvId.StartsWith("cast:") ? "   (Google Cast)" : "")), "Nobody is watching");
        _allowedList = _service.AllowedTvs.ToList();
        Fill(_allowed, _allowedList.Select(a => a.Value), "None");
    }

    private async Task ShareAsync()
    {
        if (_busy) return;
        var screenIndex = _screens.SelectedIndex;
        if (screenIndex < 0 || screenIndex >= _service.Screens.Count) { SetStatus("Choose a screen first.", true); return; }
        if (_targets.SelectedIndex < 0 || _targets.SelectedIndex >= _targetList.Count) { SetStatus("Choose where to send it.", true); return; }
        var screen = _service.Screens[screenIndex].Index;
        var target = _targetList[_targets.SelectedIndex];

        if (target.App is not null)
        {
            SetStatus(_service.Push(target.App.Id, screen) ? $"Sent to {target.Name}. It opens in a few seconds." : "Couldn't send it. Try again.", !_service.Screens.Any());
            return;
        }
        _busy = true; _share.Enabled = false;
        SetStatus($"Starting Google Cast on {target.Name}… (about 3 seconds, up to 15 the first time)", false);
        try
        {
            await _castTo(target.Device!, screen, _cts.Token);
            SetStatus($"Casting to {target.Name}. Use Stop selected to end it.", false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex is CastException ? ex.Message : "Couldn't cast: " + ex.Message, true); }
        finally { _busy = false; if (!IsDisposed) { _share.Enabled = true; Refresh2(); } }
    }

    private void SetStatus(string text, bool error) { _status.Text = text; _status.ForeColor = error ? MediaLogUi.Error : MediaLogUi.Muted; }
}
