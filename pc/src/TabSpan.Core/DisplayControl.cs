// 仮想ディスプレイの取り付けと取り外し(FR-10・FR-19・D-18): OS を呼ぶ口 IDisplayControl、切断からの時間の判定 DetachPolicy、
// 付ける位置の決め方 DisplayLayout(覚えた位置が他と重なる・覚えが無いなら主モニターの右隣。§10)。どれも OS を呼ばない。
namespace TabSpan.Core;

/// <summary>位置と大きさは物理ピクセル。Width・Height が 0 なら大きさは Windows に任せる。RefreshHz が 0 なら指定しない。</summary>
public sealed record DisplayPlacement(int X, int Y, int Width, int Height, int RefreshHz)
{
    /// <summary>覚えた位置が無い(主モニターの右隣に置き、大きさは Windows に任せる)。</summary>
    public static readonly DisplayPlacement Unknown = new(0, 0, 0, 0, 0);
}

/// <summary>OS を呼ぶ層(テストは偽物)。deviceName は \\.\DISPLAYn。</summary>
public interface IDisplayControl
{
    /// <summary>デスクトップに付いていれば今の位置と大きさ、付いていなければ null。</summary>
    DisplayPlacement? CurrentPlacement(string deviceName);

    bool Detach(string deviceName);

    bool Attach(string deviceName, DisplayPlacement placement);
}

/// <summary>D-18: 切断から delaySec 秒つなぎ直しが無ければ、1 回だけ ShouldDetachNow が真になる(単調な時計)。</summary>
public sealed class DetachPolicy(IClock clock, Func<int> delaySec)
{
    private readonly Lock _lock = new();
    private long? _disconnectedAtUs;

    public void OnDisconnected()
    {
        lock (_lock)
        {
            _disconnectedAtUs = clock.NowUs;
        }
    }

    public void OnConnected()
    {
        lock (_lock)
        {
            _disconnectedAtUs = null;
        }
    }

    /// <summary>時間が来ていれば真を返し、次の切断まで偽に戻る。</summary>
    public bool ShouldDetachNow()
    {
        lock (_lock)
        {
            if (_disconnectedAtUs is not { } at || clock.NowUs - at < delaySec() * 1_000_000L)
            {
                return false;
            }

            _disconnectedAtUs = null;
            return true;
        }
    }
}

public static class DisplayLayout
{
    /// <summary>
    /// 付ける位置を決める。wanted が大きさを持ち、付いているどのモニター(primary を含む attached)とも重ならなければそのまま。
    /// そうでなければ主モニターの右隣。そこも塞がっていれば、付いている中でいちばん右の物の右隣(大きさは wanted のまま)。
    /// </summary>
    public static DisplayPlacement Place(DisplayPlacement wanted, DisplayPlacement primary, IReadOnlyList<DisplayPlacement> attached)
    {
        if (wanted.Width > 0 && wanted.Height > 0 && !attached.Any(a => Overlaps(wanted, a)))
        {
            return wanted;
        }

        var next = wanted with { X = primary.X + primary.Width, Y = primary.Y };
        var probe = next with { Width = Math.Max(1, next.Width), Height = Math.Max(1, next.Height) };
        if (attached.Any(a => Overlaps(probe, a)))
        {
            next = next with { X = attached.Max(a => a.X + a.Width) };
        }

        return next;
    }

    private static bool Overlaps(DisplayPlacement a, DisplayPlacement b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
}
