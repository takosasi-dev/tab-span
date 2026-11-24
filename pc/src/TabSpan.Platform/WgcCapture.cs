// モニター 1 枚を Windows.Graphics.Capture で取り込む(D-3、FR-18: カーソル込み、枠を消す要求)。CaptureRig(Probe)を写して直した物。
// 置き場の枠はいちばん新しい 1 枚だけを使い、古い物は捨てる(D-13)。読み出しと NV12 への変換(縮小を含む)は CPU で行う。
// 作るのも使うのも捨てるのも、同じ 1 本のスレッドから行う(呼ぶ側の MirrorStreamer の取り込みのスレッド)。
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;

namespace TabSpan.Platform;

/// <summary>取り込みの口(偽物と差し替える)。</summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>取り込む画面の大きさ(物理ピクセル)。</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>モニターが外れた・大きさが変わった。取り込みを作り直す。</summary>
    bool Lost { get; }

    /// <summary>新しい絵があれば、いちばん新しい 1 枚を width x height の NV12 にして dest に書き、true。waitMs 待っても来なければ false。</summary>
    bool TryTake(byte[] dest, int width, int height, int waitMs, out long captureUs);
}

/// <summary>取り込みを始められない(モニターが無い、デスクトップに付いていない、WGC が使えない)。</summary>
public sealed class CaptureUnavailableException : Exception
{
    public CaptureUnavailableException()
    {
    }

    public CaptureUnavailableException(string message) : base(message)
    {
    }

    public CaptureUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class WgcCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly TabSpan.Core.IClock _clock;
    private ID3D11Texture2D? _staging;
    private volatile bool _lost;

    private WgcCapture(ID3D11Device device, ID3D11DeviceContext context, GraphicsCaptureItem item, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session, TabSpan.Core.IClock clock)
    {
        (_device, _context, _item, _pool, _session, _clock) = (device, context, item, pool, session, clock);
        Width = item.Size.Width;
        Height = item.Size.Height;
        item.Closed += (_, _) => _lost = true;
    }

    public int Width { get; }

    public int Height { get; }

    public bool Lost => _lost;

    /// <summary>識別子(\\.\DISPLAYn)のモニターの取り込みを始める。log には設定が通ったかだけを書く。</summary>
    public static WgcCapture Open(string deviceName, TabSpan.Core.IClock clock, Action<string> log)
    {
        var handle = Monitors.HandleOf(deviceName);
        if (handle.IsNull)
        {
            throw new CaptureUnavailableException($"{deviceName} がデスクトップに付いていない");
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new CaptureUnavailableException("Windows.Graphics.Capture が使えない");
        }

        PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, default,
            PInvoke.D3D11_SDK_VERSION, out var device, out var context).ThrowOnFailure();
        PInvoke.CreateDirect3D11DeviceFromDXGIDevice((IDXGIDevice)device, out var inspectable).ThrowOnFailure();
        IDirect3DDevice winrtDevice;
        var pointer = Marshal.GetIUnknownForObject(inspectable);
        try
        {
            winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }

        GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForMonitor(handle, out GraphicsCaptureItem item);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = true; // FR-18
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            // 枠を消す。許可が無いと設定は通るが無視される(仕様 §4)。§8.1 の実物ではパッケージ化していなくても Allowed が返った
            var access = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
            log($"枠を消す許可: {(access.Wait(TimeSpan.FromSeconds(5)) ? access.Result.ToString() : "5 秒で返事が無い")}");
            session.IsBorderRequired = false;
        }

        session.StartCapture();
        log($"{deviceName} の取り込みを始めた({item.Size.Width}x{item.Size.Height})");
        return new WgcCapture(device, context, item, pool, session, clock);
    }

    public unsafe bool TryTake(byte[] dest, int width, int height, int waitMs, out long captureUs)
    {
        captureUs = 0;
        if (dest.Length < Nv12.Size(width, height))
        {
            throw new ArgumentException("NV12 の置き場が小さい", nameof(dest));
        }

        var deadline = Environment.TickCount64 + waitMs;
        Direct3D11CaptureFrame? frame;
        while ((frame = _pool.TryGetNextFrame()) is null)
        {
            if (_lost || Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        // いちばん新しい 1 枚だけを使う
        while (_pool.TryGetNextFrame() is { } newer)
        {
            frame.Dispose();
            frame = newer;
        }

        using (frame)
        {
            if (frame.ContentSize.Width != Width || frame.ContentSize.Height != Height)
            {
                _lost = true; // 解像度が変わった。呼ぶ側が作り直す
                return false;
            }

            // SystemRelativeTime は QPC を 100 ns にした値。SystemClock.NowUs と同じ起点なので µs に直して使う
            var now = _clock.NowUs;
            captureUs = frame.SystemRelativeTime.Ticks / 10;
            if (captureUs <= 0 || captureUs > now)
            {
                captureUs = now;
            }

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
                    _device.CreateTexture2D(description, null, out _staging);
                }

                _context.CopyResource(_staging, texture);
            }
            finally
            {
                Marshal.ReleaseComObject(texture); // 手放さないと置き場(2 枚)が空かない
                Marshal.ReleaseComObject(access);
            }
        }

        _context.Map(_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);
        try
        {
            fixed (byte* d = dest)
            {
                Nv12.FromBgra((byte*)mapped.pData, (int)mapped.RowPitch, Width, Height, d, width, height);
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        return true;
    }

    public void Dispose()
    {
        _session.Dispose();
        _pool.Dispose();
        foreach (var o in new object?[] { _staging, _context, _device })
        {
            if (o is not null)
            {
                Marshal.ReleaseComObject(o);
            }
        }
    }
}
