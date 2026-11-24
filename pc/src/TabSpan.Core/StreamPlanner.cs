// 送る大きさとフレームレートを決める(端末が復号できる最大に収め、拡大はしない。幅と高さは偶数)。
// 割り算は整数の切り捨てだけで行う。端末が何も復号できなければ null。
namespace TabSpan.Core;

public static class StreamPlanner
{
    public static SizeFps? Plan(int sw, int sh, IReadOnlyList<SizeFps> supported, int fpsCap, int maxLongEdge)
    {
        if (sw <= 0 || sh <= 0)
        {
            return null;
        }

        // 1. fps = fpsCap。その fps の候補が無ければ 30
        var fps = supported.Any(s => s.Fps == fpsCap) ? fpsCap : 30;

        // 2. その fps の候補のうち画素数が最大の物
        SizeFps? limit = null;
        foreach (var s in supported)
        {
            if (s.Fps == fps && s.Width > 0 && s.Height > 0
                && (limit is null || (long)s.Width * s.Height > (long)limit.Width * limit.Height))
            {
                limit = s;
            }
        }

        if (limit is null)
        {
            return null;
        }

        long lw = limit.Width, lh = limit.Height;

        // 3. 幅の上限
        var cap = maxLongEdge == 0 ? lw : Math.Min(lw, maxLongEdge);

        // 4. 大きさ
        long w, h;
        if (sw <= cap && sh <= lh)
        {
            (w, h) = (sw, sh); // 拡大しない
        }
        else if ((long)sw * lh >= (long)sh * cap)
        {
            (w, h) = (cap, sh * cap / sw); // 幅で決まる
        }
        else
        {
            (w, h) = (sw * lh / sh, lh); // 高さで決まる
        }

        // 5. 偶数に
        w = w / 2 * 2;
        h = h / 2 * 2;
        return w <= 0 || h <= 0 ? null : new SizeFps((int)w, (int)h, fps); // 負の maxLongEdge や小さすぎる画面
    }
}
