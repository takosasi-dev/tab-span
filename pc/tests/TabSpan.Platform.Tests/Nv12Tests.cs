// BGRA → NV12 の色変換と縮小(純粋な関数)を確かめる。等倍(--fps 30 で 1080p)と縮小(1080p → 720p)の両方。
namespace TabSpan.Platform.Tests;

public sealed class Nv12Tests
{
    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (b, g, r, 255);
        }

        return bgra;
    }

    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)] // 白
    [InlineData(0, 0, 0, 16, 128, 128)] // 黒
    [InlineData(0, 0, 255, 63, 102, 240)] // 赤(BT.709 の限定レンジ)
    public void Colors_follow_bt709_limited(byte b, byte g, byte r, byte y, byte u, byte v)
    {
        var nv12 = new byte[Nv12.Size(4, 2)];
        Nv12.FromBgra(Solid(4, 2, b, g, r), 16, 4, 2, nv12, 4, 2);
        Assert.All(nv12[..8], x => Assert.Equal(y, x));
        Assert.Equal([u, v, u, v], nv12[8..]);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(2560, 1600, 1920, 1200)]
    public void Scales_by_picking_source_pixels(int sw, int sh, int dw, int dh)
    {
        // 左半分が白、右半分が黒。行の幅に余りのある pitch でも読めること
        var pitch = sw * 4 + 64;
        var bgra = new byte[pitch * sh];
        for (var y = 0; y < sh; y++)
        {
            bgra.AsSpan(y * pitch, sw / 2 * 4).Fill(255);
        }

        var nv12 = new byte[Nv12.Size(dw, dh)];
        Nv12.FromBgra(bgra, pitch, sw, sh, nv12, dw, dh);
        for (var y = 0; y < dh; y += 97)
        {
            Assert.Equal(235, nv12[y * dw]);
            Assert.Equal(235, nv12[y * dw + dw / 2 - 1]);
            Assert.Equal(16, nv12[y * dw + dw / 2 + 1]);
            Assert.Equal(16, nv12[y * dw + dw - 1]);
        }
    }

    [Fact]
    public void Odd_output_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Nv12.FromBgra(new byte[16], 8, 2, 2, new byte[12], 3, 2));
}
