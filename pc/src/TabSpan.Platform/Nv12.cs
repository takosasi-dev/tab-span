// BGRA(8 ビット)から NV12 への色変換と縮小を 1 回で行う(BT.709 の限定レンジ)。CPU で、行の組ごとに並列に回す。
// 縮小は最近傍(ponytail: 文字がにじむなら面積平均にする)。色差は 2x2 の 4 画素の平均。出力の幅と高さは偶数。
namespace TabSpan.Platform;

public static class Nv12
{
    public static int Size(int width, int height) => width * height * 3 / 2;

    /// <summary>配列で呼ぶ版(テスト用)。</summary>
    public static unsafe void FromBgra(ReadOnlySpan<byte> bgra, int pitch, int sw, int sh, Span<byte> nv12, int dw, int dh)
    {
        if (bgra.Length < pitch * (sh - 1) + sw * 4 || nv12.Length < Size(dw, dh))
        {
            throw new ArgumentException("配列が大きさに足りない");
        }

        fixed (byte* s = bgra)
        fixed (byte* d = nv12)
        {
            FromBgra(s, pitch, sw, sh, d, dw, dh);
        }
    }

    public static unsafe void FromBgra(byte* source, int pitch, int sw, int sh, byte* destination, int dw, int dh)
    {
        if (dw <= 0 || dh <= 0 || (dw & 1) != 0 || (dh & 1) != 0 || sw <= 0 || sh <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dw), "出力の幅と高さは正の偶数");
        }

        var xs = new int[dw];
        for (var x = 0; x < dw; x++)
        {
            xs[x] = (int)((long)x * sw / dw) * 4;
        }

        var src = (nint)source;
        var dst = (nint)destination;
        Parallel.For(0, dh / 2, pair =>
        {
            var s = (byte*)src;
            var d = (byte*)dst;
            var y0 = pair * 2;
            var row0 = s + (nint)((long)y0 * sh / dh) * pitch;
            var row1 = s + (nint)((long)(y0 + 1) * sh / dh) * pitch;
            var luma0 = d + (nint)y0 * dw;
            var luma1 = luma0 + dw;
            var uv = d + (nint)dw * dh + (nint)pair * dw;
            for (var x = 0; x < dw; x += 2)
            {
                var a = row0 + xs[x];
                var b = row0 + xs[x + 1];
                var c = row1 + xs[x];
                var e = row1 + xs[x + 1];
                luma0[x] = Y(a);
                luma0[x + 1] = Y(b);
                luma1[x] = Y(c);
                luma1[x + 1] = Y(e);
                var bl = a[0] + b[0] + c[0] + e[0];
                var gr = a[1] + b[1] + c[1] + e[1];
                var rd = a[2] + b[2] + c[2] + e[2];
                uv[x] = (byte)((((-26 * rd) - (86 * gr) + (112 * bl) + 512) >> 10) + 128);
                uv[x + 1] = (byte)((((112 * rd) - (102 * gr) - (10 * bl) + 512) >> 10) + 128);
            }
        });
    }

    private static unsafe byte Y(byte* p) => (byte)((((47 * p[2]) + (157 * p[1]) + (16 * p[0]) + 128) >> 8) + 16);
}
