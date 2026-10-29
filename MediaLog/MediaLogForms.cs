using System.Diagnostics;

namespace CastBox.MediaLog;

/// <summary>Shared look for the MediaLog dialogs (the main window's charcoal and amber).</summary>
internal static class MediaLogUi
{
    public static readonly Color Back = Color.FromArgb(25, 27, 32);
    public static readonly Color Panel = Color.FromArgb(32, 34, 40);
    public static readonly Color Text = Color.FromArgb(245, 241, 230);
    public static readonly Color Muted = Color.FromArgb(160, 163, 172);
    public static readonly Color Amber = Color.FromArgb(255, 177, 57);
    public static readonly Color Error = Color.FromArgb(255, 150, 130);

    public static void Style(Form form, string title, Size size)
    {
        UiScale.Apply(form);
        form.Text = title;
        form.ClientSize = size;
        form.StartPosition = FormStartPosition.CenterParent;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false; form.MinimizeBox = false; form.ShowInTaskbar = false;
        form.BackColor = Back; form.ForeColor = Text; form.Font = new Font("Segoe UI", 10);
    }

    public static Label Label(string text, bool muted = false, bool bold = false) => new()
    {
        Text = text, AutoSize = false, ForeColor = muted ? Muted : Text,
        Font = new Font("Segoe UI" + (bold ? " Semibold" : ""), bold ? 11 : 10), BackColor = Color.Transparent,
    };

    public static TextBox Box(string text = "") => new() { Text = text, BackColor = Panel, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle };

    public static RoundedButton Button(string text, bool primary, EventHandler onClick)
    {
        var b = new RoundedButton { Text = text, Primary = primary, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 10) };
        b.BackColor = primary ? Amber : Color.FromArgb(50, 38, 22);
        b.ForeColor = primary ? Back : Amber;
        b.FlatAppearance.BorderSize = 0;
        b.Click += onClick;
        return b;
    }
}

/// <summary>"Connect to MediaLog": address, then a code to approve in the browser. See docs/medialog-sync-design.md.</summary>
internal sealed class MediaLogConnectDialog : Form
{
    private readonly MediaLogConnector _connector;
    private readonly TextBox _address;
    private readonly Label _status = MediaLogUi.Label("", muted: true);
    private readonly Label _code = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Consolas", 30, FontStyle.Bold), ForeColor = MediaLogUi.Amber, Visible = false, BackColor = Color.Transparent };
    private readonly RoundedButton _connect;
    private readonly RoundedButton _openAgain;
    private readonly RoundedButton _copy;
    private readonly RoundedButton _close;
    private readonly CancellationTokenSource _cts = new();
    private string? _connectUrl;
    private const string DefaultAddress = "https://thinglog.netlify.app";

    public MediaLogConnectDialog(MediaLogConnector connector, string? savedAddress)
    {
        _connector = connector;
        MediaLogUi.Style(this, "Connect to MediaLog", new Size(560, 372));

        var title = MediaLogUi.Label("MediaLog address", bold: true); title.SetBounds(24, 20, 500, 24);
        _address = MediaLogUi.Box(string.IsNullOrWhiteSpace(savedAddress) ? DefaultAddress : savedAddress); _address.SetBounds(24, 48, 512, 28);
        var hint = MediaLogUi.Label("Check the address, then press Connect. NestCast shows a code and opens MediaLog so you can approve this PC.", muted: true); hint.SetBounds(24, 82, 512, 40);

        _code.SetBounds(24, 126, 512, 56);
        _status.SetBounds(24, 188, 512, 92);

        _connect = MediaLogUi.Button("Connect", true, async (_, _) => await ConnectAsync()); _connect.SetBounds(24, 304, 130, 42);
        _openAgain = MediaLogUi.Button("Open page", false, (_, _) => OpenPage()); _openAgain.SetBounds(160, 304, 130, 42); _openAgain.Visible = false;
        _copy = MediaLogUi.Button("Copy link", false, (_, _) => CopyLink()); _copy.SetBounds(298, 304, 100, 42); _copy.Visible = false;
        _close = MediaLogUi.Button("Cancel", false, (_, _) => Close()); _close.SetBounds(406, 304, 130, 42);
        Controls.AddRange(new Control[] { title, _address, hint, _code, _status, _connect, _openAgain, _copy, _close });
        AcceptButton = _connect;
        // The address is already filled in, so the next step is the button; Enter works too.
        Shown += (_, _) => { if (_address.Text.Length > 0) ActiveControl = _connect; };
        FormClosing += (_, _) => _cts.Cancel();
    }

    private void SetStatus(string text, bool error = false) { _status.Text = text; _status.ForeColor = error ? MediaLogUi.Error : MediaLogUi.Muted; }

    private void CopyLink()
    {
        if (_connectUrl is null) return;
        try { Clipboard.SetText(_connectUrl); SetStatus("Link copied. Paste it into your browser's address bar."); }
        catch (Exception) { SetStatus("Couldn't copy it. The link is " + _connectUrl); }
    }

    private void OpenPage()
    {
        if (_connectUrl is null) return;
        try { Process.Start(new ProcessStartInfo(_connectUrl) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Couldn't open the browser. Go to " + _connectUrl + " yourself. (" + ex.Message + ")", error: true); }
    }

    private async Task ConnectAsync()
    {
        _connect.Enabled = false; _address.Enabled = false; _code.Visible = false; _openAgain.Visible = false; _copy.Visible = false;
        SetStatus("Checking the address…");
        try
        {
            var (discovery, pairing, url) = await _connector.BeginAsync(_address.Text, "NestCast on " + Environment.MachineName, _cts.Token);
            _connectUrl = url;
            _code.Text = pairing.UserCode; _code.Visible = true; _openAgain.Visible = true; _copy.Visible = true;
            SetStatus("Approve “NestCast on " + Environment.MachineName + "” on the MediaLog page that just opened.\nPage didn't open? Go to " + url.Substring(0, url.LastIndexOf('/')) + " and type the code above, or use Copy link.");
            OpenPage();

            switch (await _connector.WaitAsync(discovery, pairing, _cts.Token))
            {
                case PairStatus.Approved:
                    _code.Visible = false; _openAgain.Visible = false; _copy.Visible = false;
                    SetStatus("Connected. NestCast will now mark episodes and movies you finish on the TV as watched in MediaLog.");
                    _close.Text = "Done";
                    return;
                case PairStatus.Denied:
                    SetStatus("You refused the connection in MediaLog.", error: true);
                    break;
                default:
                    SetStatus("The code expired before it was approved. Try again.", error: true);
                    break;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (MediaLogException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception ex) { SetStatus("Something went wrong: " + ex.Message, error: true); }

        _code.Visible = false; _openAgain.Visible = false; _copy.Visible = false;
        _connect.Enabled = true; _address.Enabled = true;
    }
}

/// <summary>The watches that need the person: pick the right title, say what a file is, or skip it.</summary>
internal sealed class MediaLogConfirmDialog : Form
{
    private readonly WatchSyncService _service;
    private readonly ListBox _items = new() { BackColor = MediaLogUi.Panel, ForeColor = MediaLogUi.Text, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly Label _reason = MediaLogUi.Label("", muted: true);
    private readonly ListBox _candidates = new() { BackColor = MediaLogUi.Panel, ForeColor = MediaLogUi.Text, BorderStyle = BorderStyle.None, IntegralHeight = false };
    private readonly RoundedButton _use;
    private readonly RoundedButton _skipThis;
    private readonly RoundedButton _skipTitle;
    private readonly RadioButton _asMovie = new() { Text = "Movie", AutoSize = true, ForeColor = MediaLogUi.Text, BackColor = Color.Transparent };
    private readonly RadioButton _asEpisode = new() { Text = "TV episode", AutoSize = true, ForeColor = MediaLogUi.Text, BackColor = Color.Transparent, Checked = true };
    private readonly TextBox _title = MediaLogUi.Box();
    private readonly TextBox _year = MediaLogUi.Box();
    private readonly TextBox _season = MediaLogUi.Box();
    private readonly TextBox _episode = MediaLogUi.Box();
    private List<QueuedWatch> _list = new();
    private bool _refreshing;

    public MediaLogConfirmDialog(WatchSyncService service)
    {
        _service = service;
        MediaLogUi.Style(this, "Needs confirmation", new Size(860, 520));
        MaximizeBox = false;

        var heading = MediaLogUi.Label("Watches NestCast couldn't settle by itself", bold: true); heading.SetBounds(20, 14, 600, 24);
        _items.SetBounds(20, 46, 330, 380);
        _reason.SetBounds(370, 46, 470, 40);
        var candidatesLabel = MediaLogUi.Label("Pick the right title", muted: true); candidatesLabel.SetBounds(370, 90, 470, 20);
        _candidates.SetBounds(370, 112, 470, 120);
        _use = MediaLogUi.Button("Use this title", true, (_, _) => UseCandidate()); _use.SetBounds(370, 240, 150, 38);
        _skipThis = MediaLogUi.Button("Don't sync this one", false, (_, _) => Act(id => _service.DontSyncThis(id))); _skipThis.SetBounds(530, 240, 170, 38);
        _skipTitle = MediaLogUi.Button("Never sync this title", false, (_, _) => Act(id => _service.DontSyncTitle(id))); _skipTitle.SetBounds(710, 240, 130, 38);
        _skipTitle.Font = new Font("Segoe UI Semibold", 9);

        var manual = MediaLogUi.Label("Or say what it is", muted: true); manual.SetBounds(370, 296, 470, 20);
        _asEpisode.SetBounds(370, 320, 120, 24); _asMovie.SetBounds(500, 320, 90, 24);
        var tl = MediaLogUi.Label("Title"); tl.SetBounds(370, 352, 60, 22); _title.SetBounds(430, 350, 410, 28);
        var yl = MediaLogUi.Label("Year"); yl.SetBounds(370, 388, 60, 22); _year.SetBounds(430, 386, 70, 28);
        var sl = MediaLogUi.Label("Season"); sl.SetBounds(520, 388, 60, 22); _season.SetBounds(580, 386, 50, 28);
        var el = MediaLogUi.Label("Episode"); el.SetBounds(645, 388, 62, 22); _episode.SetBounds(710, 386, 50, 28);
        var save = MediaLogUi.Button("Save and send", false, (_, _) => SaveManual()); save.SetBounds(370, 428, 170, 38);
        var close = MediaLogUi.Button("Close", false, (_, _) => Close()); close.SetBounds(740, 470, 100, 38);

        Controls.AddRange(new Control[] { heading, _items, _reason, candidatesLabel, _candidates, _use, _skipThis, _skipTitle, manual, _asEpisode, _asMovie, tl, _title, yl, _year, sl, _season, el, _episode, save, close });
        _items.SelectedIndexChanged += (_, _) => ShowSelected();
        _asMovie.CheckedChanged += (_, _) => { _season.Enabled = _episode.Enabled = !_asMovie.Checked; };
        _service.Changed += OnChanged;
        FormClosed += (_, _) => _service.Changed -= OnChanged;
        Refresh_();
    }

    private void OnChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Refresh_)); }

    private QueuedWatch? Selected => _items.SelectedIndex >= 0 && _items.SelectedIndex < _list.Count ? _list[_items.SelectedIndex] : null;

    private void Refresh_()
    {
        var keep = Selected?.EventId;
        _refreshing = true;
        _list = _service.Snapshot().NeedsConfirmationItems.OrderBy(q => q.WatchedAtUtc).ToList();
        _items.Items.Clear();
        foreach (var item in _list) _items.Items.Add(item.Description);
        _items.SelectedIndex = _list.Count == 0 ? -1 : Math.Max(0, _list.FindIndex(q => q.EventId == keep));
        _refreshing = false;
        ShowSelected();
        if (_list.Count == 0) Text = "Needs confirmation: nothing waiting";
        else Text = $"Needs confirmation ({_list.Count})";
    }

    private void ShowSelected()
    {
        if (_refreshing) return;
        var item = Selected;
        _candidates.Items.Clear();
        var has = item is not null;
        _reason.Text = item is null ? "Nothing needs your attention." : item.Reason ?? "";
        foreach (var c in item?.Candidates ?? new List<Candidate>()) _candidates.Items.Add(c.Year is { } y ? $"{c.Title} ({y})" : c.Title);
        if (_candidates.Items.Count > 0) _candidates.SelectedIndex = 0;
        _use.Enabled = _candidates.Items.Count > 0;
        _skipThis.Enabled = _skipTitle.Enabled = has;
        if (item is not null)
        {
            _title.Text = item.Title; _year.Text = item.Year?.ToString() ?? "";
            _season.Text = item.Season?.ToString() ?? ""; _episode.Text = item.Episode?.ToString() ?? "";
            _asMovie.Checked = item.Kind == WatchKind.Movie; _asEpisode.Checked = item.Kind != WatchKind.Movie;
        }
    }

    private void Act(Action<string> action)
    {
        if (Selected is { } item) action(item.EventId);
    }

    private void UseCandidate()
    {
        if (Selected is { } item && _candidates.SelectedIndex >= 0 && _candidates.SelectedIndex < item.Candidates.Count)
            _service.Confirm(item.EventId, item.Candidates[_candidates.SelectedIndex]);
    }

    private void SaveManual()
    {
        if (Selected is not { } item) return;
        var kind = _asMovie.Checked ? WatchKind.Movie : WatchKind.Episode;
        int? Number(TextBox box) => int.TryParse(box.Text.Trim(), out var n) && n >= 0 ? n : null;
        if (string.IsNullOrWhiteSpace(_title.Text)) { MessageBox.Show(this, "Enter the title.", "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (kind == WatchKind.Episode && (Number(_season) is null || Number(_episode) is null)) { MessageBox.Show(this, "Enter the season and episode numbers.", "NestCast", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        _service.SetManual(item.EventId, kind, _title.Text, Number(_year), Number(_season), Number(_episode));
    }
}
