// 遅れの統計: 1 秒ぶんの値を溜めて 50・95 パーセンタイルを出す。Ping/Pong から往復と時計のずれ(直近 5 回の中央値)を出す。
// 内訳(取り込み→送信など)ごとに 1 つずつ持つ使い方を想定する。
namespace TabSpan.Core;

public sealed record LatencySummary(int Count, long P50Us, long P95Us);

public sealed class LatencyStats
{
    private const int PongWindow = 5;

    private readonly Lock _lock = new();
    private readonly List<long> _values = [];
    private readonly Queue<(long Rtt, long Offset)> _pongs = new();
    private long? _rtt;
    private long? _offset;

    /// <summary>直近 5 回の往復の中央値(µs)。Pong がまだ無ければ null。</summary>
    public long? RttUs { get { lock (_lock) { return _rtt; } } }

    /// <summary>直近 5 回の、端末の時計 − PC の時計 の中央値(µs)。到着の時刻 − OffsetUs で PC の時計に直る。</summary>
    public long? OffsetUs { get { lock (_lock) { return _offset; } } }

    public void Add(long valueUs)
    {
        lock (_lock)
        {
            _values.Add(valueUs);
        }
    }

    /// <summary>溜めた値の p50・p95(最近傍順位)を返して空にする。1 秒ごとに呼ぶ。値が無ければ Count 0。</summary>
    public LatencySummary TakeSecond()
    {
        long[] v;
        lock (_lock)
        {
            v = [.. _values];
            _values.Clear();
        }

        if (v.Length == 0)
        {
            return new LatencySummary(0, 0, 0);
        }

        Array.Sort(v);
        return new LatencySummary(v.Length, Percentile(v, 50), Percentile(v, 95));
    }

    /// <summary>Pong を受けた。t1 = PC が Ping に入れた時刻、t2 = 端末の時刻、nowUs = 受けた時の PC の時刻。</summary>
    public void AddPong(long t1, long t2, long nowUs)
    {
        var rtt = nowUs - t1;
        lock (_lock)
        {
            _pongs.Enqueue((rtt, t2 - (t1 + rtt / 2)));
            if (_pongs.Count > PongWindow)
            {
                _pongs.Dequeue();
            }

            _rtt = Median(_pongs.Select(p => p.Rtt));
            _offset = Median(_pongs.Select(p => p.Offset));
        }
    }

    // 最近傍順位: 小さい方から ceil(p/100 * n) 番目
    private static long Percentile(long[] sorted, int p) => sorted[Math.Max(0, (p * sorted.Length + 99) / 100 - 1)];

    private static long Median(IEnumerable<long> values)
    {
        var v = values.Order().ToArray();
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
