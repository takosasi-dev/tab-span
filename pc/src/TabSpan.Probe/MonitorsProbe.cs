// モニターの一覧を出す(仕様書 §8.2)。ほかのサブコマンドが使う「識別子からモニターを引く」処理もここに置く。
// EnumDisplayDevices の 1 段目(アダプターの出力)と 2 段目(モニター)の両方を、付いていない物も含めて出す。
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace TabSpan.Probe;

/// <summary>アダプターの出力 1 つ。Mode は「現在の設定」か「レジストリの設定」か「取れない」。</summary>
internal sealed record MonitorEntry(
    string DeviceName,
    string DeviceString,
    string DeviceId,
    DISPLAY_DEVICE_STATE_FLAGS Flags,
    IReadOnlyList<(string Name, string Text, string Id, DISPLAY_DEVICE_STATE_FLAGS Flags)> Children,
    string Mode,
    int X,
    int Y,
    int Width,
    int Height,
    uint Hz)
{
    internal bool Attached => Flags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP);

    internal bool Primary => Flags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_PRIMARY_DEVICE);

    /// <summary>確認の文に出す名前(DeviceName と、アダプターとモニターの DeviceString)。</summary>
    internal string Label => $"{DeviceName} ({DeviceString}{string.Concat(Children.Select(c => " / " + c.Text))})";
}

internal static class MonitorsProbe
{
    internal static int Run()
    {
        var all = All();
        Console.WriteLine($"モニターの一覧: {all.Count} 件(大きさと位置は物理ピクセル。付いていない物も出す)");
        foreach (var m in all)
        {
            Console.WriteLine();
            Console.WriteLine($"DeviceName  : {m.DeviceName}");
            Console.WriteLine($"DeviceString: {m.DeviceString}");
            Console.WriteLine($"DeviceID    : {m.DeviceId}");
            Console.WriteLine($"付いているか: {YesNo(m.Attached)}    主モニターか: {YesNo(m.Primary)}    StateFlags: 0x{(uint)m.Flags:X8} ({m.Flags})");
            Console.WriteLine($"大きさ      : {m.Width}x{m.Height} @ {m.Hz} Hz    位置: ({m.X},{m.Y})    [{m.Mode}]");
            foreach (var c in m.Children)
            {
                Console.WriteLine($"  モニター DeviceName  : {c.Name}");
                Console.WriteLine($"  モニター DeviceString: {c.Text}");
                Console.WriteLine($"  モニター DeviceID    : {c.Id}");
                Console.WriteLine($"  モニター StateFlags  : 0x{(uint)c.Flags:X8} ({c.Flags})");
            }
        }

        return 0;
    }

    internal static List<MonitorEntry> All()
    {
        var list = new List<MonitorEntry>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICEW { cb = (uint)Unsafe.SizeOf<DISPLAY_DEVICEW>() };
            if (!PInvoke.EnumDisplayDevices(null, i, ref adapter, 0))
            {
                return list;
            }

            var name = adapter.DeviceName.ToString();
            var children = new List<(string, string, string, DISPLAY_DEVICE_STATE_FLAGS)>();
            for (uint j = 0; ; j++)
            {
                var child = new DISPLAY_DEVICEW { cb = (uint)Unsafe.SizeOf<DISPLAY_DEVICEW>() };
                if (!PInvoke.EnumDisplayDevices(name, j, ref child, 0))
                {
                    break;
                }

                children.Add((child.DeviceName.ToString(), child.DeviceString.ToString(), child.DeviceID.ToString(), child.StateFlags));
            }

            var mode = new DEVMODEW { dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>() };
            var source = "現在の設定";
            if (!PInvoke.EnumDisplaySettingsEx(name, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode, 0))
            {
                source = PInvoke.EnumDisplaySettingsEx(name, ENUM_DISPLAY_SETTINGS_MODE.ENUM_REGISTRY_SETTINGS, ref mode, 0)
                    ? "レジストリの設定(現在の設定は取れない)"
                    : "設定を取れない";
            }

            list.Add(new MonitorEntry(
                name,
                adapter.DeviceString.ToString(),
                adapter.DeviceID.ToString(),
                adapter.StateFlags,
                children,
                source,
                mode.dmPosition.x,
                mode.dmPosition.y,
                (int)mode.dmPelsWidth,
                (int)mode.dmPelsHeight,
                mode.dmDisplayFrequency));
        }
    }

    /// <summary>識別子(DeviceName。先頭の \\.\ は省いてよい)からモニターを引く。無ければ、その旨を出して null。</summary>
    internal static MonitorEntry? Find(string identifier)
    {
        var all = All();
        var found = all.FirstOrDefault(m =>
            m.DeviceName.Equals(identifier, StringComparison.OrdinalIgnoreCase)
            || m.DeviceName.Equals(@"\\.\" + identifier, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            Console.WriteLine($"失敗: 識別子 {identifier} のモニターがありません。使える識別子: {string.Join(" ", all.Select(m => m.DeviceName))}");
        }

        return found;
    }

    /// <summary>デスクトップに付いているモニターの HMONITOR を、DeviceName から引く。無ければ Null。</summary>
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

    private static string YesNo(bool value) => value ? "はい" : "いいえ";
}
