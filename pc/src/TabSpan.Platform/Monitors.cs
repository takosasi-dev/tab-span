// モニターの一覧(FR-9): デスクトップに付いている物と、外れている「仮想」の物。大きさと位置は物理ピクセル(呼ぶ側は PerMonitorV2)。
// 「仮想」はモニターの DeviceID に virtualMonitorMatch の文字列が含まれるかで決める。識別子は DeviceName(\\.\DISPLAYn)。
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace TabSpan.Platform;

/// <summary>モニター 1 枚。DeviceName が識別子(設定の sourceMonitor に入れる値)。</summary>
public sealed record MonitorInfo(string DeviceName, string DisplayName, string MonitorId, int X, int Y, int Width, int Height, bool Primary, bool Attached, bool IsVirtual);

public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> List(IReadOnlyList<string> virtualMatch)
    {
        var list = new List<MonitorInfo>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICEW { cb = (uint)Unsafe.SizeOf<DISPLAY_DEVICEW>() };
            if (!PInvoke.EnumDisplayDevices(null, i, ref adapter, 0))
            {
                return list;
            }

            var name = adapter.DeviceName.ToString();
            var child = new DISPLAY_DEVICEW { cb = (uint)Unsafe.SizeOf<DISPLAY_DEVICEW>() };
            var hasMonitor = PInvoke.EnumDisplayDevices(name, 0, ref child, 0);
            var monitorId = hasMonitor ? child.DeviceID.ToString() : "";
            var isVirtual = monitorId.Length > 0 && virtualMatch.Any(m => m.Length > 0 && monitorId.Contains(m, StringComparison.OrdinalIgnoreCase));
            var attached = adapter.StateFlags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP);
            if (!attached && !isVirtual)
            {
                continue; // 付いていない出口(モニターの無い物)は出さない
            }

            var mode = new DEVMODEW { dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>() };
            if (!PInvoke.EnumDisplaySettingsEx(name, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode, 0))
            {
                PInvoke.EnumDisplaySettingsEx(name, ENUM_DISPLAY_SETTINGS_MODE.ENUM_REGISTRY_SETTINGS, ref mode, 0);
            }

            list.Add(new MonitorInfo(
                name,
                hasMonitor ? child.DeviceString.ToString() : adapter.DeviceString.ToString(),
                monitorId,
                mode.dmPosition.x,
                mode.dmPosition.y,
                (int)mode.dmPelsWidth,
                (int)mode.dmPelsHeight,
                adapter.StateFlags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_PRIMARY_DEVICE),
                attached,
                isVirtual));
        }
    }

    /// <summary>識別子(\\.\ は省いてよい)で引く。無ければ null。</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> list, string identifier) =>
        list.FirstOrDefault(m => m.DeviceName.Equals(identifier, StringComparison.OrdinalIgnoreCase)
            || m.DeviceName.Equals(@"\\.\" + identifier, StringComparison.OrdinalIgnoreCase));

    /// <summary>§9.2 の sourceMonitor: 指定があればそれ、空なら「仮想」の最初の 1 つ、それも無ければ null(未選択)。</summary>
    public static MonitorInfo? PickSource(IReadOnlyList<MonitorInfo> list, string sourceMonitor) =>
        sourceMonitor.Length > 0 ? Find(list, sourceMonitor) : list.FirstOrDefault(m => m.IsVirtual);

    /// <summary>デスクトップに付いているモニターの HMONITOR。無ければ Null。</summary>
    internal static unsafe HMONITOR HandleOf(string deviceName)
    {
        var result = HMONITOR.Null;
        MONITORENUMPROC each = (handle, _, _, _) =>
        {
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(handle, (MONITORINFO*)&info) && info.szDevice.ToString().Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                result = handle;
            }

            return true;
        };
        PInvoke.EnumDisplayMonitors(default, null, each, default);
        return result;
    }
}
