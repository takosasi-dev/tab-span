// モニター 1 枚を 5 秒取り込み、Media Foundation の H.264 エンコーダーで圧縮して、数・バイト数・時間を出す(仕様書 §8.1、D-5、R-3)。
// ハードウェアは全部、ソフトウェアは 1 つを MFTEnumEx の順に試す。ハードウェアには型の前に D3D11 のデバイス マネージャーを渡し、非同期の MFT はイベントを時間切れ付きで回す。
// BGRA から NV12 への変換は CPU で行う(断られたら、その NV12 を D3D11 のテクスチャに上げて入れ直す)。絵も圧縮した中身も保存しない。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.WinRT.Direct3D11;
using WinRT;
using MfEvent = Windows.Win32.Media.MediaFoundation.__MIDL___MIDL_itf_mfobjects_0000_0013_0001;

namespace TabSpan.Probe;

internal static class EncodeProbe
{
    private const int EncodeMs = 5000;
    private const uint BitrateBps = 8_000_000;
    private const uint Fps = 60;

    internal static int Run(string identifier)
    {
        Console.WriteLine($"色変換の自己検査(白 → Y=235 U=128 V=128): {SelfCheck()}");
        if (!Mf.Startup())
        {
            return 1;
        }

        var encoders = Mf.EnumerateH264();
        using var rig = CaptureRig.Open(identifier);
        if (rig is null || !Attempt.Step("StartCapture", rig.Session.StartCapture))
        {
            Mf.Shutdown();
            return 1;
        }

        Console.WriteLine($"エンコーダーごとに {EncodeMs / 1000} 秒取り込んで圧縮します。画面が変わらないとフレームは届かないので、このモニターの上でマウスを動かし続けてください。");
        PInvoke.timeBeginPeriod(1); // Sleep(1) を 1 ms 前後にして、フレームとイベントを待つ刻みを細かくする
        try
        {
            foreach (var hardware in new[] { true, false })
            {
                var candidates = encoders.Where(e => e.Hardware == hardware).ToList();
                Console.WriteLine();
                Console.WriteLine($"=== {(hardware ? "ハードウェア" : "ソフトウェア")}のエンコーダー: {candidates.Count} 件{(hardware ? "(どれが通るか知るため、全部試す)" : "(1 フレームでも出たら、残りは試さない)")}");
                foreach (var encoder in candidates)
                {
                    var pass = new Pass(rig, encoder, false);
                    var ok = pass.Execute();
                    if (!ok && pass.WorthGpuRetry)
                    {
                        // 非同期の MFT は ProcessInput が通っても、後から MEError を出すか、何も出さないことがある。流れの途中では差し替えず、作り直して試す
                        Console.WriteLine();
                        Console.WriteLine("システム メモリの NV12 では出力が出なかったので、同じエンコーダーを作り直し、D3D11 の NV12 テクスチャで入れ直す");
                        ok = new Pass(rig, encoder, true).Execute();
                    }

                    if (ok && !hardware)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            PInvoke.timeEndPeriod(1);
        }

        Mf.Shutdown();
        return 0;
    }

    private static unsafe string SelfCheck()
    {
        var white = stackalloc byte[16];
        new Span<byte>(white, 16).Fill(255);
        var nv12 = stackalloc byte[6];
        BgraToNv12(white, 8, nv12, 2, 2);
        return new ReadOnlySpan<byte>(nv12, 6).SequenceEqual<byte>([235, 235, 235, 235, 128, 128]) ? "OK" : "失敗(変換の式が違う)";
    }

    /// <summary>BGRA(8 ビット)を NV12 にする。BT.709 の限定レンジ。幅と高さは偶数。</summary>
    private static unsafe void BgraToNv12(byte* source, int pitch, byte* destination, int width, int height)
    {
        var chroma = destination + (width * height);
        for (var y = 0; y < height; y++)
        {
            var row = source + ((nint)y * pitch);
            var luma = destination + (y * width);
            for (var x = 0; x < width; x++)
            {
                var p = row + (x * 4);
                luma[x] = (byte)((((47 * p[2]) + (157 * p[1]) + (16 * p[0]) + 128) >> 8) + 16);
            }

            if ((y & 1) != 0)
            {
                continue;
            }

            // shortcut: 色差は 2x2 の左上の 1 画素だけを使う。絵の質を見る段(Phase 2)では 4 画素の平均か GPU の変換にする
            var uv = chroma + ((y / 2) * width);
            for (var x = 0; x < width; x += 2)
            {
                var p = row + (x * 4);
                uv[x] = (byte)((((-26 * p[2]) - (86 * p[1]) + (112 * p[0]) + 128) >> 8) + 128);
                uv[x + 1] = (byte)((((112 * p[2]) - (102 * p[1]) - (10 * p[0]) + 128) >> 8) + 128);
            }
        }
    }

    /// <summary>エンコーダー 1 つで 5 秒回す 1 回分。</summary>
    private sealed class Pass(CaptureRig rig, H264Encoder encoder, bool gpuInput)
    {
        private readonly int _width = rig.Item.Size.Width & ~1;
        private readonly int _height = rig.Item.Size.Height & ~1;
        private readonly Dictionary<long, long> _inputAt = [];
        private readonly List<double> _encodeMs = [];
        private readonly List<double> _convertMs = [];
        private IMFTransform _transform = null!;
        private IMFMediaEventGenerator? _events;
        private ID3D11Texture2D? _staging;
        private IMFSample? _ownSample;
        private IMFMediaBuffer? _ownBuffer;
        private int _taken;
        private int _inputs;
        private int _notAccepting;
        private int _outputs;
        private int _keyFrames;
        private int _pendingInput;
        private int _streamChanges;
        private int _otherEvents;
        private int _unmatched;
        private long _bytes;
        private string? _stop;
        private bool _configured;

        // ハードウェアの MFT に渡す D3D11 のデバイス(取り込みのデバイスとは別。VIDEO_SUPPORT 付きで作る)
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IMFDXGIDeviceManager? _manager;

        /// <summary>型は決まりフレームも入れたのに 1 つも出なかった。D3D11 のテクスチャの入力で試し直す値打ちがある。</summary>
        internal bool WorthGpuRetry => encoder.Hardware && !gpuInput && _configured && _taken > 0 && _outputs == 0;

        internal bool Execute()
        {
            Console.WriteLine();
            Console.WriteLine($"--- {encoder.Name} ({encoder.Kind}、入力は{(gpuInput ? " D3D11 の NV12 テクスチャ" : "システム メモリの NV12")})");
            var transform = Mf.Activate(encoder, out var isAsync);
            if (transform is null)
            {
                return false;
            }

            _transform = transform;
            try
            {
                if (!(encoder.Hardware ? ConfigureOnAnAdapter(isAsync) : Configure(isAsync)))
                {
                    return false;
                }

                _configured = true;
                Console.WriteLine($"圧縮のループ: {Attempt.Read(() => Loop(isAsync))}");
                Report(isAsync);
                return _outputs > 0;
            }
            finally
            {
                Mf.Release(encoder, transform);
                ReleaseDevice();
            }
        }

        /// <summary>
        /// GPU が複数あると、ハードウェアの MFT がどのアダプターの物かは一覧から分からない(MFT_ENUM_ADAPTER_LUID は MFTEnumEx の Activate に付くとは限らない)。
        /// そこでアダプターを EnumAdapters1 の順(0 番が既定で、取り込みと同じ)に試し、型が決まった所で回す。
        /// </summary>
        private bool ConfigureOnAnAdapter(bool isAsync)
        {
            Console.WriteLine($"取り込みのデバイスのアダプター: {Attempt.Read(() =>
            {
                ((IDXGIDevice)rig.Device).GetAdapter(out var adapter);
                return Describe(adapter.GetDesc());
            })}");
            Console.WriteLine($"MF_SA_D3D11_AWARE: {Attempt.Read(() =>
            {
                _transform.GetAttributes(out var attributes).ThrowOnFailure();
                var hr = attributes.GetUINT32(PInvoke.MF_SA_D3D11_AWARE, out var value);
                return hr.Succeeded ? value.ToString() : $"なし (HRESULT {hr})";
            })}");

            IDXGIFactory1 factory = null!;
            if (!Attempt.Step("CreateDXGIFactory1", () => PInvoke.CreateDXGIFactory1(out factory).ThrowOnFailure()))
            {
                return false;
            }

            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Succeeded; i++)
            {
                Console.WriteLine($"[アダプター {i}] {Describe(adapter.GetDesc())}");
                if (AttachDevice(adapter) && Configure(isAsync))
                {
                    return true;
                }

                ReleaseDevice();
            }

            Console.WriteLine("失敗: どのアダプターのデバイスでも、型を決めるまで通らなかった");
            return false;
        }

        private static string Describe(DXGI_ADAPTER_DESC description) =>
            $"{description.Description} (LUID {description.AdapterLuid.HighPart:X8}-{description.AdapterLuid.LowPart:X8})";

        /// <summary>D3D11 のデバイスを作り、デバイス マネージャーに載せて MFT に渡す。型を決める前に呼ぶ。</summary>
        private bool AttachDevice(IDXGIAdapter1 adapter)
        {
            uint token = 0;
            // 機能レベルを渡さないと 11_0 止まりになる(既定の並びに 11_1 が無い)
            var levels = new[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
            if (!Attempt.Step("D3D11CreateDevice (このアダプター、UNKNOWN、VIDEO_SUPPORT | BGRA_SUPPORT、11_1 か 11_0)", () =>
                    PInvoke.D3D11CreateDevice(adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, PInvoke.D3D11_SDK_VERSION, out _device, out _context).ThrowOnFailure()))
            {
                return false;
            }

            Console.WriteLine($"  機能レベル: {_device!.GetFeatureLevel()}");
            return Attempt.Step("ID3D10Multithread::SetMultithreadProtected(TRUE)", () => ((ID3D10Multithread)_device!).SetMultithreadProtected(true))
                && Attempt.Step("MFCreateDXGIDeviceManager", () => PInvoke.MFCreateDXGIDeviceManager(out token, out _manager).ThrowOnFailure())
                && Attempt.Step("IMFDXGIDeviceManager::ResetDevice", () => _manager!.ResetDevice(_device!, token))
                && SendManager();
        }

        private bool SendManager()
        {
            // ProcessMessage には IUnknown のポインターを渡す。MFT は自分で参照を持つので、渡した後にこちらの分を返す
            var pointer = Marshal.GetIUnknownForObject(_manager!);
            try
            {
                return Attempt.Hr("ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER)", _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)pointer));
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private void ReleaseDevice()
        {
            foreach (var item in new object?[] { _manager, _context, _device })
            {
                if (item is not null)
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            (_manager, _context, _device) = (null, null, null);
        }

        private bool Configure(bool isAsync)
        {
            // 低遅延(並べ替えなし、入力 1 つに出力 1 つ)。型を決める前に設定する。通らなくても続ける
            Console.WriteLine($"ICodecAPI::SetValue(CODECAPI_AVLowLatencyMode, true): {Attempt.Read(() =>
            {
                object on = true;
                return $"HRESULT {((ICodecAPI)_transform).SetValue(typeof(CODECAPI_AVLowLatencyMode).GUID, on)}";
            })}");

            IMFMediaType outputType = null!;
            IMFMediaType inputType = null!;
            var ok =
                Attempt.Step($"出力の型を作る (H264、Main、{_width}x{_height}、{Fps} fps、{BitrateBps / 1_000_000} Mbps)", () =>
                {
                    PInvoke.MFCreateMediaType(out outputType).ThrowOnFailure();
                    outputType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
                    outputType.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, BitrateBps);
                    outputType.SetUINT32(PInvoke.MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_Main);
                    SetCommon(outputType);
                })
                && Attempt.Hr("IMFTransform::SetOutputType", _transform.SetOutputType(0, outputType, 0))
                && Attempt.Step("入力の型を選ぶ (NV12)", () => inputType = InputType())
                && Attempt.Hr("IMFTransform::SetInputType", _transform.SetInputType(0, inputType, 0))
                && Attempt.Hr("IMFTransform::GetOutputStreamInfo", _transform.GetOutputStreamInfo(0, out var info))
                && Attempt.Step("出力のサンプルを用意する", () => PrepareOutput(info))
                && (!isAsync || Attempt.Step("IMFMediaEventGenerator を取る", () => _events = (IMFMediaEventGenerator)_transform))
                && Attempt.Hr("ProcessMessage(NOTIFY_BEGIN_STREAMING)", _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0))
                && Attempt.Hr("ProcessMessage(NOTIFY_START_OF_STREAM)", _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
            return ok;
        }

        /// <summary>MFT が挙げる入力の型から NV12 を探す。無ければ自分で作る。どちらも大きさと速さは書き直す。</summary>
        private IMFMediaType InputType()
        {
            HRESULT hr;
            for (uint i = 0; (hr = _transform.GetInputAvailableType(0, i, out var type)).Succeeded; i++)
            {
                type.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
                if (subtype == PInvoke.MFVideoFormat_NV12)
                {
                    Console.WriteLine($"  GetInputAvailableType の {i} 番目が NV12。これを使う");
                    SetCommon(type);
                    return type;
                }

                Marshal.ReleaseComObject(type);
            }

            Console.WriteLine($"  GetInputAvailableType に NV12 が無い (止まった値 HRESULT {hr})。自分で作る");
            PInvoke.MFCreateMediaType(out var own).ThrowOnFailure();
            own.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            SetCommon(own);
            return own;
        }

        private void SetCommon(IMFMediaType type)
        {
            type.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            type.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, ((ulong)(uint)_width << 32) | (uint)_height);
            type.SetUINT64(PInvoke.MF_MT_FRAME_RATE, ((ulong)Fps << 32) | 1);
            type.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            type.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        }

        private void PrepareOutput(MFT_OUTPUT_STREAM_INFO info)
        {
            var provides = (info.dwFlags & (uint)_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            Console.WriteLine($"  出力のサンプルは{(provides ? " MFT が用意する" : "呼ぶ側が用意する")} (dwFlags=0x{info.dwFlags:X}、cbSize={info.cbSize})");
            if (provides)
            {
                return;
            }

            // 中身は数えるだけなので、1 つのサンプルを使い回す
            PInvoke.MFCreateMemoryBuffer(Math.Max(info.cbSize, (uint)(_width * _height * 3 / 2)), out _ownBuffer).ThrowOnFailure();
            PInvoke.MFCreateSample(out _ownSample).ThrowOnFailure();
            _ownSample.AddBuffer(_ownBuffer);
        }

        private string Loop(bool isAsync)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < EncodeMs && _stop is null)
            {
                if (isAsync)
                {
                    PumpEvents();
                }

                // 非同期の MFT には、METransformNeedInput が来た数だけ入れる
                if (_stop is null && (!isAsync || _pendingInput > 0) && Feed(clock, isAsync))
                {
                    continue;
                }

                Thread.Sleep(1);
            }

            return _stop ?? $"OK({EncodeMs / 1000} 秒たった)";
        }

        private bool Feed(Stopwatch clock, bool isAsync)
        {
            using var frame = rig.Pool.TryGetNextFrame();
            if (frame is null)
            {
                return false;
            }

            _taken++;
            var time = clock.Elapsed.Ticks; // TimeSpan の Ticks は 100 ns で、サンプルの時刻の単位と同じ
            var started = Stopwatch.GetTimestamp();
            var sample = ToNv12Sample(frame, time);
            _convertMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            var inputAt = Stopwatch.GetTimestamp();
            var hr = _transform.ProcessInput(0, sample, 0);
            Marshal.ReleaseComObject(sample);
            if (hr == HRESULT.MF_E_NOTACCEPTING)
            {
                _notAccepting++;
            }
            else if (hr.Failed)
            {
                _stop = $"失敗: IMFTransform::ProcessInput (HRESULT {hr})"
                    + (_device is null ? "" : $"。エンコーダーに渡したデバイスの GetDeviceRemovedReason: {Attempt.Read(() => { _device.GetDeviceRemovedReason(); return "S_OK(消えていない)"; })}");
            }
            else
            {
                _inputs++;
                _inputAt[time] = inputAt;
                if (isAsync)
                {
                    _pendingInput--;
                }
            }

            // 同期の MFT は、入れた後に「もっと入力が要る」と言うまで取り出す
            while (!isAsync && _stop is null && PullOutput())
            {
            }

            return true;
        }

        private unsafe IMFSample ToNv12Sample(Direct3D11CaptureFrame frame, long time)
        {
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            access.GetInterface(out ID3D11Texture2D texture);
            try
            {
                if (_staging is null)
                {
                    texture.GetDesc(out var description);
                    description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
                    description.BindFlags = 0;
                    description.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
                    description.MiscFlags = 0;
                    rig.Device.CreateTexture2D(description, null, out _staging);
                }

                rig.Context.CopyResource(_staging, texture);
                rig.Context.Map(_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);
                try
                {
                    var length = (uint)(_width * _height * 3 / 2);
                    PInvoke.MFCreateMemoryBuffer(length, out var buffer).ThrowOnFailure();
                    buffer.Lock(out byte* destination);
                    IMFMediaBuffer input;
                    try
                    {
                        BgraToNv12((byte*)mapped.pData, (int)mapped.RowPitch, destination, _width, _height);
                        input = gpuInput ? Upload(destination) : buffer;
                    }
                    finally
                    {
                        buffer.Unlock();
                    }

                    if (gpuInput)
                    {
                        Marshal.ReleaseComObject(buffer);
                    }
                    else
                    {
                        buffer.SetCurrentLength(length);
                    }

                    PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
                    sample.AddBuffer(input);
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(10_000_000 / Fps);
                    Marshal.ReleaseComObject(input);
                    return sample;
                }
                finally
                {
                    rig.Context.Unmap(_staging, 0);
                }
            }
            finally
            {
                // 手放さないと、フレームの置き場(2 枚)が空かなくなる
                Marshal.ReleaseComObject(texture);
                Marshal.ReleaseComObject(access);
            }
        }

        /// <summary>CPU の NV12(Y の後に UV が続き、行の幅は横の画素数)を、エンコーダーのデバイスの NV12 テクスチャに上げて、MF のバッファで包む。</summary>
        private unsafe IMFMediaBuffer Upload(byte* nv12)
        {
            var stage = "ID3D11Device::CreateTexture2D (NV12、DEFAULT、BindFlags 0)";
            try
            {
                var description = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)_width,
                    Height = (uint)_height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_NV12,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                };

                // ponytail: 1 フレームごとに作る(非同期の MFT がまだ読んでいる物を上書きしないため)。重さが測りの邪魔になったら数枚を回して使う
                _device!.CreateTexture2D(description, null, out var texture);
                try
                {
                    stage = "ID3D11DeviceContext::UpdateSubresource (Y と UV を 1 回で)";
                    _context!.UpdateSubresource(texture, 0, null, nv12, (uint)_width, 0);
                    stage = "MFCreateDXGISurfaceBuffer";
                    PInvoke.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false, out var buffer).ThrowOnFailure();
                    stage = "IMF2DBuffer::GetContiguousLength";
                    ((IMF2DBuffer)buffer).GetContiguousLength(out var length);
                    buffer.SetCurrentLength(length);
                    return buffer;
                }
                finally
                {
                    Marshal.ReleaseComObject(texture);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"GPU への上げ直しの {stage} で止まった", ex);
            }
        }

        private void PumpEvents()
        {
            while (_stop is null)
            {
                var hr = _events!.GetEvent(MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS.MF_EVENT_FLAG_NO_WAIT, out var mediaEvent);
                if (hr == HRESULT.MF_E_NO_EVENTS_AVAILABLE)
                {
                    return;
                }

                if (hr.Failed)
                {
                    _stop = $"失敗: IMFMediaEventGenerator::GetEvent (HRESULT {hr})";
                    return;
                }

                mediaEvent.GetType(out var type);
                mediaEvent.GetStatus(out var status);
                Marshal.ReleaseComObject(mediaEvent);
                switch ((MfEvent)type)
                {
                    case MfEvent.METransformNeedInput:
                        _pendingInput++;
                        break;
                    case MfEvent.METransformHaveOutput:
                        PullOutput();
                        break;
                    case MfEvent.MEError:
                        _stop = $"失敗: エンコーダーが MEError を出した (HRESULT {status})";
                        break;
                    default:
                        _otherEvents++;
                        break;
                }
            }
        }

        /// <summary>出力を 1 つ取り出す。取り出せたら true。「もっと入力が要る」か失敗なら false。</summary>
        private bool PullOutput()
        {
            var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
            buffers[0].pSample = _ownSample!;
            var hr = _transform.ProcessOutput(0, buffers, out _);
            var outputAt = Stopwatch.GetTimestamp();
            var sample = buffers[0].pSample;
            try
            {
                if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT)
                {
                    return false;
                }

                if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    return Renegotiate();
                }

                if (hr.Failed || sample is null)
                {
                    _stop = $"失敗: IMFTransform::ProcessOutput (HRESULT {hr}、サンプル{(sample is null ? "なし" : "あり")})";
                    return false;
                }

                sample.GetTotalLength(out var length);
                _bytes += length;
                _outputs++;
                if (sample.GetUINT32(PInvoke.MFSampleExtension_CleanPoint, out var clean).Succeeded && clean != 0)
                {
                    _keyFrames++;
                }

                // 入れたときの時刻で対にする。エンコーダーが時刻を書き換えていたら、いちばん古い入力と対にする(並べ替えなしなら順番は同じ)
                if ((sample.GetSampleTime(out var time).Failed || !_inputAt.ContainsKey(time)) && _inputAt.Count > 0)
                {
                    time = _inputAt.Keys.Min();
                    _unmatched++;
                }

                if (_inputAt.Remove(time, out var inputAt))
                {
                    _encodeMs.Add(Stopwatch.GetElapsedTime(inputAt, outputAt).TotalMilliseconds);
                }

                return true;
            }
            finally
            {
                if (buffers[0].pEvents is not null)
                {
                    Marshal.ReleaseComObject(buffers[0].pEvents);
                }

                if (ReferenceEquals(sample, _ownSample))
                {
                    // 使い回すので、前の中身と属性(キーフレームの印)を消す
                    _ownSample?.DeleteAllItems();
                    _ownBuffer?.SetCurrentLength(0);
                }
                else if (sample is not null)
                {
                    // MFT が用意したサンプルは、手放さないと MFT の置き場に戻らない
                    Marshal.ReleaseComObject(sample);
                }
            }
        }

        private bool Renegotiate()
        {
            if (++_streamChanges > 3)
            {
                _stop = "失敗: MF_E_TRANSFORM_STREAM_CHANGE が 3 回を超えた";
                return false;
            }

            var hr = _transform.GetOutputAvailableType(0, 0, out var type);
            if (hr.Succeeded)
            {
                hr = _transform.SetOutputType(0, type, 0);
            }

            if (hr.Failed)
            {
                _stop = $"失敗: MF_E_TRANSFORM_STREAM_CHANGE の後の出力の型の決め直し (HRESULT {hr})";
            }

            return hr.Succeeded;
        }

        private void Report(bool isAsync)
        {
            Console.WriteLine($"取り出したフレームの数: {_taken}");
            Console.WriteLine($"エンコーダーに入れたフレームの数: {_inputs}(受け付けなかった回数 {_notAccepting})");
            Console.WriteLine($"出たフレームの数: {_outputs}");
            Console.WriteLine($"キーフレームの数: {_keyFrames}");
            Console.WriteLine($"合計のバイト数: {_bytes}");
            Console.WriteLine($"1 フレームの圧縮にかかった時間(ProcessInput の直前から、その絵の出力を受け取るまで): {Median(_encodeMs)}");
            Console.WriteLine($"絵の読み出しと NV12 への変換(CPU){(gpuInput ? "と GPU への上げ直し" : "")}にかかった時間: {Median(_convertMs)}");
            Console.WriteLine($"どのエンコーダーで出たか: {encoder.Name} ({encoder.Kind}。回し方は{(isAsync ? "イベント駆動。イベントを 1 ms 刻みで見ているので、時間は 1〜2 ms 多めに出る" : "同期")})");
            Console.WriteLine($"入力の形: {(gpuInput ? "D3D11 の NV12 テクスチャ(CPU の NV12 を UpdateSubresource で上げ、MFCreateDXGISurfaceBuffer で包んだ)" : "システム メモリの NV12(MFCreateMemoryBuffer)")}{(_outputs > 0 ? "。この形で通った" : "。この形では出なかった")}");
            if (_streamChanges + _otherEvents + _unmatched > 0)
            {
                Console.WriteLine($"そのほか: 出力の型の決め直し {_streamChanges} 回、数えていない種類のイベント {_otherEvents} 件、時刻で対にできなかった出力 {_unmatched} 件");
            }
        }

        private static string Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return "(測れた物なし)";
            }

            values.Sort();
            return $"中央値 {values[values.Count / 2]:F2} ms(最小 {values[0]:F2}、最大 {values[^1]:F2}、{values.Count} 件)";
        }
    }
}
