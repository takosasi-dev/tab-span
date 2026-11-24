// OS に入っているソフトウェアの H.264 エンコーダー(H264 Encoder MFT)に合成の NV12 を流し、実物の出力の形を確かめる。
// 画面の取り込みも窓も使わない。分かったこと: 入れた 1 枚に 1 枚が返るか、キーフレームに SPS/PPS/IDR が付くか、
// MF_MT_MPEG_SEQUENCE_HEADER があるか、ForceKeyFrame が効くか、実物の SPS を AddNoReorder が通せるか。
using TabSpan.Core;
using Windows.Win32;
using Xunit.Abstractions;

namespace TabSpan.Platform.Tests;

public sealed class RealEncoderTests(ITestOutputHelper output)
{
    private static byte[] Picture(int w, int h, int t)
    {
        var nv12 = new byte[Nv12.Size(w, h)];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                nv12[y * w + x] = (byte)(16 + ((x + y + t * 8) & 0xFF) * 219 / 255);
            }
        }

        nv12.AsSpan(w * h).Fill(128);
        return nv12;
    }

    private static int[] Types(byte[] annexB) => [.. AnnexB.SplitNals(annexB).Select(n => n.Span[0] & 0x1F)];

    [Theory]
    [InlineData("high")]
    [InlineData("main")]
    [InlineData("baseline")]
    public void Software_mft_gives_one_annexb_frame_per_input_with_sps_pps_on_keyframes(string profile)
    {
        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
        try
        {
            var software = H264Candidate.Enumerate().First(c => !c.Hardware);
            using var encoder = new MfH264Encoder(software, new EncoderSettings(1280, 720, 30, 4_000_000, profile));
            var header = encoder.SequenceHeader();
            output.WriteLine($"{software.Name} / {profile}: 始める前の MF_MT_MPEG_SEQUENCE_HEADER = {(header is null ? "無し" : Convert.ToHexString(header))}");

            var frames = new List<EncodedFrame>();
            for (var i = 0; i < 12; i++)
            {
                frames.Add(encoder.Encode(Picture(1280, 720, i), i * 33_333L, forceKey: i is 0 or 7)
                    ?? throw new Xunit.Sdk.XunitException($"{i} 枚目の出力が無い(1 枚に 1 枚が返っていない)"));
            }

            header = encoder.SequenceHeader();
            output.WriteLine($"後の MF_MT_MPEG_SEQUENCE_HEADER = {(header is null ? "無し" : Convert.ToHexString(header))}");
            for (var i = 0; i < frames.Count; i++)
            {
                output.WriteLine($"{i}: key={frames[i].Key} NAL={string.Join(",", Types(frames[i].AnnexB))} {frames[i].AnnexB.Length} バイト 先頭 {Convert.ToHexString(frames[i].AnnexB, 0, 5)}");
            }

            Assert.All(frames, f => Assert.True(f.AnnexB.AsSpan(0, 4).SequenceEqual((byte[])[0, 0, 0, 1]) || f.AnnexB.AsSpan(0, 3).SequenceEqual((byte[])[0, 0, 1])));
            Assert.True(frames[0].Key);
            Assert.True(frames[7].Key);
            Assert.Contains(7, Types(frames[0].AnnexB));
            Assert.Contains(8, Types(frames[0].AnnexB));
            Assert.Contains(7, Types(frames[7].AnnexB));
            Assert.False(frames[1].Key);

            var sets = MirrorStreamer.ExtractParameterSets(frames[0].AnnexB);
            Assert.NotNull(sets);
            var rawSps = AnnexB.SplitNals(frames[0].AnnexB).First(n => (n.Span[0] & 0x1F) == 7).ToArray();
            Assert.Equal(profile switch { "high" => 100, "main" => 77, _ => 66 }, rawSps[1]);
            output.WriteLine($"SPS {Convert.ToHexString(rawSps)} → {Convert.ToHexString(sets.Value.Sps)}");
            // 実物: このエンコーダーの SPS は、もともと VUI に bitstream_restriction(並べ替え 0)を持っていて、書き直しても同じバイトになる。
            // 書き直しが同じ形で落ち着くこと(2 回かけても変わらない)と、例外を出さずに読めることを確かめる
            Assert.Equal(sets.Value.Sps, SpsVui.AddNoReorder(sets.Value.Sps));
        }
        finally
        {
            PInvoke.MFShutdown();
        }
    }

    [Fact]
    public void Auto_encoder_without_hardware_settles_on_software()
    {
        var logs = new List<string>();
        using var encoder = new AutoH264Encoder(new EncoderSettings(640, 360, 30, 2_000_000, "main"), allowHardware: false, logs.Add);
        var first = encoder.Encode(Picture(640, 360, 0), 0, forceKey: false);
        Assert.NotNull(first);
        Assert.True(first.Key); // 採用前は必ずキーフレームを頼む
        Assert.False(encoder.IsHardware);
        Assert.False(encoder.Encode(Picture(640, 360, 1), 33_333, forceKey: false)!.Key);
        output.WriteLine(string.Join("\n", logs));
    }
}
