// H.264 のバイトの扱い: Annex B の NAL への分け方と、SPS の VUI の書き換え(CamPane のテストを写して直した物)。
namespace TabSpan.Core.Tests;

public class AnnexBTests
{
    private static byte[][] Split(byte[] data) => [.. AnnexB.SplitNals(data).Select(m => m.ToArray())];

    [Fact]
    public void 開始コードが_3_バイトでも_4_バイトでも区切り_開始コードは含めない()
    {
        byte[] data = [0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9];

        Assert.Equal([[0x67, 1, 2], [0x68, 3], [0x65, 9]], Split(data));
    }

    [Fact]
    public void 先頭のごみ_後ろの_0_空の_NAL_は捨て_開始コードが無ければ空()
    {
        Assert.Equal([[0x41, 7]], Split([9, 9, 0, 0, 1, 0x41, 7, 0, 0]));
        Assert.Equal([[0x41]], Split([0, 0, 1, 0, 0, 1, 0x41]));
        Assert.Empty(Split([1, 2, 3, 0, 0, 2]));
        Assert.Empty(Split([]));
        Assert.Empty(Split([0, 0, 1]));
    }

    [Fact]
    public void 中身の_00_00_03_は区切りにしない()
    {
        Assert.Equal([[0x65, 0, 0, 3, 1, 5]], Split([0, 0, 1, 0x65, 0, 0, 3, 1, 5]));
    }
}

public class SpsVuiTests
{
    // SPS のバイトだけ。dtab = 端末の撮った映像の先頭、ほかは libx264 で合成の絵から作った物。
    private static readonly byte[] DtabSps = Convert.FromHexString("6742e033ab402802dc80");
    private static readonly byte[] MainSps = Convert.FromHexString("674d401fd9005005bb0110000003001000000303c0f1832480");
    private static readonly byte[] HighSps = Convert.FromHexString("6764000dacb20283f60220000003002000000781e28549");
    private static readonly byte[] High444Sps = Convert.FromHexString("67f4000d919b28283f6022000003000200000300781e28532c");

    [Fact]
    public void VUI_の無い_SPS_に並べ替えなしの_bitstream_restriction_を足し_読み直して合う()
    {
        var before = SpsVui.Parse(DtabSps);
        Assert.Equal((66, 0, 1, false), (before.ProfileIdc, before.PicOrderCntType, before.MaxNumRefFrames, before.Vui));

        var after = SpsVui.Parse(SpsVui.AddNoReorder(DtabSps));

        Assert.Equal(before with { Vui = true, BitstreamRestriction = true, MaxNumReorderFrames = 0, MaxDecFrameBuffering = 1, RestrictionFlagBit = after.RestrictionFlagBit }, after);
    }

    [Theory]
    [InlineData("main", 77, 1, 3, 79, 44, 11, 0)]
    [InlineData("high", 100, 1, 3, 19, 14, 9, 0)]
    [InlineData("high444", 244, 3, 4, 19, 14, 9, 2)]
    public void VUI_のある_SPS_は_bitstream_restriction_だけを直し_ほかは_ビットのまま残す(
        string name, int profile, int chroma, int maxRef, int width, int height, int mvLength, int reorder)
    {
        var sps = name switch { "main" => MainSps, "high" => HighSps, _ => High444Sps };
        var before = SpsVui.Parse(sps);
        // 期待の値は ffmpeg の -bsf:v trace_headers で読んだ物
        Assert.Equal(
            (profile, chroma, maxRef, width, height, true, true, mvLength, reorder, maxRef),
            (before.ProfileIdc, before.ChromaFormatIdc, before.MaxNumRefFrames, before.WidthInMbsMinus1, before.HeightInMapUnitsMinus1,
             before.Vui, before.BitstreamRestriction, before.Log2MaxMvLengthHorizontal, before.MaxNumReorderFrames, before.MaxDecFrameBuffering));

        var nal = SpsVui.AddNoReorder(sps);
        var after = SpsVui.Parse(nal);

        Assert.Equal(before with { MaxNumReorderFrames = 0, MaxDecFrameBuffering = maxRef }, after);
        var whole = before.RestrictionFlagBit / 8;
        Assert.Equal(SpsVui.Unescape(sps)[..whole], SpsVui.Unescape(nal)[..whole]);
    }

    [Fact]
    public void 二度かけても同じ()
    {
        var once = SpsVui.AddNoReorder(MainSps);
        Assert.Equal(once, SpsVui.AddNoReorder(once));
    }

    [Fact]
    public void scaling_matrix_のある_SPS_は写しをそのまま返す()
    {
        // high(100)、sps_id 0、chroma 1、bit_depth 0/0、qpprime 0、seq_scaling_matrix_present_flag 1 → 1 010 1 1 0 1
        byte[] sps = [0x67, 0x64, 0x00, 0x1f, 0xAD];
        Assert.True(SpsVui.Parse(sps).ScalingMatrix);

        var result = SpsVui.AddNoReorder(sps);

        Assert.Equal(sps, result);
        Assert.NotSame(sps, result);
    }

    [Fact]
    public void SPS_でない_途中で切れた_NAL_は_InvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => SpsVui.AddNoReorder([0x68, 0xee, 0x3c, 0x80]));
        Assert.Throws<InvalidDataException>(() => SpsVui.AddNoReorder(DtabSps.AsSpan(0, 4)));
    }

    [Fact]
    public void エミュレーション防止の_03_を外して_付け直すと元に戻る()
    {
        byte[] rbsp = [0x67, 0, 0, 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 4, 9];

        var escaped = SpsVui.Escape(rbsp);

        Assert.Equal([0x67, 0, 0, 3, 0, 0, 3, 0, 1, 0, 0, 3, 2, 0, 0, 3, 3, 0, 0, 4, 9], escaped);
        Assert.Equal(rbsp, SpsVui.Unescape(escaped));
    }
}
