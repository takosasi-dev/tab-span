// 待ち受けの計画(AC-2)、切断から外すまでの時間の判定(D-18)、仮想ディスプレイを付ける位置(§10 の「重なるときは主モニターの右隣」)。
using System.Net;

namespace TabSpan.Core.Tests;

public class ListenAndDisplayTests
{
    [Fact]
    public void AC2_LAN_が偽なら_127_0_0_1_の_TCP_だけで_真なら_0_0_0_0_と_UDP()
    {
        Assert.Equal(new ListenPlan(IPAddress.Loopback, false), ListenPlanner.For(false));
        Assert.Equal(new ListenPlan(IPAddress.Any, true), ListenPlanner.For(true));
    }

    [Fact]
    public void 切断から_delaySec_が過ぎたら_1_回だけ真で_つなぎ直すと数え直す()
    {
        var clock = new FakeClock();
        var policy = new DetachPolicy(clock, () => 30);
        Assert.False(policy.ShouldDetachNow()); // 切れていない

        policy.OnDisconnected();
        clock.NowUs += 29_999_999;
        Assert.False(policy.ShouldDetachNow());
        clock.NowUs += 1;
        Assert.True(policy.ShouldDetachNow());
        Assert.False(policy.ShouldDetachNow()); // 1 回だけ

        policy.OnDisconnected();
        clock.NowUs += 20_000_000;
        policy.OnConnected();
        clock.NowUs += 20_000_000;
        Assert.False(policy.ShouldDetachNow());
    }

    private static readonly DisplayPlacement Primary = new(0, 0, 1920, 1080, 60);

    [Fact]
    public void 覚えた位置が空いていればそのまま()
    {
        var wanted = new DisplayPlacement(-2560, 0, 2560, 1600, 60);
        Assert.Equal(wanted, DisplayLayout.Place(wanted, Primary, [Primary]));
    }

    [Fact]
    public void 覚えた位置が重なるなら主モニターの右隣で_大きさは覚えた物()
    {
        var wanted = new DisplayPlacement(1000, 0, 2560, 1600, 60);
        Assert.Equal(new DisplayPlacement(1920, 0, 2560, 1600, 60), DisplayLayout.Place(wanted, Primary, [Primary]));
    }

    [Fact]
    public void 覚えが無いなら主モニターの右隣で_大きさは任せる()
    {
        Assert.Equal(new DisplayPlacement(1920, 0, 0, 0, 0), DisplayLayout.Place(DisplayPlacement.Unknown, Primary, [Primary]));
    }

    [Fact]
    public void 主モニターの右隣も塞がっていれば_いちばん右の物の右隣()
    {
        var right = new DisplayPlacement(1920, 0, 1920, 1080, 60);
        Assert.Equal(new DisplayPlacement(3840, 0, 0, 0, 0), DisplayLayout.Place(DisplayPlacement.Unknown, Primary, [Primary, right]));
    }
}
