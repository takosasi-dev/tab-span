// Media Foundation の H.264 エンコーダーの一覧(名前・ハードウェアかどうか)と、ICodecAPI::IsSupported で調べたプロパティを出す(仕様書 §8.1)。
// 調べるプロパティは、Microsoft Learn「H.264 Video Encoder」が挙げている物。名前は Win32 のメタデータの定数から取る。
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace TabSpan.Probe;

internal static class EncodersProbe
{
    private static readonly (string Group, string Name, Guid Api)[] Properties =
    [
        ("低遅延", "CODECAPI_AVLowLatencyMode", typeof(CODECAPI_AVLowLatencyMode).GUID),
        ("レート制御", "CODECAPI_AVEncCommonRateControlMode", typeof(CODECAPI_AVEncCommonRateControlMode).GUID),
        ("レート制御", "CODECAPI_AVEncCommonQuality", typeof(CODECAPI_AVEncCommonQuality).GUID),
        ("レート制御", "CODECAPI_AVEncCommonBufferSize", typeof(CODECAPI_AVEncCommonBufferSize).GUID),
        ("レート制御", "CODECAPI_AVEncVideoEncodeQP", typeof(CODECAPI_AVEncVideoEncodeQP).GUID),
        ("レート制御", "CODECAPI_AVEncVideoMinQP", typeof(CODECAPI_AVEncVideoMinQP).GUID),
        ("レート制御", "CODECAPI_AVEncVideoMaxQP", typeof(CODECAPI_AVEncVideoMaxQP).GUID),
        ("レート制御", "CODECAPI_AVEncVideoEncodeFrameTypeQP", typeof(CODECAPI_AVEncVideoEncodeFrameTypeQP).GUID),
        ("レート制御", "CODECAPI_AVEncAdaptiveMode", typeof(CODECAPI_AVEncAdaptiveMode).GUID),
        ("ビットレート", "CODECAPI_AVEncCommonMeanBitRate", typeof(CODECAPI_AVEncCommonMeanBitRate).GUID),
        ("ビットレート", "CODECAPI_AVEncCommonMaxBitRate", typeof(CODECAPI_AVEncCommonMaxBitRate).GUID),
        ("キーフレーム", "CODECAPI_AVEncVideoForceKeyFrame", typeof(CODECAPI_AVEncVideoForceKeyFrame).GUID),
        ("キーフレーム", "CODECAPI_AVEncMPVGOPSize", typeof(CODECAPI_AVEncMPVGOPSize).GUID),
        ("並べ替え", "CODECAPI_AVEncMPVDefaultBPictureCount", typeof(CODECAPI_AVEncMPVDefaultBPictureCount).GUID),
        ("そのほか", "CODECAPI_AVEncCommonQualityVsSpeed", typeof(CODECAPI_AVEncCommonQualityVsSpeed).GUID),
        ("そのほか", "CODECAPI_AVEncH264CABACEnable", typeof(CODECAPI_AVEncH264CABACEnable).GUID),
        ("そのほか", "CODECAPI_AVEncNumWorkerThreads", typeof(CODECAPI_AVEncNumWorkerThreads).GUID),
        ("そのほか", "CODECAPI_AVEncVideoContentType", typeof(CODECAPI_AVEncVideoContentType).GUID),
        ("そのほか", "CODECAPI_AVEncVideoMaxNumRefFrame", typeof(CODECAPI_AVEncVideoMaxNumRefFrame).GUID),
        ("そのほか", "CODECAPI_AVEncVideoLTRBufferControl", typeof(CODECAPI_AVEncVideoLTRBufferControl).GUID),
        ("そのほか", "CODECAPI_AVEncVideoTemporalLayerCount", typeof(CODECAPI_AVEncVideoTemporalLayerCount).GUID),
        ("そのほか", "CODECAPI_AVEncSliceControlMode", typeof(CODECAPI_AVEncSliceControlMode).GUID),
        ("そのほか", "CODECAPI_AVEncSliceControlSize", typeof(CODECAPI_AVEncSliceControlSize).GUID),
    ];

    internal static int Run()
    {
        if (!Mf.Startup())
        {
            return 1;
        }

        var encoders = Mf.EnumerateH264();
        Console.WriteLine($"H.264 のエンコーダー: {encoders.Count} 件(MFTEnumEx が返した順)");
        foreach (var encoder in encoders)
        {
            Console.WriteLine();
            Console.WriteLine($"名前: {encoder.Name}");
            Console.WriteLine($"種類: {encoder.Kind}  (MF_TRANSFORM_FLAGS_Attribute = 0x{(uint)encoder.Flags:X} {encoder.Flags})");
            Console.WriteLine($"CLSID: {Attempt.Read(() =>
            {
                encoder.Activate.GetGUID(PInvoke.MFT_TRANSFORM_CLSID_Attribute, out var clsid);
                return clsid.ToString();
            })}");
            if (encoder.Hardware)
            {
                Console.WriteLine($"ベンダー ID: {Attempt.Read(() =>
                {
                    encoder.Activate.GetAllocatedString(PInvoke.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute, out var text, out _);
                    var value = text.ToString();
                    unsafe
                    {
                        PInvoke.CoTaskMemFree(text.Value);
                    }

                    return value;
                })}");
            }

            var transform = Mf.Activate(encoder, out _);
            if (transform is null)
            {
                continue;
            }

            ICodecAPI codec = null!;
            if (Attempt.Step("ICodecAPI を取る", () => codec = (ICodecAPI)transform))
            {
                foreach (var (group, name, api) in Properties)
                {
                    // 使えるのは S_OK のときだけ(S_FALSE などの成功コードは「使える」に数えない)
                    var supported = codec.IsSupported(api);
                    Console.WriteLine(supported == HRESULT.S_OK
                        ? $"  [{group}] {name}: 使える (IsSupported {supported}、IsModifiable {codec.IsModifiable(api)})"
                        : $"  [{group}] {name}: 使えない (IsSupported {supported})");
                }
            }

            Mf.Release(encoder, transform);
        }

        Mf.Shutdown();
        return 0;
    }
}
