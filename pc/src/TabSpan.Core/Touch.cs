// タッチ(§9.3 の Touch、§9.8 の注入): 枠の読み書き、映像の割合 → 画面の物理ピクセル、接点の表、注入の流れ(更新の送り直し・時間切れ・失敗の数え上げ)。
// TouchRelay はスレッドに安全でない。注入は 1 本のスレッドからだけ行う(§9.8)ので、呼ぶ側がそのスレッドに集める。
using System.Buffers.Binary;

namespace TabSpan.Core;

public enum TouchAction : byte { Down = 0, Move = 1, Up = 2, Cancel = 3 }

public readonly record struct TouchPoint(byte Id, TouchAction Action, ushort X, ushort Y);

/// <summary>映す画面の矩形(物理ピクセル)。</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height);

public enum ContactChange { Down, Move, Hold, Up, Cancel }

/// <summary>注入に渡す 1 接点。Slot は 0〜9 の接点の番号。</summary>
public readonly record struct InjectedContact(int Slot, int X, int Y, ContactChange Change);

/// <summary>OS を呼ぶ層(テストでは偽物)。全部の接点を 1 回で渡す。失敗なら false と OS のエラーの番号。</summary>
public interface ITouchInjector
{
    bool Inject(IReadOnlyList<InjectedContact> contacts, out int error);
}

public static class TouchCodec
{
    public const int ContactSize = 6;

    public static TouchPoint[] Parse(ReadOnlySpan<byte> p)
    {
        if (p.Length < 1 || p.Length != 1 + p[0] * ContactSize)
        {
            throw new FormatException($"Touch の長さが合わない({p.Length} バイト)");
        }

        var points = new TouchPoint[p[0]];
        for (var i = 0; i < points.Length; i++)
        {
            var c = p.Slice(1 + i * ContactSize, ContactSize);
            if (c[1] > (byte)TouchAction.Cancel)
            {
                throw new FormatException($"Touch の action が {c[1]}");
            }

            points[i] = new TouchPoint(c[0], (TouchAction)c[1], BinaryPrimitives.ReadUInt16BigEndian(c[2..]), BinaryPrimitives.ReadUInt16BigEndian(c[4..]));
        }

        return points;
    }

    public static byte[] Encode(IReadOnlyList<TouchPoint> points)
    {
        var p = new byte[1 + points.Count * ContactSize];
        p[0] = checked((byte)points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var c = p.AsSpan(1 + i * ContactSize);
            c[0] = points[i].Id;
            c[1] = (byte)points[i].Action;
            BinaryPrimitives.WriteUInt16BigEndian(c[2..], points[i].X);
            BinaryPrimitives.WriteUInt16BigEndian(c[4..], points[i].Y);
        }

        return p;
    }
}

public static class CoordinateMapper
{
    /// <summary>§9.8: px = left + round(x * (width - 1) / 65535)。</summary>
    public static (int X, int Y) Map(ushort x, ushort y, ScreenRect r) =>
        (r.Left + (int)Math.Round(x * (double)(r.Width - 1) / 65535, MidpointRounding.AwayFromZero),
         r.Top + (int)Math.Round(y * (double)(r.Height - 1) / 65535, MidpointRounding.AwayFromZero));
}

/// <summary>接点の表。端末の id ごとに接点の番号(0〜9)と位置を持ち、注入に渡す「全部の接点」の配列を組み立てる。</summary>
public sealed class ContactTable
{
    public const int MaxContacts = 10;

    private readonly Dictionary<byte, (int Slot, int X, int Y)> _live = [];

    public int Count => _live.Count;

    /// <summary>枠 1 つ分の変化を入れる。変化が無ければ空。変化があれば、表の全部の接点(変化の無い物は Hold)。</summary>
    public List<InjectedContact> Apply(IEnumerable<TouchPoint> points, ScreenRect rect)
    {
        var changed = new Dictionary<int, InjectedContact>();
        var ended = new List<byte>();
        foreach (var p in points)
        {
            var (x, y) = CoordinateMapper.Map(p.X, p.Y, rect);
            var known = _live.TryGetValue(p.Id, out var c);
            switch (p.Action)
            {
                case TouchAction.Down when !known:
                    if (_live.Count >= MaxContacts)
                    {
                        break; // 11 点目は無視(FR-21)
                    }

                    var slot = Enumerable.Range(0, MaxContacts).First(s => _live.Values.All(v => v.Slot != s));
                    _live[p.Id] = (slot, x, y);
                    changed[slot] = new InjectedContact(slot, x, y, ContactChange.Down);
                    break;
                case TouchAction.Down or TouchAction.Move when known:
                    _live[p.Id] = (c.Slot, x, y);
                    var keepDown = changed.TryGetValue(c.Slot, out var prev) && prev.Change == ContactChange.Down;
                    changed[c.Slot] = new InjectedContact(c.Slot, x, y, keepDown ? ContactChange.Down : ContactChange.Move);
                    break;
                case TouchAction.Up or TouchAction.Cancel when known:
                    // 離すときの位置は最後に押していた位置(取り消しは位置を問わない)
                    changed[c.Slot] = new InjectedContact(c.Slot, c.X, c.Y, p.Action == TouchAction.Up ? ContactChange.Up : ContactChange.Cancel);
                    ended.Add(p.Id);
                    break;
            }
        }

        if (changed.Count == 0)
        {
            return [];
        }

        var result = _live.Values.Where(v => !changed.ContainsKey(v.Slot)).Select(v => new InjectedContact(v.Slot, v.X, v.Y, ContactChange.Hold))
            .Concat(changed.Values).OrderBy(v => v.Slot).ToList();
        foreach (var id in ended)
        {
            _live.Remove(id);
        }

        return result;
    }

    /// <summary>押したままの接点を、同じ位置の更新として全部。</summary>
    public List<InjectedContact> Refresh() => [.. _live.Values.OrderBy(v => v.Slot).Select(v => new InjectedContact(v.Slot, v.X, v.Y, ContactChange.Hold))];

    /// <summary>全部の接点の取り消し。表は空になる。</summary>
    public List<InjectedContact> CancelAll()
    {
        var result = _live.Values.OrderBy(v => v.Slot).Select(v => new InjectedContact(v.Slot, v.X, v.Y, ContactChange.Cancel)).ToList();
        _live.Clear();
        return result;
    }
}

/// <summary>§9.8 の注入の流れ。Begin でセッションを始め、End で終える(押したままの接点は取り消す。INV-7)。</summary>
public sealed class TouchRelay(ITouchInjector injector, IClock clock, Action<string> log)
{
    public const long SilenceUs = 2_000_000;
    private const int MaxFailures = 10;
    private const long FailureWindowUs = 10_000_000;

    private readonly ContactTable _table = new();
    private readonly Queue<long> _failures = new();
    private ScreenRect? _rect;
    private long _lastFrameUs;
    private long _lastInjectUs;

    /// <summary>注入の失敗が続いて、自分で切った(§9.8)。呼ぶ側は設定と端末への Notice に反映する。</summary>
    public event Action? AutoDisabled;

    public bool Enabled { get; private set; } = true;

    public int RefreshMs { get; set; } = 100;

    /// <summary>Touch の枠を受けてから注入が戻るまで(§9.9)。</summary>
    public LatencyStats Latency { get; } = new();

    public int ActiveContacts => _table.Count;

    public void Begin(ScreenRect? rect)
    {
        CancelAll();
        _rect = rect;
        _lastFrameUs = clock.NowUs;
    }

    /// <summary>映す画面が変わった。押したままの接点は元の画面の上で取り消す。</summary>
    public void SetRect(ScreenRect? rect)
    {
        if (rect != _rect)
        {
            CancelAll();
            _rect = rect;
        }
    }

    public void End()
    {
        CancelAll();
        _rect = null;
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            CancelAll();
        }

        Enabled = enabled;
    }

    /// <summary>端末から何かの枠が届いた(2 秒の時間切れの見張り)。</summary>
    public void OnFrameSeen() => _lastFrameUs = clock.NowUs;

    public void OnTouch(ReadOnlySpan<byte> payload, long receivedUs)
    {
        OnFrameSeen();
        if (!Enabled || _rect is not { } rect)
        {
            return; // FR-22
        }

        TouchPoint[] points;
        try
        {
            points = TouchCodec.Parse(payload);
        }
        catch (FormatException ex)
        {
            log($"Touch を読めない: {ex.Message}");
            return;
        }

        if (Inject(_table.Apply(points, rect)))
        {
            Latency.Add(clock.NowUs - receivedUs);
        }
    }

    /// <summary>注入のスレッドが細かく呼ぶ。押したままの接点の送り直しと、2 秒の無通信での取り消し。</summary>
    public void Tick()
    {
        if (_table.Count == 0)
        {
            return;
        }

        var now = clock.NowUs;
        if (now - _lastFrameUs > SilenceUs)
        {
            log("端末から 2 秒枠が来ないので、押したままの接点を取り消した");
            CancelAll();
        }
        else if (now - _lastInjectUs >= RefreshMs * 1000L)
        {
            Inject(_table.Refresh());
        }
    }

    private void CancelAll()
    {
        var all = _table.CancelAll();
        if (all.Count > 0)
        {
            Inject(all, countFailure: false);
        }
    }

    private bool Inject(List<InjectedContact> contacts, bool countFailure = true)
    {
        if (contacts.Count == 0)
        {
            return false;
        }

        _lastInjectUs = clock.NowUs;
        if (injector.Inject(contacts, out var error))
        {
            return true;
        }

        log($"タッチの注入に失敗した(エラー {error})。接点を全部取り消す");
        if (!countFailure)
        {
            return false;
        }

        CancelAll();
        var now = clock.NowUs;
        _failures.Enqueue(now);
        while (_failures.Peek() < now - FailureWindowUs)
        {
            _failures.Dequeue();
        }

        if (_failures.Count >= MaxFailures && Enabled)
        {
            _failures.Clear();
            Enabled = false;
            log($"{FailureWindowUs / 1_000_000} 秒の間に注入の失敗が {MaxFailures} 回続いたので、タッチを切った");
            AutoDisabled?.Invoke();
        }

        return false;
    }
}
