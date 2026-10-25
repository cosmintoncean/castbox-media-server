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

/// <summary>"Share screen": send one of this PC's screens to a TV whose app is open, see what is being watched, stop it, and forget TVs.</summary>
internal sealed class ShareScreenDialog : Form
{
    private readonly ScreenShareService _service;
    private readonly ListBox _screens = Make();
    private readonly ListBox _tvs = Make();
    private readonly ListBox _sessions = Make();
    private readonly ListBox _allowed = Make();
    private readonly Label _status = MediaLogUi.Label("", muted: true);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private IReadOnlyList<TvInfo> _tvList = Array.Empty<TvInfo>();
    private IReadOnlyList<ShareSession> _sessionList = Array.Empty<ShareSession>();
    private List<KeyValuePair<string, string>> _allowedList = new();

    private static ListBox Make() => new()
    {
        BackColor = MediaLogUi.Panel, ForeColor = MediaLogUi.Text, BorderStyle = BorderStyle.None, IntegralHeight = false, Font = new Font("Segoe UI", 10.5f),
    };

    public ShareScreenDialog(ScreenShareService service, bool ffmpegAvailable)
    {
        _service = service;
        MediaLogUi.Style(this, "Share screen", new Size(700, 640));

        var l1 = MediaLogUi.Label("Screen to share", bold: true); l1.SetBounds(24, 16, 300, 24);
        _screens.SetBounds(24, 44, 310, 96);
        var l2 = MediaLogUi.Label("Send it to this TV", bold: true); l2.SetBounds(358, 16, 318, 24);
        _tvs.SetBounds(358, 44, 318, 96);
        var hint = MediaLogUi.Label(ffmpegAvailable
            ? "A TV appears here while the CastBox app is open on it. You can also start from the TV with the Cast button next to this PC."
            : "FFmpeg was not found, so this PC can't share its screen yet. Reinstall CastBox or put ffmpeg.exe in the CastBox folder's ffmpeg subfolder.", muted: !ffmpegAvailable ? false : true);
        hint.SetBounds(24, 148, 652, 44);
        var share = MediaLogUi.Button("Share to TV", true, (_, _) => Share()); share.SetBounds(24, 198, 150, 42); share.Enabled = ffmpegAvailable;
        _status.SetBounds(186, 204, 490, 34);

        var l3 = MediaLogUi.Label("Being watched now", bold: true); l3.SetBounds(24, 256, 300, 24);
        _sessions.SetBounds(24, 284, 652, 80);
        var stop = MediaLogUi.Button("Stop selected", false, (_, _) => { if (_sessions.SelectedIndex >= 0 && _sessions.SelectedIndex < _sessionList.Count) _service.Stop(_sessionList[_sessions.SelectedIndex].Id); Refresh2(); }); stop.SetBounds(24, 372, 150, 40);
        var stopAll = MediaLogUi.Button("Stop everything", false, (_, _) => { _service.StopAll(); Refresh2(); }); stopAll.SetBounds(186, 372, 160, 40);

        var l4 = MediaLogUi.Label("TVs you always allow", bold: true); l4.SetBounds(24, 432, 400, 24);
        _allowed.SetBounds(24, 460, 652, 90);
        var forget = MediaLogUi.Button("Forget selected TV", false, (_, _) =>
        {
            if (_allowed.SelectedIndex >= 0 && _allowed.SelectedIndex < _allowedList.Count) _service.Forget(_allowedList[_allowed.SelectedIndex].Key);
            Refresh2();
        });
        forget.SetBounds(24, 560, 190, 40);
        var close = MediaLogUi.Button("Close", false, (_, _) => Close()); close.SetBounds(546, 560, 130, 40);

        Controls.AddRange(new Control[] { l1, _screens, l2, _tvs, hint, share, _status, l3, _sessions, stop, stopAll, l4, _allowed, forget, close });
        CancelButton = close;
        Refresh2();
        if (_screens.Items.Count > 0) _screens.SelectedIndex = Math.Max(0, _service.Screens.ToList().FindIndex(s => s.Primary));
        _timer.Tick += (_, _) => Refresh2();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
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
        var screens = _service.Screens;
        Fill(_screens, screens.Select(s => $"{s.Name}   {s.Width}×{s.Height}"), "No screens found");
        _tvList = _service.RecentTvs();
        Fill(_tvs, _tvList.Select(t => t.Name), "No TV has the CastBox app open");
        _sessionList = _service.Sessions;
        Fill(_sessions, _sessionList.Select(s => $"{s.TvName} is watching {_service.GetScreen(s.Screen)?.Name ?? "a screen"}"), "Nobody is watching");
        _allowedList = _service.AllowedTvs.ToList();
        Fill(_allowed, _allowedList.Select(a => a.Value), "None");
    }

    private void Share()
    {
        var screenIndex = _screens.SelectedIndex;
        if (screenIndex < 0 || screenIndex >= _service.Screens.Count) { SetStatus("Choose a screen first.", true); return; }
        if (_tvs.SelectedIndex < 0 || _tvs.SelectedIndex >= _tvList.Count) { SetStatus("Choose a TV. Open the CastBox app on the TV if none is listed.", true); return; }
        var tv = _tvList[_tvs.SelectedIndex];
        if (_service.Push(tv.Id, _service.Screens[screenIndex].Index)) SetStatus($"Sent to {tv.Name}. It opens on the TV in a few seconds.", false);
        else SetStatus("Couldn't send it. Try again.", true);
    }

    private void SetStatus(string text, bool error) { _status.Text = text; _status.ForeColor = error ? MediaLogUi.Error : MediaLogUi.Muted; }
}
