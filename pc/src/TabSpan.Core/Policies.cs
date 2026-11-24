// 時計と、時間で決まる判断: 認証の失敗の遮断(FR-3)、キーフレームの要求(FR-16)、送信待ちの詰まり(FR-17)。
// 時計は IClock で差し替える(テストは偽の時計)。どれも複数のスレッドから呼ばれてよい。
using System.Diagnostics;

namespace TabSpan.Core;

/// <summary>単調な µs(差だけに意味がある)と、壁時計。</summary>
public interface IClock
{
    long NowUs { get; }
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    // ts * 1e6 は 10 MHz の時計で起動から約 10 日であふれるので、割ってから掛ける
    public long NowUs
    {
        get
        {
            var ts = Stopwatch.GetTimestamp();
            var f = Stopwatch.Frequency;
            return ts / f * 1_000_000 + ts % f * 1_000_000 / f;
        }
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>同じリモートから認証の失敗が 5 回続いたら、60 秒の間 IsBlocked が真。成功で数え直す。</summary>
public sealed class AuthThrottle(IClock clock)
{
    public const int MaxFailures = 5;
    public const long BlockUs = 60_000_000;

    private readonly Dictionary<string, (int Failures, long? BlockedUntilUs)> _remotes = [];
    private readonly Lock _lock = new();

    public bool IsBlocked(string remote)
    {
        lock (_lock)
        {
            if (!_remotes.TryGetValue(remote, out var r) || r.BlockedUntilUs is not { } until)
            {
                return false;
            }

            if (clock.NowUs < until)
            {
                return true;
            }

            _remotes.Remove(remote); // 遮断が明けたら数え直し
            return false;
        }
    }

    public void Fail(string remote)
    {
        lock (_lock)
        {
            _remotes.TryGetValue(remote, out var r);
            var failures = r.Failures + 1;
            _remotes[remote] = failures >= MaxFailures ? (0, clock.NowUs + BlockUs) : (failures, r.BlockedUntilUs);
        }
    }

    public void Succeed(string remote)
    {
        lock (_lock)
        {
            _remotes.Remove(remote);
        }
    }
}

/// <summary>FR-16。次に送るフレームをキーフレームにするかを持つ。端末からの要求は 1 秒に 1 回まで。</summary>
public sealed class KeyframePolicy(IClock clock)
{
    public const long RequestIntervalUs = 1_000_000;

    private readonly Lock _lock = new();
    private bool _force = true; // セッションの最初のフレーム
    private long? _lastRequestUs;

    /// <summary>RequestKeyframe を受けた。前に受け付けてから 1 秒たっていなければ捨てて false。</summary>
    public bool OnRequest()
    {
        lock (_lock)
        {
            var now = clock.NowUs;
            if (_lastRequestUs is { } last && now - last < RequestIntervalUs)
            {
                return false;
            }

            _lastRequestUs = now;
            _force = true;
            return true;
        }
    }

    /// <summary>StreamConfig を送った。次のフレームをキーフレームにする。</summary>
    public void MarkStreamStart() => ForceNext();

    /// <summary>1 秒の制限を通さずに次をキーフレームにする(詰まりで待ちを捨てたとき、エンコーダーを作り直したとき)。</summary>
    public void ForceNext()
    {
        lock (_lock)
        {
            _force = true;
        }
    }

    /// <summary>次のフレームをキーフレームにするなら true を返し、印を消す。</summary>
    public bool TakeForceKey()
    {
        lock (_lock)
        {
            var f = _force;
            _force = false;
            return f;
        }
    }
}

/// <summary>FR-17。送信待ちが上限を超えたら、待ちを全部捨てて次をキーフレームにする。捨てた数を数える。</summary>
public sealed class BacklogPolicy
{
    private long _dropped;

    public BacklogPolicy(int limitFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limitFrames, 1);
        LimitFrames = limitFrames;
    }

    public int LimitFrames { get; }

    /// <summary>これまでに捨てた枚数(統計の PC 側の分)。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// 待ちの枚数を渡す。上限を超えていれば捨てる枚数として数えて true。
    /// 呼び出し側は true なら待ちを全部捨て、KeyframePolicy.ForceNext() を呼ぶ。
    /// </summary>
    public bool ShouldFlush(int queued)
    {
        if (queued <= LimitFrames)
        {
            return false;
        }

        Interlocked.Add(ref _dropped, queued);
        return true;
    }
}
