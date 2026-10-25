using CastBox.ScreenShare;
using Xunit;

namespace CastBox.Tests;

public sealed class ScreenShareServiceTests
{
    private static readonly IReadOnlyList<ScreenInfo> Screens = new[]
    {
        new ScreenInfo(0, "Screen 1 (main)", @"\\.\DISPLAY1", 2560, 1080, true),
        new ScreenInfo(1, "Screen 2", @"\\.\DISPLAY2", 1920, 1080, false),
    };

    private readonly ManualClock _clock = new();
    private readonly List<ShareRequest> _asked = new();
    private IReadOnlyDictionary<string, string>? _saved;
    private int _changes;

    private ScreenShareService Create(IReadOnlyDictionary<string, string>? allowed = null)
    {
        var service = new ScreenShareService(() => Screens, allowed, saved => _saved = saved, _clock);
        service.ApprovalNeeded += _asked.Add;
        service.Changed += () => _changes++;
        return service;
    }

    [Fact]
    public void A_TV_that_was_never_allowed_has_to_wait_for_an_answer_and_gets_no_token()
    {
        var service = Create();
        var request = service.Request("tv1", "Living room", 1)!;
        Assert.Equal(ShareStatus.Pending, request.Status);
        Assert.Null(request.Token);
        Assert.Single(_asked);
        Assert.Equal("Living room", _asked[0].TvName);
        Assert.Null(service.Open("anything", 1));
    }

    [Fact]
    public void Allowing_once_gives_a_token_for_that_screen_only_and_is_not_remembered()
    {
        var service = Create();
        var request = service.Request("tv1", "Living room", 1)!;
        service.Resolve(request.Id, ConsentChoice.AllowOnce);

        var answer = service.Poll(request.Id)!;
        Assert.Equal(ShareStatus.Approved, answer.Status);
        Assert.NotNull(service.Open(answer.Token, 1));
        Assert.Null(service.Open(answer.Token, 0)); // the other monitor is a different permission
        Assert.Null(_saved);

        // Asking again needs a new answer.
        Assert.Equal(ShareStatus.Pending, service.Request("tv1", "Living room", 1)!.Status);
    }

    [Fact]
    public void Allowing_for_good_is_saved_and_later_requests_are_approved_without_asking()
    {
        var service = Create();
        var first = service.Request("tv1", "Living room", 0)!;
        service.Resolve(first.Id, ConsentChoice.AlwaysAllow);
        Assert.Equal("Living room", Assert.Single(_saved!).Value);

        var second = service.Request("tv1", "Living room", 1)!;
        Assert.Equal(ShareStatus.Approved, second.Status);
        Assert.NotNull(second.Token);
        Assert.Single(_asked); // only the first time

        // A fresh run of the app remembers.
        var next = Create(_saved);
        Assert.Equal(ShareStatus.Approved, next.Request("tv1", "x", 0)!.Status);
    }

    [Fact]
    public void Refusing_gives_no_token_and_an_unanswered_question_expires()
    {
        var service = Create();
        var denied = service.Request("tv1", "Living room", 0)!;
        service.Resolve(denied.Id, ConsentChoice.Deny);
        Assert.Equal(ShareStatus.Denied, service.Poll(denied.Id)!.Status);
        Assert.Null(denied.Token);

        var forgotten = service.Request("tv2", "Bedroom", 0)!;
        _clock.Advance(ScreenShareService.RequestLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(ShareStatus.Expired, service.Poll(forgotten.Id)!.Status);
        service.Resolve(forgotten.Id, ConsentChoice.AllowOnce); // too late: ignored
        Assert.Equal(ShareStatus.Expired, service.Poll(forgotten.Id)!.Status);
    }

    [Fact]
    public void Asking_again_replaces_the_earlier_waiting_question_and_unknown_screens_are_refused()
    {
        var service = Create();
        var a = service.Request("tv1", "Living room", 0)!;
        var b = service.Request("tv1", "Living room", 1)!;
        Assert.Equal(ShareStatus.Expired, service.Poll(a.Id)!.Status);
        Assert.Equal(ShareStatus.Pending, service.Poll(b.Id)!.Status);
        Assert.Single(service.PendingRequests());
        Assert.Null(service.Request("tv1", "Living room", 5));
        Assert.Null(service.Request("", "x", 0));
        Assert.Null(service.Poll("nope"));
    }

    [Fact]
    public void A_token_stops_working_after_it_expires()
    {
        var service = Create();
        var r = service.Request("tv1", "Living room", 0)!;
        service.Resolve(r.Id, ConsentChoice.AllowOnce);
        var token = service.Poll(r.Id)!.Token;
        _clock.Advance(ScreenShareService.TokenLifetime + TimeSpan.FromMinutes(1));
        Assert.Null(service.Open(token, 0));
    }

    [Fact]
    public void A_stream_is_a_session_that_stops_when_closed_and_can_be_ended_from_the_PC()
    {
        var service = Create();
        var r = service.Request("tv1", "Living room", 0)!;
        service.Resolve(r.Id, ConsentChoice.AllowOnce);
        var session = service.Open(service.Poll(r.Id)!.Token, 0)!;
        Assert.Single(service.Sessions);
        Assert.False(session.Ended.IsCancellationRequested);

        service.Stop(session.Id);
        Assert.True(session.Ended.IsCancellationRequested);
        service.Close(session);
        Assert.Empty(service.Sessions);
    }

    [Fact]
    public void Stopping_everything_ends_streams_and_cancels_one_time_permissions_but_keeps_allowed_TVs()
    {
        var service = Create();
        var once = service.Request("tv1", "Living room", 0)!; service.Resolve(once.Id, ConsentChoice.AllowOnce);
        var always = service.Request("tv2", "Bedroom", 0)!; service.Resolve(always.Id, ConsentChoice.AlwaysAllow);
        var onceToken = service.Poll(once.Id)!.Token;
        var alwaysToken = service.Poll(always.Id)!.Token;
        var s1 = service.Open(onceToken, 0)!;
        var s2 = service.Open(alwaysToken, 0)!;

        service.StopAll();
        Assert.True(s1.Ended.IsCancellationRequested);
        Assert.True(s2.Ended.IsCancellationRequested);
        Assert.Null(service.Open(onceToken, 0));          // one-time permission is gone
        Assert.NotNull(service.Open(alwaysToken, 0));     // the allowed TV can carry on
    }

    [Fact]
    public void Forgetting_a_TV_cancels_its_permission_and_ends_its_streams()
    {
        var service = Create();
        var r = service.Request("tv1", "Living room", 0)!; service.Resolve(r.Id, ConsentChoice.AlwaysAllow);
        var token = service.Poll(r.Id)!.Token;
        var session = service.Open(token, 0)!;

        service.Forget("tv1");
        Assert.True(session.Ended.IsCancellationRequested);
        Assert.Null(service.Open(token, 0));
        Assert.Empty(_saved!);
        Assert.Equal(ShareStatus.Pending, service.Request("tv1", "Living room", 0)!.Status);
    }

    [Fact]
    public void The_PC_can_send_a_screen_to_a_TV_that_has_checked_in_recently()
    {
        var service = Create();
        Assert.False(service.Push("tv1", 0)); // never seen
        service.Touch("tv1", "Living room");
        Assert.Equal("Living room", Assert.Single(service.RecentTvs()).Name);

        Assert.True(service.Push("tv1", 1));
        Assert.False(service.Push("tv1", 9));
        var offer = service.TakePush("tv1")!;
        Assert.Equal(1, offer.Screen);
        Assert.NotNull(service.Open(offer.Token, 1));
        Assert.Null(service.Open(offer.Token, 0));
        Assert.Null(service.TakePush("tv1")); // handed out once
    }

    [Fact]
    public void A_TV_that_stopped_checking_in_drops_off_the_list()
    {
        var service = Create();
        service.Touch("tv1", "Living room");
        _clock.Advance(ScreenShareService.TvRecentWindow + TimeSpan.FromSeconds(1));
        Assert.Empty(service.RecentTvs());
        service.Touch("tv1", "Living room");
        Assert.Single(service.RecentTvs());
    }

    [Fact]
    public void Changes_are_announced_so_the_window_can_refresh()
    {
        var service = Create();
        var before = _changes;
        service.Request("tv1", "Living room", 0);
        Assert.True(_changes > before);
    }
}
