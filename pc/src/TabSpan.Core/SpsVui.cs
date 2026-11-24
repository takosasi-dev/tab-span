// H.264 の SPS を読み、VUI に bitstream_restriction(並べ替えなし)を足して書き直す(端末のデコーダーが絵を溜めないように)。
// エミュレーション防止の 0x03、Exp-Golomb、rbsp_trailing_bits を扱う(H.264 7.3.2.1.1、E.1.1)。VUI のほかの項目はビットのまま写す。
// CamPane(同じ作者、MIT)の Sps.cs を写した物。
namespace TabSpan.Core;

/// <summary>SPS の値のうち、溜めの判断と書き換えに使う物。…Bit は RBSP(NAL ヘッダーを含む)の先頭からのビットの位置。</summary>
internal sealed record SpsInfo
{
    internal int ProfileIdc { get; init; }
    internal int ChromaFormatIdc { get; init; } = 1;
    internal bool ScalingMatrix { get; init; }
    internal int PicOrderCntType { get; init; }
    internal int MaxNumRefFrames { get; init; }
    internal int WidthInMbsMinus1 { get; init; }
    internal int HeightInMapUnitsMinus1 { get; init; }
    internal bool Vui { get; init; }
    internal bool BitstreamRestriction { get; init; }
    internal bool MotionVectorsOverPicBoundaries { get; init; } = true; // 無いときの値(E.2.1)
    internal int MaxBytesPerPicDenom { get; init; } = 2;
    internal int MaxBitsPerMbDenom { get; init; } = 1;
    internal int Log2MaxMvLengthHorizontal { get; init; } = 15;
    internal int Log2MaxMvLengthVertical { get; init; } = 15;
    internal int MaxNumReorderFrames { get; init; } = -1; // -1 = 書かれていない
    internal int MaxDecFrameBuffering { get; init; } = -1;
    internal int VuiFlagBit { get; init; }
    internal int RestrictionFlagBit { get; init; }
}

public static class SpsVui
{
    /// <summary>
    /// SPS の NAL(開始コードなし、NAL ヘッダーから)の VUI に bitstream_restriction を足す/直す:
    /// max_num_reorder_frames = 0、max_dec_frame_buffering = max_num_ref_frames。
    /// scaling matrix のある SPS は書き換えられないので、入力の写しをそのまま返す。
    /// SPS でない・読めない(途中で切れている、終わりが合わない)ときは InvalidDataException。
    /// </summary>
    public static byte[] AddNoReorder(ReadOnlySpan<byte> spsNal) => AddReorderLimit(spsNal).Nal ?? spsNal.ToArray();

    // chroma_format_idc などが入るプロファイル(7.3.2.1.1)
    private static readonly int[] HighProfiles = [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135];

    /// <summary>SPS の NAL(開始コードを除き、NAL ヘッダーから)を読む。</summary>
    internal static SpsInfo Parse(ReadOnlySpan<byte> nal)
    {
        var r = new BitReader(Unescape(nal));
        if ((r.Read(8) & 0x1F) != 7)
        {
            throw new InvalidDataException("SPS(nal_unit_type 7)ではない");
        }

        var profile = (int)r.Read(8);
        r.Read(8); // constraint_set0〜5_flag と reserved_zero_2bits
        r.Read(8); // level_idc
        r.Ue();    // seq_parameter_set_id
        var chroma = 1;
        if (HighProfiles.Contains(profile))
        {
            chroma = (int)r.Ue();
            if (chroma == 3)
            {
                r.Flag(); // separate_colour_plane_flag
            }

            r.Ue();   // bit_depth_luma_minus8
            r.Ue();   // bit_depth_chroma_minus8
            r.Flag(); // qpprime_y_zero_transform_bypass_flag
            if (r.Flag())
            {
                // seq_scaling_matrix_present_flag。scaling_list は読まず、書き換えもしない。
                return new SpsInfo { ProfileIdc = profile, ChromaFormatIdc = chroma, ScalingMatrix = true };
            }
        }

        r.Ue(); // log2_max_frame_num_minus4
        var poc = (int)r.Ue();
        if (poc == 0)
        {
            r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (poc == 1)
        {
            r.Flag(); // delta_pic_order_always_zero_flag
            r.Se();   // offset_for_non_ref_pic
            r.Se();   // offset_for_top_to_bottom_field
            var cycle = r.Ue();
            if (cycle > 255)
            {
                throw new InvalidDataException($"num_ref_frames_in_pic_order_cnt_cycle {cycle} は 255 を超える");
            }

            for (var i = 0; i < cycle; i++)
            {
                r.Se(); // offset_for_ref_frame
            }
        }

        var maxRef = (int)r.Ue();
        r.Flag(); // gaps_in_frame_num_value_allowed_flag
        var width = (int)r.Ue();
        var height = (int)r.Ue();
        if (!r.Flag())
        {
            r.Flag(); // frame_mbs_only_flag が 0 なら mb_adaptive_frame_field_flag
        }

        r.Flag(); // direct_8x8_inference_flag
        if (r.Flag())
        {
            r.Ue(); r.Ue(); r.Ue(); r.Ue(); // frame_crop_left/right/top/bottom_offset
        }

        var info = new SpsInfo
        {
            ProfileIdc = profile,
            ChromaFormatIdc = chroma,
            PicOrderCntType = poc,
            MaxNumRefFrames = maxRef,
            WidthInMbsMinus1 = width,
            HeightInMapUnitsMinus1 = height,
            VuiFlagBit = r.Position,
        };
        if (r.Flag())
        {
            info = ReadVui(r, info with { Vui = true });
        }

        // rbsp_trailing_bits: 1 が 1 つ、あとは 0(後ろの 0 のバイトも許す)
        if (!r.Flag() || !r.RestIsZero())
        {
            throw new InvalidDataException("SPS の終わり(rbsp_trailing_bits)が合わない。読み違えている");
        }

        return info;
    }

    // E.1.1 vui_parameters。bitstream_restriction_flag の手前までは、位置だけ分かればよい。
    private static SpsInfo ReadVui(BitReader r, SpsInfo info)
    {
        if (r.Flag() && r.Read(8) == 255) // aspect_ratio_info_present_flag、aspect_ratio_idc = Extended_SAR
        {
            r.Read(16); // sar_width
            r.Read(16); // sar_height
        }

        if (r.Flag())
        {
            r.Flag(); // overscan_appropriate_flag
        }

        if (r.Flag()) // video_signal_type_present_flag
        {
            r.Read(3); // video_format
            r.Flag();  // video_full_range_flag
            if (r.Flag())
            {
                r.Read(24); // colour_primaries、transfer_characteristics、matrix_coefficients
            }
        }

        if (r.Flag())
        {
            r.Ue(); // chroma_sample_loc_type_top_field
            r.Ue(); // chroma_sample_loc_type_bottom_field
        }

        if (r.Flag()) // timing_info_present_flag
        {
            r.Read(32); // num_units_in_tick
            r.Read(32); // time_scale
            r.Flag();   // fixed_frame_rate_flag
        }

        var nalHrd = r.Flag();
        if (nalHrd)
        {
            SkipHrd(r);
        }

        var vclHrd = r.Flag();
        if (vclHrd)
        {
            SkipHrd(r);
        }

        if (nalHrd || vclHrd)
        {
            r.Flag(); // low_delay_hrd_flag
        }

        r.Flag(); // pic_struct_present_flag
        info = info with { RestrictionFlagBit = r.Position };
        if (!r.Flag())
        {
            return info;
        }

        return info with
        {
            BitstreamRestriction = true,
            MotionVectorsOverPicBoundaries = r.Flag(),
            MaxBytesPerPicDenom = (int)r.Ue(),
            MaxBitsPerMbDenom = (int)r.Ue(),
            Log2MaxMvLengthHorizontal = (int)r.Ue(),
            Log2MaxMvLengthVertical = (int)r.Ue(),
            MaxNumReorderFrames = (int)r.Ue(),
            MaxDecFrameBuffering = (int)r.Ue(),
        };
    }

    // E.1.2 hrd_parameters
    private static void SkipHrd(BitReader r)
    {
        var count = r.Ue() + 1; // cpb_cnt_minus1 + 1
        if (count > 32)
        {
            throw new InvalidDataException($"cpb_cnt_minus1 {count - 1} は 31 を超える");
        }

        r.Read(8); // bit_rate_scale、cpb_size_scale
        for (var i = 0; i < count; i++)
        {
            r.Ue();   // bit_rate_value_minus1
            r.Ue();   // cpb_size_value_minus1
            r.Flag(); // cbr_flag
        }

        r.Read(20); // initial_cpb_removal_delay_length_minus1 ほか 5 ビット × 4
    }

    /// <summary>
    /// VUI の bitstream_restriction を max_num_reorder_frames = 0、max_dec_frame_buffering = max_num_ref_frames にした SPS を返す。
    /// VUI が無ければ、ほかの項目を全部「無し」にした VUI を足す。ある場合は bitstream_restriction より前をビットのまま写す。
    /// 書き換えないときは null と、その理由。
    /// </summary>
    internal static (byte[]? Nal, string Note) AddReorderLimit(ReadOnlySpan<byte> nal)
    {
        var info = Parse(nal);
        if (info.ScalingMatrix)
        {
            return (null, $"scaling matrix がある(profile_idc {info.ProfileIdc})ので書き換えない");
        }

        var rbsp = Unescape(nal);
        var w = new BitWriter();
        if (info.Vui)
        {
            w.Copy(rbsp, info.RestrictionFlagBit);
        }
        else
        {
            w.Copy(rbsp, info.VuiFlagBit);
            w.Write(1, 1); // vui_parameters_present_flag
            w.Write(0, 8); // aspect_ratio_info ほか、pic_struct_present_flag までの 8 つの flag を 0
        }

        w.Write(1, 1); // bitstream_restriction_flag
        w.Write(info.MotionVectorsOverPicBoundaries ? 1u : 0u, 1);
        w.Ue((uint)info.MaxBytesPerPicDenom);
        w.Ue((uint)info.MaxBitsPerMbDenom);
        w.Ue((uint)info.Log2MaxMvLengthHorizontal);
        w.Ue((uint)info.Log2MaxMvLengthVertical);
        w.Ue(0);                           // max_num_reorder_frames
        w.Ue((uint)info.MaxNumRefFrames);  // max_dec_frame_buffering
        var result = Escape(w.Finish());

        var before = info.BitstreamRestriction
            ? $"VUI あり、bitstream_restriction あり(max_num_reorder_frames {info.MaxNumReorderFrames}、max_dec_frame_buffering {info.MaxDecFrameBuffering})"
            : info.Vui ? "VUI あり、bitstream_restriction 無し" : "VUI 無し";
        var after = Parse(result);
        return (result,
            $"profile_idc {info.ProfileIdc}、max_num_ref_frames {info.MaxNumRefFrames}、{before} → 読み直すと " +
            $"max_num_reorder_frames {after.MaxNumReorderFrames}、max_dec_frame_buffering {after.MaxDecFrameBuffering}");
    }

    /// <summary>00 00 03 の 03(エミュレーション防止)を外す。</summary>
    internal static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        var output = new List<byte>(nal.Length);
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }

            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return output.ToArray();
    }

    /// <summary>00 00 の後に 00〜03 が来る所に 03 を挟む。</summary>
    internal static byte[] Escape(ReadOnlySpan<byte> rbsp)
    {
        var output = new List<byte>(rbsp.Length + 4);
        var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                output.Add(3);
                zeros = 0;
            }

            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return output.ToArray();
    }

    private sealed class BitReader(byte[] data)
    {
        internal int Position { get; private set; }

        internal uint Read(int count)
        {
            if (Position + count > data.Length * 8)
            {
                throw new InvalidDataException("SPS が途中で切れている");
            }

            ulong value = 0;
            for (var i = 0; i < count; i++, Position++)
            {
                value = (value << 1) | (uint)((data[Position >> 3] >> (7 - (Position & 7))) & 1);
            }

            return (uint)value;
        }

        internal bool Flag() => Read(1) == 1;

        // Exp-Golomb(9.1)
        internal uint Ue()
        {
            var zeros = 0;
            while (Read(1) == 0)
            {
                if (++zeros > 31)
                {
                    throw new InvalidDataException("Exp-Golomb の 0 が 31 個を超える");
                }
            }

            return (uint)((1UL << zeros) - 1 + Read(zeros));
        }

        internal int Se()
        {
            var k = Ue();
            return (k & 1) == 1 ? (int)((k + 1) / 2) : -(int)(k / 2);
        }

        internal bool RestIsZero()
        {
            while (Position < data.Length * 8)
            {
                if (Read(1) != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits; // 書いたビットの数

        internal void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--, _bits++)
            {
                if ((_bits & 7) == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> (_bits & 7));
                }
            }
        }

        internal void Ue(uint value)
        {
            var code = (ulong)value + 1;
            var length = 64 - System.Numerics.BitOperations.LeadingZeroCount(code);
            Write(0, length - 1);
            for (var i = length - 1; i >= 0; i--)
            {
                Write((uint)((code >> i) & 1), 1);
            }
        }

        /// <summary>source の先頭から count ビットを、そのまま写す。</summary>
        internal void Copy(byte[] source, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Write((uint)((source[i >> 3] >> (7 - (i & 7))) & 1), 1);
            }
        }

        /// <summary>rbsp_trailing_bits(1 と、バイトの区切りまでの 0)を付けて返す。</summary>
        internal byte[] Finish()
        {
            Write(1, 1);
            while ((_bits & 7) != 0)
            {
                Write(0, 1);
            }

            return [.. _bytes];
        }
    }
}
