// IDisplayControl の本物: ChangeDisplaySettingsEx でモニターをデスクトップから外す・付ける(Probe の DisplayProbe から写した。D-18・R-5)。
// 主モニターと、付いている最後の 1 枚は外さない。付ける位置は DisplayLayout.Place(重なるなら主モニターの右隣)。
// 実在のモニターに呼ばない判定は呼ぶ側(VirtualDisplayKeeper)が持つ。実機では未確認(仕様 §8.1 の detach・attach が未記入)。
using System.Runtime.CompilerServices;
using TabSpan.Core;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace TabSpan.Platform;

public sealed class Win32DisplayControl : IDisplayControl
{
    public DisplayPlacement? CurrentPlacement(string deviceName) => Find(Attached(), deviceName)?.Placement;

    public bool Detach(string deviceName)
    {
        var attached = Attached();
        if (Find(attached, deviceName) is not { } me || me.Primary || attached.Count <= 1)
        {
            return false;
        }

        // Learn の手本と同じ: 位置・幅・高さ・色の深さ・周波数・フラグを有効にして、値はすべて 0
        var mode = new DEVMODEW
        {
            dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>(),
            dmFields = DEVMODE_FIELD_FLAGS.DM_POSITION | DEVMODE_FIELD_FLAGS.DM_PELSWIDTH | DEVMODE_FIELD_FLAGS.DM_PELSHEIGHT
                | DEVMODE_FIELD_FLAGS.DM_BITSPERPEL | DEVMODE_FIELD_FLAGS.DM_DISPLAYFREQUENCY | DEVMODE_FIELD_FLAGS.DM_DISPLAYFLAGS,
        };
        return Change(me.Name, mode);
    }

    public bool Attach(string deviceName, DisplayPlacement placement)
    {
        var attached = Attached();
        if (Find(attached, deviceName) is not null)
        {
            return true;
        }

        if (attached.FirstOrDefault(a => a.Primary) is not { } primary)
        {
            return false;
        }

        var p = DisplayLayout.Place(placement, primary.Placement, [.. attached.Select(a => a.Placement)]);
        var mode = new DEVMODEW { dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>(), dmFields = DEVMODE_FIELD_FLAGS.DM_POSITION };
        mode.dmPosition.x = p.X;
        mode.dmPosition.y = p.Y;
        if (p.Width > 0 && p.Height > 0)
        {
            mode.dmFields |= DEVMODE_FIELD_FLAGS.DM_PELSWIDTH | DEVMODE_FIELD_FLAGS.DM_PELSHEIGHT;
            mode.dmPelsWidth = (uint)p.Width;
            mode.dmPelsHeight = (uint)p.Height;
        }

        if (p.RefreshHz > 0)
        {
            mode.dmFields |= DEVMODE_FIELD_FLAGS.DM_DISPLAYFREQUENCY;
            mode.dmDisplayFrequency = (uint)p.RefreshHz;
        }

        return Change(deviceName, mode);
    }

    private static Entry? Find(List<Entry> list, string deviceName) =>
        list.FirstOrDefault(e => e.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>デスクトップに付いている出口と、今の位置・大きさ・周波数。</summary>
    private static List<Entry> Attached()
    {
        var list = new List<Entry>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICEW { cb = (uint)Unsafe.SizeOf<DISPLAY_DEVICEW>() };
            if (!PInvoke.EnumDisplayDevices(null, i, ref adapter, 0))
            {
                return list;
            }

            if (!adapter.StateFlags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP))
            {
                continue;
            }

            var name = adapter.DeviceName.ToString();
            var mode = new DEVMODEW { dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>() };
            if (!PInvoke.EnumDisplaySettingsEx(name, ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode, 0))
            {
                continue;
            }

            list.Add(new Entry(name, adapter.StateFlags.HasFlag(DISPLAY_DEVICE_STATE_FLAGS.DISPLAY_DEVICE_PRIMARY_DEVICE),
                new DisplayPlacement(mode.dmPosition.x, mode.dmPosition.y, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency)));
        }
    }

    private static unsafe bool Change(string deviceName, DEVMODEW mode) =>
        PInvoke.ChangeDisplaySettingsEx(deviceName, mode, CDS_TYPE.CDS_UPDATEREGISTRY, null) == DISP_CHANGE.DISP_CHANGE_SUCCESSFUL;

    private sealed record Entry(string Name, bool Primary, DisplayPlacement Placement);
}
