// モニター 1 枚を Windows.Graphics.Capture で取り込む準備(D3D11 のデバイス → 取り込みの対象 → フレームの置き場 → セッション)を 1 歩ずつ行う。
// capture と encode が共通で使う。どの歩で失敗したかを出して null を返す。FrameArrived は使わず、呼ぶ側が TryGetNextFrame を回す。
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;

namespace TabSpan.Probe;

internal sealed class CaptureRig : IDisposable
{
    internal ID3D11Device Device = null!;
    internal ID3D11DeviceContext Context = null!;
    internal IDirect3DDevice WinRtDevice = null!;
    internal GraphicsCaptureItem Item = null!;
    internal Direct3D11CaptureFramePool Pool = null!;
    internal GraphicsCaptureSession Session = null!;

    internal static CaptureRig? Open(string identifier)
    {
        var monitor = MonitorsProbe.Find(identifier);
        if (monitor is null)
        {
            return null;
        }

        Console.WriteLine($"対象: {monitor.Label}  {monitor.Width}x{monitor.Height} 位置 ({monitor.X},{monitor.Y})");
        var handle = monitor.Attached ? MonitorsProbe.HandleOf(monitor.DeviceName) : default;
        if (handle.IsNull)
        {
            Console.WriteLine("失敗: このモニターの HMONITOR がありません(デスクトップに付いていないモニターは取り込めません)");
            return null;
        }

        Console.WriteLine($"GraphicsCaptureSession.IsSupported: {Attempt.Read(() => GraphicsCaptureSession.IsSupported().ToString())}");

        var rig = new CaptureRig();
        var ok =
            Attempt.Step("D3D11CreateDevice (HARDWARE, BGRA_SUPPORT)", () =>
                PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, default, PInvoke.D3D11_SDK_VERSION, out rig.Device, out rig.Context).ThrowOnFailure())
            && Attempt.Step("CreateDirect3D11DeviceFromDXGIDevice", () =>
            {
                PInvoke.CreateDirect3D11DeviceFromDXGIDevice((IDXGIDevice)rig.Device, out var inspectable).ThrowOnFailure();
                var pointer = Marshal.GetIUnknownForObject(inspectable);
                try
                {
                    rig.WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pointer);
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            })
            && Attempt.Step("IGraphicsCaptureItemInterop::CreateForMonitor", () =>
                GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForMonitor(handle, out rig.Item))
            && Attempt.Step("Direct3D11CaptureFramePool.CreateFreeThreaded (B8G8R8A8, 2 枚)", () =>
                rig.Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(rig.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, rig.Item.Size))
            && Attempt.Step("CreateCaptureSession", () => rig.Session = rig.Pool.CreateCaptureSession(rig.Item));
        if (!ok)
        {
            rig.Dispose();
            return null;
        }

        Console.WriteLine($"取り込みの対象の大きさ: {rig.Item.Size.Width}x{rig.Item.Size.Height}");
        return rig;
    }

    public void Dispose()
    {
        // セッションを閉じると取り込みが止まる。閉じるときの失敗も出力に出す
        if (Session is not null)
        {
            Attempt.Step("GraphicsCaptureSession.Dispose", Session.Dispose);
        }

        if (Pool is not null)
        {
            Attempt.Step("Direct3D11CaptureFramePool.Dispose", Pool.Dispose);
        }
    }
}
