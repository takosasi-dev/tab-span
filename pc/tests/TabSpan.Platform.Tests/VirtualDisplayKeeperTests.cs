// 仮想ディスプレイの番を、偽の IDisplayControl と偽の時計で確かめる(AC-21・FR-10・FR-19・§10)。OS のディスプレイの構成には触らない。
using TabSpan.Core;

namespace TabSpan.Platform.Tests;

public sealed class VirtualDisplayKeeperTests : IDisposable
{
    private const string Virtual = @"\\.\DISPLAY5";
    private const string Real = @"\\.\DISPLAY1";

    private readonly string _dir = Directory.CreateTempSubdirectory("tabspan-test-").FullName;
    private readonly FakeClock _clock = new();
    private readonly FakeDisplay _display = new();
    private bool _detachOnDisconnect = true;

    private sealed class FakeClock : IClock
    {
        public long NowUs { get; set; }

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class FakeDisplay : IDisplayControl
    {
        public Dictionary<string, DisplayPlacement> Attached { get; } = [];
        public List<string> Detaches { get; } = [];
        public List<(string Name, DisplayPlacement Placement)> Attaches { get; } = [];

        public DisplayPlacement? CurrentPlacement(string deviceName) => Attached.GetValueOrDefault(deviceName);

        public bool Detach(string deviceName)
        {
            Detaches.Add(deviceName);
            return Attached.Remove(deviceName);
        }

        public bool Attach(string deviceName, DisplayPlacement placement)
        {
            Attaches.Add((deviceName, placement));
            Attached[deviceName] = placement == DisplayPlacement.Unknown ? new DisplayPlacement(1920, 0, 1280, 800, 60) : placement;
            return true;
        }
    }

    private string StatePath => Path.Combine(_dir, "state.json");

    private VirtualDisplayKeeper NewKeeper() =>
        new(_display, name => name == Virtual, StatePath, _clock, () => _detachOnDisconnect, () => 30, _ => { });

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AC21_切断から_detachDelaySec_が過ぎると取り外しが_1_回呼ばれ_位置を覚える()
    {
        var placed = new DisplayPlacement(-2560, 0, 2560, 1600, 60);
        _display.Attached[Virtual] = placed;
        var keeper = NewKeeper();
        var detached = new List<string>();
        keeper.Detached += detached.Add;
        Assert.True(keeper.EnsureAttached(Virtual));
        keeper.OnSessionStarted();
        keeper.OnSessionEnded();

        _clock.NowUs += 29_000_000;
        keeper.Tick();
        Assert.Empty(_display.Detaches);
        _clock.NowUs += 1_000_000;
        keeper.Tick();
        keeper.Tick();
        Assert.Equal([Virtual], _display.Detaches);
        Assert.Equal([Virtual], detached);

        // つなぎ直したら覚えた位置で戻す
        Assert.True(NewKeeper().EnsureAttached(Virtual));
        Assert.Equal([(Virtual, placed)], _display.Attaches);
    }

    [Fact]
    public void AC21_過ぎる前につなぎ直すと取り外しは_0_回()
    {
        _display.Attached[Virtual] = new DisplayPlacement(1920, 0, 1280, 800, 60);
        var keeper = NewKeeper();
        keeper.EnsureAttached(Virtual);
        keeper.OnSessionEnded();
        _clock.NowUs += 20_000_000;
        keeper.OnSessionStarted();
        _clock.NowUs += 60_000_000;
        keeper.Tick();
        Assert.Empty(_display.Detaches);
    }

    [Fact]
    public void AC21_実在のモニターの識別子を渡すと_取り外しも取り付けも呼ばずに例外()
    {
        _display.Attached[Real] = new DisplayPlacement(0, 0, 1920, 1080, 60);
        var keeper = NewKeeper();
        Assert.Throws<InvalidOperationException>(() => keeper.DetachVirtual(Real));
        Assert.Throws<InvalidOperationException>(() => keeper.AttachVirtual(Real, DisplayPlacement.Unknown));
        Assert.True(keeper.EnsureAttached(Real)); // FR-11: そのまま映す
        keeper.OnSessionEnded();
        _clock.NowUs += 60_000_000;
        keeper.Tick();
        keeper.DetachOwnedNow();
        Assert.Empty(_display.Detaches);
        Assert.Empty(_display.Attaches);
    }

    [Fact]
    public void 外れている仮想ディスプレイは覚えが無ければ主モニターの右隣に任せて付け_失敗なら_false()
    {
        var keeper = NewKeeper();
        Assert.True(keeper.EnsureAttached(Virtual));
        Assert.Equal([(Virtual, DisplayPlacement.Unknown)], _display.Attaches);

        var failing = new VirtualDisplayKeeper(new RefusingDisplay(), _ => true, Path.Combine(_dir, "other.json"), _clock, () => true, () => 30, _ => { });
        Assert.False(failing.EnsureAttached(Virtual));
    }

    [Fact]
    public void 強制終了で付いたままの物を_次の起動で片付ける()
    {
        NewKeeper().EnsureAttached(Virtual); // TabSpan が付けた。ここで落ちたことにする
        Assert.True(File.Exists(StatePath));

        NewKeeper().CleanupFromState();
        Assert.Equal([Virtual], _display.Detaches);
        NewKeeper().CleanupFromState();
        Assert.Single(_display.Detaches); // 2 回目は何もしない
    }

    [Fact]
    public void 終了のとき_外さない設定でも_TabSpan_が付けた物は外し_映していただけの物は残す()
    {
        const string other = @"\\.\DISPLAY6";
        _display.Attached[other] = new DisplayPlacement(1920, 0, 1280, 800, 60);
        _detachOnDisconnect = false;
        var keeper = new VirtualDisplayKeeper(_display, _ => true, StatePath, _clock, () => _detachOnDisconnect, () => 30, _ => { });
        keeper.EnsureAttached(other);   // 付いていた物を映しただけ
        keeper.EnsureAttached(Virtual); // TabSpan が付けた
        keeper.OnSessionEnded();
        _clock.NowUs += 60_000_000;
        keeper.Tick();
        Assert.Empty(_display.Detaches);

        keeper.DetachOwnedNow();
        Assert.Equal([Virtual], _display.Detaches);
    }

    private sealed class RefusingDisplay : IDisplayControl
    {
        public DisplayPlacement? CurrentPlacement(string deviceName) => null;

        public bool Detach(string deviceName) => false;

        public bool Attach(string deviceName, DisplayPlacement placement) => false;
    }
}
