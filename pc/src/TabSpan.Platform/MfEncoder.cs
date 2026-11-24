// H.264 の圧縮(D-5): Media Foundation のエンコーダーを、低遅延・B フレームなし・CBR・キーフレームの強制で使う。出力は Annex B。
// ハードウェア(D3D11 のデバイス マネージャーを渡す。非同期の MFT はイベントで回す)を先に試し、最初のキーフレームが出なければソフトウェアに落ちる。
// EncodeProbe・Mf(Probe)を写して直した物。作るのも使うのも捨てるのも、同じ 1 本のスレッドから行う。
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using MfEvent = Windows.Win32.Media.MediaFoundation.__MIDL___MIDL_itf_mfobjects_0000_0013_0001;

namespace TabSpan.Platform;

/// <summary>Profile は baseline / main / high。BitrateBps はビット毎秒。</summary>
public sealed record EncoderSettings(int Width, int Height, int Fps, int BitrateBps, string Profile);

/// <summary>1 フレーム分の Annex B(開始コード付き)。Key は IDR を含むか。</summary>
public sealed record EncodedFrame(byte[] AnnexB, bool Key);

/// <summary>圧縮の口(偽物と差し替える)。</summary>
public interface IVideoEncoder : IDisposable
{
    string Name { get; }

    bool IsHardware { get; }

    /// <summary>NV12 を 1 枚圧縮する。出力が出なければ null。forceKey なら IDR にする。</summary>
    EncodedFrame? Encode(byte[] nv12, long timeUs, bool forceKey);
}

/// <summary>
/// ハードウェアを MFTEnumEx の順に試し、駄目ならソフトウェアを使う。最初のキーフレームが出たら採用する。
/// 採用した後の失敗は、次の候補(最後はソフトウェアの作り直し)に移る。10 秒に 3 回失敗したら例外を投げる(§10)。
/// </summary>
public sealed class AutoH264Encoder : IVideoEncoder
{
    private const int TriesBeforeAccepting = 3;

    private readonly EncoderSettings _settings;
    private readonly Action<string> _log;
    private readonly Queue<H264Candidate> _candidates;
    private readonly H264Candidate? _software;
    private readonly Queue<long> _failures = new();
    private MfH264Encoder? _current;
    private bool _accepted;
    private int _tries;

    public AutoH264Encoder(EncoderSettings settings, bool allowHardware, Action<string> log)
    {
        _settings = settings;
        _log = log;
        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
        var all = H264Candidate.Enumerate();
        _software = all.FirstOrDefault(c => !c.Hardware);
        _candidates = new(all.Where(c => c.Hardware && allowHardware).Append(_software).OfType<H264Candidate>());
    }

    public string Name => _current?.Name ?? "";

    public bool IsHardware => _current?.IsHardware ?? false;

    public EncodedFrame? Encode(byte[] nv12, long timeUs, bool forceKey)
    {
        while (true)
        {
            if (_current is null)
            {
                if (!_candidates.TryDequeue(out var next))
                {
                    throw new InvalidOperationException("使える H.264 のエンコーダーが無い");
                }

                try
                {
                    _current = new MfH264Encoder(next, _settings);
                    (_accepted, _tries) = (false, 0);
                    _log($"エンコーダーを試す: {next.Name}");
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    _log($"エンコーダー {next.Name} を使えない: {ex.Message}");
                    continue;
                }
            }

            try
            {
                var frame = _current.Encode(nv12, timeUs, forceKey || !_accepted);
                if (!_accepted)
                {
                    if (frame is { Key: true })
                    {
                        _accepted = true;
                        _log($"エンコーダーを決めた: {_current.Name}({(_current.IsHardware ? "ハードウェア" : "ソフトウェア")})");
                    }
                    else if (++_tries >= TriesBeforeAccepting)
                    {
                        throw new InvalidOperationException($"{TriesBeforeAccepting} 枚入れてもキーフレームが出ない");
                    }
                }

                return frame;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _log($"エンコーダー {_current.Name} が失敗した: {ex.Message}");
                var wasAccepted = _accepted;
                var wasSoftware = !_current.IsHardware;
                _current.Dispose();
                _current = null;
                if (wasAccepted)
                {
                    var now = Environment.TickCount64;
                    _failures.Enqueue(now);
                    while (_failures.Peek() < now - 10_000)
                    {
                        _failures.Dequeue();
                    }

                    if (_failures.Count >= 3)
                    {
                        throw new InvalidOperationException("エンコーダーが 10 秒に 3 回失敗した", ex);
                    }

                    if (wasSoftware && _software is not null && _candidates.Count == 0)
                    {
                        _candidates.Enqueue(_software); // 作り直してキーフレームから
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
        PInvoke.MFShutdown();
    }
}

/// <summary>MFTEnumEx が返した H.264 のエンコーダー 1 つ。</summary>
internal sealed record H264Candidate(IMFActivate Activate, string Name, bool Hardware)
{
    internal static unsafe List<H264Candidate> Enumerate()
    {
        var list = new List<H264Candidate>();
        var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 };
        var flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;
        PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, null, output, out var activates, out var count).ThrowOnFailure();
        for (var i = 0; i < count; i++)
        {
            var activate = (IMFActivate)Marshal.GetObjectForIUnknown((nint)activates[i]);
            Marshal.Release((nint)activates[i]);
            var name = "?";
            try
            {
                activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out var text, out _);
                name = text.ToString();
                PInvoke.CoTaskMemFree(text.Value);
            }
            catch (COMException)
            {
                // 名前が無くても使える
            }

            var hardware = activate.GetUINT32(PInvoke.MF_TRANSFORM_FLAGS_Attribute, out var f).Succeeded
                && ((MFT_ENUM_FLAG)f).HasFlag(MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE);
            list.Add(new H264Candidate(activate, name, hardware));
        }

        PInvoke.CoTaskMemFree(activates);
        return list;
    }
}

/// <summary>1 つの MFT を 1 つの大きさで回す。失敗は COMException か InvalidOperationException。</summary>
internal sealed unsafe class MfH264Encoder : IDisposable
{
    private const int EventWaitMs = 1000;

    private readonly H264Candidate _candidate;
    private readonly EncoderSettings _settings;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly bool _providesSamples;
    private readonly uint _outputSize;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IMFDXGIDeviceManager? _manager;
    private int _needInput;

    internal MfH264Encoder(H264Candidate candidate, EncoderSettings settings)
    {
        _candidate = candidate;
        _settings = settings;
        var iid = typeof(IMFTransform).GUID;
        // 生成された <T> 版は IInspectable を問い合わせる。他社のエンコーダーがそれに E_NOINTERFACE 以外を返すと失敗になるので、ポインターで受ける
        candidate.Activate.ActivateObject(&iid, out var pointer);
        _transform = (IMFTransform)Marshal.GetObjectForIUnknown((nint)pointer);
        Marshal.Release((nint)pointer);
        try
        {
            var isAsync = false;
            if (_transform.GetAttributes(out var attributes).Succeeded)
            {
                isAsync = attributes.GetUINT32(PInvoke.MF_TRANSFORM_ASYNC, out var a).Succeeded && a != 0;
                if (isAsync)
                {
                    attributes.SetUINT32(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                }
            }

            if (candidate.Hardware)
            {
                AttachDevice(); // NVIDIA はこれが無いと SetOutputType で MF_E_UNSUPPORTED_D3D_TYPE(§8.1)
            }

            var codec = (ICodecAPI)_transform;
            SetCodec(codec, typeof(CODECAPI_AVLowLatencyMode).GUID, true);
            SetCodec(codec, typeof(CODECAPI_AVEncMPVDefaultBPictureCount).GUID, 0u);
            SetCodec(codec, typeof(CODECAPI_AVEncCommonRateControlMode).GUID, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR);
            SetCodec(codec, typeof(CODECAPI_AVEncCommonMeanBitRate).GUID, (uint)settings.BitrateBps);

            PInvoke.MFCreateMediaType(out var outputType).ThrowOnFailure();
            outputType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            outputType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
            outputType.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, (uint)settings.BitrateBps);
            outputType.SetUINT32(PInvoke.MF_MT_MPEG2_PROFILE, (uint)(settings.Profile switch
            {
                "high" => eAVEncH264VProfile.eAVEncH264VProfile_High,
                "main" => eAVEncH264VProfile.eAVEncH264VProfile_Main,
                _ => eAVEncH264VProfile.eAVEncH264VProfile_Base,
            }));
            SetCommon(outputType);
            _transform.SetOutputType(0, outputType, 0).ThrowOnFailure();
            _transform.SetInputType(0, InputType(), 0).ThrowOnFailure();
            _transform.GetOutputStreamInfo(0, out var info).ThrowOnFailure();
            _providesSamples = (info.dwFlags & (uint)_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            _outputSize = Math.Max(info.cbSize, (uint)Nv12.Size(settings.Width, settings.Height));
            if (isAsync)
            {
                _events = (IMFMediaEventGenerator)_transform;
            }

            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0).ThrowOnFailure();
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0).ThrowOnFailure();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string Name => _candidate.Name;

    internal bool IsHardware => _candidate.Hardware;

    /// <summary>出力の型の MF_MT_MPEG_SEQUENCE_HEADER(SPS/PPS の Annex B)。無ければ null。確かめ用。</summary>
    internal byte[]? SequenceHeader()
    {
        if (_transform.GetOutputCurrentType(0, out var type).Failed)
        {
            return null;
        }

        uint size;
        try
        {
            type.GetBlobSize(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER, out size);
        }
        catch (COMException)
        {
            return null; // 属性が無い
        }

        var blob = new byte[size];
        var key = PInvoke.MF_MT_MPEG_SEQUENCE_HEADER;
        type.GetBlob(&key, blob, size, null);
        return blob;
    }

    internal EncodedFrame? Encode(byte[] nv12, long timeUs, bool forceKey)
    {
        if (forceKey)
        {
            SetCodec((ICodecAPI)_transform, typeof(CODECAPI_AVEncVideoForceKeyFrame).GUID, 1u);
        }

        var sample = MakeSample(nv12, timeUs);
        try
        {
            return _events is null ? EncodeSync(sample) : EncodeAsync(sample);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    private EncodedFrame? EncodeSync(IMFSample sample)
    {
        var output = new List<byte>();
        var hr = _transform.ProcessInput(0, sample, 0);
        if (hr == HRESULT.MF_E_NOTACCEPTING)
        {
            while (Pull(output))
            {
            }

            hr = _transform.ProcessInput(0, sample, 0);
        }

        hr.ThrowOnFailure();
        while (Pull(output))
        {
        }

        return ToFrame(output);
    }

    private EncodedFrame? EncodeAsync(IMFSample sample)
    {
        var output = new List<byte>();
        var deadline = Environment.TickCount64 + EventWaitMs;
        while (_needInput == 0)
        {
            Pump(output, deadline);
        }

        _transform.ProcessInput(0, sample, 0).ThrowOnFailure();
        _needInput--;

        // 低遅延のモードでは入れた 1 枚に 1 枚が返る。返るまで待つ(時間切れなら null)
        deadline = Environment.TickCount64 + EventWaitMs;
        while (output.Count == 0 && Environment.TickCount64 < deadline)
        {
            Pump(output, deadline);
        }

        return ToFrame(output);
    }

    /// <summary>非同期の MFT のイベントを 1 つ処理する。無ければ 1 ms 待つ。時間切れは例外。</summary>
    private void Pump(List<byte> output, long deadline)
    {
        var hr = _events!.GetEvent(MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS.MF_EVENT_FLAG_NO_WAIT, out var mediaEvent);
        if (hr == HRESULT.MF_E_NO_EVENTS_AVAILABLE)
        {
            if (Environment.TickCount64 >= deadline)
            {
                throw new InvalidOperationException("エンコーダーのイベントが時間内に来ない");
            }

            Thread.Sleep(1);
            return;
        }

        hr.ThrowOnFailure();
        mediaEvent.GetType(out var type);
        mediaEvent.GetStatus(out var status);
        Marshal.ReleaseComObject(mediaEvent);
        switch ((MfEvent)type)
        {
            case MfEvent.METransformNeedInput:
                _needInput++;
                break;
            case MfEvent.METransformHaveOutput:
                Pull(output);
                break;
            case MfEvent.MEError:
                throw new InvalidOperationException($"エンコーダーが MEError を出した (HRESULT {status})");
        }
    }

    /// <summary>出力を 1 つ取り出して output に足す。取り出せたら true、「もっと入力が要る」なら false。</summary>
    private bool Pull(List<byte> output)
    {
        IMFSample? own = null;
        if (!_providesSamples)
        {
            PInvoke.MFCreateMemoryBuffer(_outputSize, out var buffer).ThrowOnFailure();
            PInvoke.MFCreateSample(out own).ThrowOnFailure();
            own.AddBuffer(buffer);
            Marshal.ReleaseComObject(buffer);
        }

        var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
        buffers[0].pSample = own!;
        var hr = _transform.ProcessOutput(0, buffers, out _);
        var sample = buffers[0].pSample;
        try
        {
            if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                return false;
            }

            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                _transform.GetOutputAvailableType(0, 0, out var type).ThrowOnFailure();
                _transform.SetOutputType(0, type, 0).ThrowOnFailure();
                return true;
            }

            hr.ThrowOnFailure();
            if (sample is null)
            {
                throw new InvalidOperationException("ProcessOutput がサンプルを返さない");
            }

            sample.ConvertToContiguousBuffer(out var contiguous);
            try
            {
                byte* data;
                uint length;
                contiguous.Lock(&data, null, &length);
                try
                {
                    output.AddRange(new ReadOnlySpan<byte>(data, (int)length));
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(contiguous);
            }

            return true;
        }
        finally
        {
            if (buffers[0].pEvents is not null)
            {
                Marshal.ReleaseComObject(buffers[0].pEvents);
            }

            if (sample is not null)
            {
                Marshal.ReleaseComObject(sample);
            }

            if (own is not null && !ReferenceEquals(own, sample))
            {
                Marshal.ReleaseComObject(own);
            }
        }
    }

    private static EncodedFrame? ToFrame(List<byte> output)
    {
        if (output.Count == 0)
        {
            return null;
        }

        var bytes = output.ToArray();
        return new EncodedFrame(bytes, TabSpan.Core.AnnexB.SplitNals(bytes).Any(n => (n.Span[0] & 0x1F) == 5));
    }

    private IMFSample MakeSample(byte[] nv12, long timeUs)
    {
        var length = (uint)Nv12.Size(_settings.Width, _settings.Height);
        PInvoke.MFCreateMemoryBuffer(length, out var buffer).ThrowOnFailure();
        try
        {
            byte* destination;
            buffer.Lock(&destination, null, null);
            try
            {
                nv12.AsSpan(0, (int)length).CopyTo(new Span<byte>(destination, (int)length));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength(length);
            PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
            sample.AddBuffer(buffer);
            sample.SetSampleTime(timeUs * 10); // 100 ns
            sample.SetSampleDuration(10_000_000 / _settings.Fps);
            return sample;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>MFT が挙げる入力の型から NV12 を探す。無ければ自分で作る。</summary>
    private IMFMediaType InputType()
    {
        for (uint i = 0; _transform.GetInputAvailableType(0, i, out var type).Succeeded; i++)
        {
            type.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
            if (subtype == PInvoke.MFVideoFormat_NV12)
            {
                SetCommon(type);
                return type;
            }

            Marshal.ReleaseComObject(type);
        }

        PInvoke.MFCreateMediaType(out var own).ThrowOnFailure();
        own.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        own.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
        SetCommon(own);
        return own;
    }

    private void SetCommon(IMFMediaType type)
    {
        type.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, ((ulong)(uint)_settings.Width << 32) | (uint)_settings.Height);
        type.SetUINT64(PInvoke.MF_MT_FRAME_RATE, ((ulong)(uint)_settings.Fps << 32) | 1);
        type.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
        type.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
    }

    // 通らない設定があっても続ける(エンコーダーごとに使えるプロパティが違う。§8.1)
    private static void SetCodec(ICodecAPI codec, Guid api, object value) => _ = codec.SetValue(api, value);

    /// <summary>既定のアダプターの D3D11 のデバイスを作り、デバイス マネージャーに載せて MFT に渡す。</summary>
    private void AttachDevice()
    {
        var levels = new[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, PInvoke.D3D11_SDK_VERSION, out _device, out _context).ThrowOnFailure();
        ((ID3D10Multithread)_device).SetMultithreadProtected(true);
        PInvoke.MFCreateDXGIDeviceManager(out var token, out _manager).ThrowOnFailure();
        _manager.ResetDevice(_device, token);
        var pointer = Marshal.GetIUnknownForObject(_manager);
        try
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)pointer).ThrowOnFailure();
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public void Dispose()
    {
        try
        {
            _candidate.Activate.ShutdownObject();
        }
        catch (COMException)
        {
            // 止められなくても手放す
        }

        foreach (var o in new object?[] { _transform, _manager, _context, _device })
        {
            if (o is not null)
            {
                Marshal.ReleaseComObject(o);
            }
        }
    }
}
