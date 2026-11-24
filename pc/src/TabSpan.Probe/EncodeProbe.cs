// モニター 1 枚を 5 秒取り込み、Media Foundation の H.264 エンコーダーで圧縮して、数・バイト数・時間を出す(仕様書 §8.1、D-5、R-3)。
// ハードウェアとソフトウェアを 1 つずつ、MFTEnumEx の順に試す。非同期の MFT はイベントを時間切れ付きで見て回し、同期の MFT はそのまま回す。
// BGRA から NV12 への変換は CPU で行う(測る道具なので、COM の歩数を増やさない)。絵も圧縮した中身も保存しない。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
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
                Console.WriteLine($"=== {(hardware ? "ハードウェア" : "ソフトウェア")}のエンコーダー: {candidates.Count} 件(1 フレームでも出たら、残りは試さない)");
                foreach (var encoder in candidates)
                {
                    if (new Pass(rig, encoder).Execute())
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
    private sealed class Pass(CaptureRig rig, H264Encoder encoder)
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

        internal bool Execute()
        {
            Console.WriteLine();
            Console.WriteLine($"--- {encoder.Name} ({encoder.Kind})");
            var transform = Mf.Activate(encoder, out var isAsync);
            if (transform is null)
            {
                return false;
            }

            _transform = transform;
            try
            {
                if (!Configure(isAsync))
                {
                    return false;
                }

                Console.WriteLine($"圧縮のループ: {Attempt.Read(() => Loop(isAsync))}");
                Report(isAsync);
                return _outputs > 0;
            }
            finally
            {
                Mf.Release(encoder, transform);
            }
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
                && Attempt.Step("入力の型を作る (NV12)", () =>
                {
                    PInvoke.MFCreateMediaType(out inputType).ThrowOnFailure();
                    inputType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
                    SetCommon(inputType);
                })
                && Attempt.Hr("IMFTransform::SetInputType", _transform.SetInputType(0, inputType, 0))
                && Attempt.Hr("IMFTransform::GetOutputStreamInfo", _transform.GetOutputStreamInfo(0, out var info))
                && Attempt.Step("出力のサンプルを用意する", () => PrepareOutput(info))
                && (!isAsync || Attempt.Step("IMFMediaEventGenerator を取る", () => _events = (IMFMediaEventGenerator)_transform))
                && Attempt.Hr("ProcessMessage(NOTIFY_BEGIN_STREAMING)", _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0))
                && Attempt.Hr("ProcessMessage(NOTIFY_START_OF_STREAM)", _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
            return ok;
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
                _stop = $"失敗: IMFTransform::ProcessInput (HRESULT {hr})";
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
                    BgraToNv12((byte*)mapped.pData, (int)mapped.RowPitch, destination, _width, _height);
                    buffer.Unlock();
                    buffer.SetCurrentLength(length);
                    PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
                    sample.AddBuffer(buffer);
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(10_000_000 / Fps);
                    Marshal.ReleaseComObject(buffer);
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
            Console.WriteLine($"絵の読み出しと NV12 への変換(CPU)にかかった時間: {Median(_convertMs)}");
            Console.WriteLine($"どのエンコーダーで出たか: {encoder.Name} ({encoder.Kind}。回し方は{(isAsync ? "イベント駆動。イベントを 1 ms 刻みで見ているので、時間は 1〜2 ms 多めに出る" : "同期")})");
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
