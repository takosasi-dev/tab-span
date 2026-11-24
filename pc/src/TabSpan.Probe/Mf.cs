// Media Foundation の H.264 エンコーダーを数え上げる(MFTEnumEx)。encoders と encode が共通で使う。
// 並びは MFTEnumEx が返した順(ハードウェアが先)。名前と種類は、MFTEnumEx が付けた属性から読む。
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace TabSpan.Probe;

internal sealed record H264Encoder(IMFActivate Activate, string Name, MFT_ENUM_FLAG Flags)
{
    internal bool Hardware => Flags.HasFlag(MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE);

    internal bool Async => Flags.HasFlag(MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT);

    internal string Kind => $"{(Hardware ? "ハードウェア" : "ソフトウェア")}、{(Async ? "非同期の MFT" : "同期の MFT")}";
}

internal static class Mf
{
    internal static bool Startup() => Attempt.Hr("MFStartup", PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL));

    internal static void Shutdown() => Attempt.Hr("MFShutdown", PInvoke.MFShutdown());

    internal static unsafe List<H264Encoder> EnumerateH264()
    {
        var list = new List<H264Encoder>();
        var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 };
        var flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;
        if (!Attempt.Hr("MFTEnumEx (VIDEO_ENCODER, 出力 H264)", PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, null, output, out var activates, out var count)))
        {
            return list;
        }

        for (var i = 0; i < count; i++)
        {
            var activate = (IMFActivate)Marshal.GetObjectForIUnknown((nint)activates[i]);
            Marshal.Release((nint)activates[i]);
            var name = Attempt.Read(() =>
            {
                activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out var text, out _);
                var value = text.ToString();
                PInvoke.CoTaskMemFree(text.Value);
                return value;
            });
            var flagsText = Attempt.Read(() =>
            {
                activate.GetUINT32(PInvoke.MF_TRANSFORM_FLAGS_Attribute, out var value).ThrowOnFailure();
                return value.ToString();
            });
            if (!uint.TryParse(flagsText, out var transformFlags))
            {
                Console.WriteLine($"{name}: MF_TRANSFORM_FLAGS_Attribute を読めません({flagsText})。ソフトウェアの同期の MFT として扱います");
            }

            list.Add(new H264Encoder(activate, name, (MFT_ENUM_FLAG)transformFlags));
        }

        PInvoke.CoTaskMemFree(activates);
        return list;
    }

    /// <summary>エンコーダーの実体を作る。非同期の MFT なら、使えるようにロックを外す(Microsoft Learn「Asynchronous MFTs」)。</summary>
    internal static unsafe IMFTransform? Activate(H264Encoder encoder, out bool isAsync)
    {
        isAsync = false;
        IMFTransform transform = null!;
        if (!Attempt.Step("IMFActivate::ActivateObject(IMFTransform)", () =>
            {
                // 生成された <T> 版は IInspectable を問い合わせる。他社のエンコーダーがそれに E_NOINTERFACE 以外を返すと失敗になるので、ポインターで受ける
                var iid = typeof(IMFTransform).GUID;
                encoder.Activate.ActivateObject(&iid, out var pointer);
                transform = (IMFTransform)Marshal.GetObjectForIUnknown((nint)pointer);
                Marshal.Release((nint)pointer);
            }))
        {
            return null;
        }

        var hr = transform.GetAttributes(out var attributes);
        if (hr.Failed)
        {
            Console.WriteLine($"IMFTransform::GetAttributes: HRESULT {hr}(属性が無いので、同期の MFT として扱う)");
            return transform;
        }

        var asyncHr = attributes.GetUINT32(PInvoke.MF_TRANSFORM_ASYNC, out var asyncValue);
        isAsync = asyncHr.Succeeded && asyncValue != 0;
        Console.WriteLine($"MF_TRANSFORM_ASYNC: {(asyncHr.Succeeded ? asyncValue.ToString() : $"なし (HRESULT {asyncHr})")} → {(isAsync ? "非同期の MFT。イベントで回す" : "同期の MFT")}");
        if (isAsync && !Attempt.Step("MF_TRANSFORM_ASYNC_UNLOCK = 1", () => attributes.SetUINT32(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1)))
        {
            Release(encoder, transform);
            return null;
        }

        return transform;
    }

    /// <summary>エンコーダーの実体を止めて手放す。</summary>
    internal static void Release(H264Encoder encoder, IMFTransform transform)
    {
        Attempt.Step("IMFActivate::ShutdownObject", encoder.Activate.ShutdownObject);
        Marshal.ReleaseComObject(transform);
    }
}
