// 時間で決まる判断(AuthThrottle・KeyframePolicy)、BacklogPolicy、SessionMachine の受けてよい type、DeviceRegistry、LatencyStats。
// 時計は 0 から始まる偽物で進める。
using System.Security.Cryptography;

namespace TabSpan.Core.Tests;

internal sealed class FakeClock : IClock
{
    public long NowUs { get; set; }

    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
}

public class AuthThrottleTests
{
    [Fact]
    public void 失敗_5_回で_60_秒遮断し_明けたら数え直す()
    {
        var clock = new FakeClock();
        var t = new AuthThrottle(clock);
        for (var i = 0; i < 4; i++)
        {
            t.Fail("127.0.0.1");
        }

        Assert.False(t.IsBlocked("127.0.0.1"));
        t.Fail("127.0.0.1");
        Assert.True(t.IsBlocked("127.0.0.1"));
        Assert.False(t.IsBlocked("192.168.0.2")); // 別のリモートは別に数える

        clock.NowUs = 59_999_999;
        Assert.True(t.IsBlocked("127.0.0.1"));
        clock.NowUs = 60_000_000;
        Assert.False(t.IsBlocked("127.0.0.1"));

        t.Fail("127.0.0.1"); // 明けた後は 1 回目から
        Assert.False(t.IsBlocked("127.0.0.1"));
    }

    [Fact]
    public void 成功すると続いた失敗を数え直す()
    {
        var t = new AuthThrottle(new FakeClock());
        for (var i = 0; i < 4; i++)
        {
            t.Fail("a");
        }

        t.Succeed("a");
        for (var i = 0; i < 4; i++)
        {
            t.Fail("a");
        }

        Assert.False(t.IsBlocked("a"));
    }
}

public class KeyframePolicyTests
{
    [Fact]
    public void 最初のフレームはキーフレームで_取ったら消える()
    {
        var k = new KeyframePolicy(new FakeClock());
        Assert.True(k.TakeForceKey());
        Assert.False(k.TakeForceKey());
        k.MarkStreamStart();
        Assert.True(k.TakeForceKey());
    }

    [Fact]
    public void 要求は_1_秒に_1_回まで_時計が_0_でも最初の要求は通る()
    {
        var clock = new FakeClock();
        var k = new KeyframePolicy(clock);
        k.TakeForceKey();

        Assert.True(k.OnRequest());
        Assert.True(k.TakeForceKey());
        clock.NowUs = 999_999;
        Assert.False(k.OnRequest());
        Assert.False(k.TakeForceKey());
        clock.NowUs = 1_000_000;
        Assert.True(k.OnRequest());
        Assert.True(k.TakeForceKey());
    }

    [Fact]
    public void ForceNext_は_1_秒の制限を通さない()
    {
        var k = new KeyframePolicy(new FakeClock());
        k.TakeForceKey();
        Assert.True(k.OnRequest());
        k.TakeForceKey();

        k.ForceNext();

        Assert.True(k.TakeForceKey());
    }
}

public class BacklogPolicyTests
{
    [Fact]
    public void 上限を超えたら捨てる判断をして_捨てた数を足す()
    {
        var b = new BacklogPolicy(3);

        Assert.False(b.ShouldFlush(3));
        Assert.Equal(0, b.Dropped);
        Assert.True(b.ShouldFlush(4));
        Assert.Equal(4, b.Dropped);
        Assert.True(b.ShouldFlush(5));
        Assert.Equal(9, b.Dropped);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BacklogPolicy(0));
    }
}

public class SessionMachineTests
{
    private static readonly MessageType[] All = Enum.GetValues<MessageType>();

    [Theory]
    [InlineData(SessionState.AwaitingHello, new[] { MessageType.Hello, MessageType.PairRequest, MessageType.Bye })]
    [InlineData(SessionState.Pairing, new[] { MessageType.Bye })]
    [InlineData(SessionState.Streaming, new[] { MessageType.RequestKeyframe, MessageType.Stats, MessageType.Touch, MessageType.Pong, MessageType.Bye })]
    [InlineData(SessionState.Closed, new MessageType[0])]
    public void 状態ごとに受けてよい_type(SessionState state, MessageType[] accepted)
    {
        var m = new SessionMachine();
        switch (state)
        {
            case SessionState.Pairing: m.BeginPairing(); break;
            case SessionState.Streaming: m.Authenticate(); break;
            case SessionState.Closed: m.Close(); break;
        }

        Assert.Equal(state, m.State);
        Assert.Equal(15, All.Length);
        foreach (var t in All)
        {
            Assert.True(accepted.Contains(t) == m.Accepts(t), $"{state} で {t}");
        }

        Assert.False(m.Accepts((MessageType)0x7E)); // enum に無い値
    }

    [Fact]
    public void 移れない遷移は例外で_Close_はどこからでも()
    {
        var m = new SessionMachine();
        m.Authenticate();
        Assert.Throws<InvalidOperationException>(m.BeginPairing);
        Assert.Throws<InvalidOperationException>(m.Authenticate);
        m.Close();
        m.Close();
        Assert.Equal(SessionState.Closed, m.State);
        Assert.Throws<InvalidOperationException>(m.Authenticate);
    }
}

public class DeviceRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Token(int i) => Enumerable.Repeat((byte)i, 32).ToArray();

    [Fact]
    public void トークンは_SHA256_の_16_進だけを持ち_照合で_LastSeenAt_を更新する()
    {
        var r = new DeviceRegistry([]);
        r.Upsert("a", "Tab", Token(1), T0);

        var e = Assert.Single(r.Entries);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Token(1))), e.TokenSha256);
        Assert.Equal((T0, T0), (e.PairedAt, e.LastSeenAt));

        Assert.False(r.Verify("a", Token(2), T0.AddHours(1)));
        Assert.False(r.Verify("a", Token(1).AsSpan(0, 31), T0.AddHours(1))); // 長さ違い
        Assert.False(r.Verify("b", Token(1), T0.AddHours(1)));
        Assert.Equal(T0, r.Entries[0].LastSeenAt);

        Assert.True(r.Verify("a", Token(1), T0.AddHours(2)));
        Assert.Equal(T0.AddHours(2), r.Entries[0].LastSeenAt);
    }

    [Fact]
    public void 同じ_deviceId_は置き換え_古いトークンは通らない()
    {
        var r = new DeviceRegistry([]);
        r.Upsert("a", "old", Token(1), T0);
        r.Upsert("a", "new", Token(2), T0.AddDays(1));

        var e = Assert.Single(r.Entries);
        Assert.Equal(("new", T0.AddDays(1)), (e.Name, e.PairedAt));
        Assert.False(r.Verify("a", Token(1), T0));
        Assert.True(r.Verify("a", Token(2), T0));
    }

    [Fact]
    public void 十台で満杯_新しい端末は例外_同じ端末の置き換えは通る_消すと空く()
    {
        var r = new DeviceRegistry([]);
        for (var i = 0; i < DeviceRegistry.MaxDevices; i++)
        {
            Assert.False(r.IsFull);
            r.Upsert($"d{i}", "Tab", Token(i), T0);
        }

        Assert.True(r.IsFull);
        Assert.Throws<InvalidOperationException>(() => r.Upsert("d10", "Tab", Token(10), T0));
        r.Upsert("d3", "again", Token(33), T0);
        Assert.Equal(10, r.Entries.Count);
        Assert.True(r.Verify("d3", Token(33), T0));

        Assert.True(r.Remove("d3"));
        Assert.False(r.Remove("d3"));
        Assert.False(r.IsFull);
    }

    [Fact]
    public void 読み込んだ行で照合でき_壊れたハッシュは通らない()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Token(7)));
        var r = new DeviceRegistry([new DeviceEntry("a", "Tab", hash, T0, T0), new DeviceEntry("b", "Tab", "zz", T0, T0)]);

        Assert.True(r.Verify("a", Token(7), T0));
        Assert.False(r.Verify("b", Token(7), T0));
    }
}

public class LatencyStatsTests
{
    [Fact]
    public void 一秒ぶんの_p50_と_p95_を出して空にする()
    {
        var s = new LatencyStats();
        for (var i = 1; i <= 100; i++)
        {
            s.Add(i);
        }

        Assert.Equal(new LatencySummary(100, 50, 95), s.TakeSecond());
        Assert.Equal(new LatencySummary(0, 0, 0), s.TakeSecond());
        s.Add(7);
        Assert.Equal(new LatencySummary(1, 7, 7), s.TakeSecond());
    }

    [Fact]
    public void 往復と時計のずれは直近_5_回の中央値()
    {
        var s = new LatencyStats();
        Assert.Null(s.RttUs);

        // 端末の時計は PC より 1000 µs 進んでいる。往復 200 µs が続き、外れの 1 回(往復 10000)が混ざる。
        s.AddPong(0, 1100, 200);
        s.AddPong(1000, 2100, 1200);
        s.AddPong(2000, 8000, 12000);
        Assert.Equal((200L, 1000L), (s.RttUs!.Value, s.OffsetUs!.Value));

        for (var i = 0; i < 5; i++)
        {
            s.AddPong(10_000 + i, 10_000 + i + 1000 + 50, 10_000 + i + 100); // 往復 100
        }

        Assert.Equal((100L, 1000L), (s.RttUs!.Value, s.OffsetUs!.Value)); // 古い 3 回は外れた
    }
}
