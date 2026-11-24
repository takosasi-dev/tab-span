// タッチ(§9.3 の Touch、§9.8)の単体テスト。AC-24・AC-26・AC-27・AC-28 と、接点の表の決まり。
namespace TabSpan.Core.Tests;

public class TouchTests
{
    private sealed class FakeClock : IClock
    {
        public long NowUs { get; set; } = 1_000_000;

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class FakeInjector : ITouchInjector
    {
        public List<List<InjectedContact>> Calls { get; } = [];

        public bool Fail { get; set; }

        public bool Inject(IReadOnlyList<InjectedContact> contacts, out int error)
        {
            Calls.Add([.. contacts]);
            error = Fail ? 87 : 0;
            return !Fail;
        }
    }

    private static readonly ScreenRect Left = new(-1920, 0, 1920, 1200);

    private static byte[] Touch(params TouchPoint[] points) => TouchCodec.Encode(points);

    private static (TouchRelay Relay, FakeInjector Injector, FakeClock Clock) NewRelay()
    {
        var injector = new FakeInjector();
        var clock = new FakeClock();
        var relay = new TouchRelay(injector, clock, _ => { }) { RefreshMs = 100 };
        relay.Begin(Left);
        return (relay, injector, clock);
    }

    [Fact]
    public void AC24_矩形の隅が物理ピクセルの隅になる()
    {
        Assert.Equal((-1920, 0), CoordinateMapper.Map(0, 0, Left));
        Assert.Equal((-1, 1199), CoordinateMapper.Map(65535, 65535, Left));
    }

    [Fact]
    public void 枠の長さと_action_が合わなければ読まない()
    {
        Assert.Throws<FormatException>(() => TouchCodec.Parse([]));
        Assert.Throws<FormatException>(() => TouchCodec.Parse([1, 0, 0, 0, 0, 0]));
        Assert.Throws<FormatException>(() => TouchCodec.Parse([1, 0, 4, 0, 0, 0, 0]));
        Assert.Empty(TouchCodec.Parse([0]));
    }

    [Fact]
    public void 押して動かして離すと_押す_動く_離すの順に全部の接点を渡す()
    {
        var (relay, injector, _) = NewRelay();
        relay.OnTouch(Touch(new TouchPoint(5, TouchAction.Down, 0, 0)), 0);
        relay.OnTouch(Touch(new TouchPoint(7, TouchAction.Down, 65535, 65535)), 0);
        relay.OnTouch(Touch(new TouchPoint(5, TouchAction.Move, 65535, 0)), 0);
        relay.OnTouch(Touch(new TouchPoint(5, TouchAction.Up, 65535, 0)), 0);

        Assert.Equal([new InjectedContact(0, -1920, 0, ContactChange.Down)], injector.Calls[0]);
        Assert.Equal([new InjectedContact(0, -1920, 0, ContactChange.Hold), new InjectedContact(1, -1, 1199, ContactChange.Down)], injector.Calls[1]);
        Assert.Equal([new InjectedContact(0, -1, 0, ContactChange.Move), new InjectedContact(1, -1, 1199, ContactChange.Hold)], injector.Calls[2]);
        Assert.Equal([new InjectedContact(0, -1, 0, ContactChange.Up), new InjectedContact(1, -1, 1199, ContactChange.Hold)], injector.Calls[3]);
        Assert.Equal(1, relay.ActiveContacts);
    }

    [Fact]
    public void 接点は_10_点まで_11_点目は無視し_空いた番号を使い直す()
    {
        var table = new ContactTable();
        for (byte id = 0; id < 11; id++)
        {
            table.Apply([new TouchPoint(id, TouchAction.Down, 0, 0)], Left);
        }

        Assert.Equal(10, table.Count);
        Assert.Empty(table.Apply([new TouchPoint(10, TouchAction.Move, 1, 1)], Left)); // 11 点目は表に無い
        table.Apply([new TouchPoint(3, TouchAction.Up, 0, 0)], Left);
        var again = table.Apply([new TouchPoint(20, TouchAction.Down, 0, 0)], Left);
        Assert.Contains(new InjectedContact(3, -1920, 0, ContactChange.Down), again);
    }

    [Fact]
    public void AC26_接点が_2_つ押されたままセッションを終えると_2_つの取り消しが届く()
    {
        var (relay, injector, _) = NewRelay();
        relay.OnTouch(Touch(new TouchPoint(0, TouchAction.Down, 0, 0), new TouchPoint(1, TouchAction.Down, 100, 100)), 0);
        relay.End();
        Assert.Equal([ContactChange.Cancel, ContactChange.Cancel], injector.Calls[^1].Select(c => c.Change));
        Assert.Equal(0, relay.ActiveContacts);
    }

    [Fact]
    public void AC26_タッチを切ったときも押したままの接点を取り消す()
    {
        var (relay, injector, _) = NewRelay();
        relay.OnTouch(Touch(new TouchPoint(0, TouchAction.Down, 0, 0), new TouchPoint(1, TouchAction.Down, 100, 100)), 0);
        relay.SetEnabled(false);
        Assert.Equal([ContactChange.Cancel, ContactChange.Cancel], injector.Calls[^1].Select(c => c.Change));
    }

    [Fact]
    public void AC27_無効の間に受けた_Touch_では注入を呼ばない()
    {
        var (relay, injector, _) = NewRelay();
        relay.SetEnabled(false);
        relay.OnTouch(Touch(new TouchPoint(0, TouchAction.Down, 0, 0)), 0);
        Assert.Empty(injector.Calls);
    }

    [Fact]
    public void AC28_更新の間隔が過ぎると全部の接点を_1_回送り直し_2_秒無通信で取り消す()
    {
        var (relay, injector, clock) = NewRelay();
        relay.OnTouch(Touch(new TouchPoint(0, TouchAction.Down, 0, 0), new TouchPoint(1, TouchAction.Down, 100, 100)), 0);
        clock.NowUs += 99_000;
        relay.Tick();
        Assert.Single(injector.Calls);
        clock.NowUs += 1_000;
        relay.Tick();
        Assert.Equal(2, injector.Calls.Count);
        Assert.Equal([ContactChange.Hold, ContactChange.Hold], injector.Calls[1].Select(c => c.Change));

        relay.OnFrameSeen(); // Stats などが届いている間は取り消さない
        clock.NowUs += 2_000_000;
        relay.Tick();
        Assert.Equal([ContactChange.Hold, ContactChange.Hold], injector.Calls[^1].Select(c => c.Change));
        clock.NowUs += 1;
        relay.Tick();
        Assert.Equal([ContactChange.Cancel, ContactChange.Cancel], injector.Calls[^1].Select(c => c.Change));
        Assert.Equal(0, relay.ActiveContacts);
    }

    [Fact]
    public void 注入に_10_秒で_10_回失敗すると自分で切る()
    {
        var (relay, injector, clock) = NewRelay();
        var disabled = 0;
        relay.AutoDisabled += () => disabled++;
        injector.Fail = true;
        for (var i = 0; i < 10; i++)
        {
            relay.OnTouch(Touch(new TouchPoint(0, TouchAction.Down, 0, 0)), 0);
            clock.NowUs += 500_000;
        }

        Assert.Equal(1, disabled);
        Assert.False(relay.Enabled);
        Assert.Equal(0, relay.ActiveContacts);
    }

    [Fact]
    public void 映す画面が変わると前の画面の上で取り消す()
    {
        var (relay, injector, _) = NewRelay();
        relay.OnTouch(Touch(new TouchPoint(0, TouchAction.Down, 0, 0)), 0);
        relay.SetRect(new ScreenRect(0, 0, 1920, 1080));
        Assert.Equal([new InjectedContact(0, -1920, 0, ContactChange.Cancel)], injector.Calls[^1]);
    }
}
