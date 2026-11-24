// モニターをデスクトップから外す・付ける(ChangeDisplaySettingsEx。仕様書 §8.1、D-18、R-5)。ディスプレイの構成を変えるので、実行の前に確認を求める。
// DEVMODE の入れ方は Microsoft Learn「Enumeration and Display Control」のとおり。主モニターと、付いている最後の 1 枚は外さない。
// 外す前の位置と大きさは出力に出すだけで、ファイルには保存しない。
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace TabSpan.Probe;

internal static class DisplayProbe
{
    internal static int Detach(string identifier)
    {
        var monitor = MonitorsProbe.Find(identifier);
        if (monitor is null)
        {
            return 1;
        }

        if (!monitor.Attached)
        {
            Console.WriteLine($"やめました: {monitor.Label} は、もうデスクトップから外れています");
            return 1;
        }

        if (monitor.Primary)
        {
            Console.WriteLine($"断りました: {monitor.Label} は主モニターです。主モニターは外しません");
            return 1;
        }

        if (MonitorsProbe.All().Count(m => m.Attached) <= 1)
        {
            Console.WriteLine($"断りました: {monitor.Label} は、付いている最後の 1 枚です");
            return 1;
        }

        Console.WriteLine($"外す前: 位置 ({monitor.X},{monitor.Y})、大きさ {monitor.Width}x{monitor.Height}");
        Console.WriteLine($"同じ位置と大きさに戻す命令: TabSpan.Probe.exe attach {monitor.DeviceName} {monitor.X} {monitor.Y} {monitor.Width} {monitor.Height}");
        Console.WriteLine("仮想ディスプレイでないモニターを外すと、その画面は消えます。上の命令で戻せます。");
        if (!Confirm($"{monitor.Label} をデスクトップから外します。続けますか [y/N]: "))
        {
            return 0;
        }

        // Learn の手本と同じ: 位置・幅・高さ・色の深さ・周波数・フラグを有効にして、値はすべて 0
        var mode = new DEVMODEW
        {
            dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>(),
            dmFields = DEVMODE_FIELD_FLAGS.DM_POSITION | DEVMODE_FIELD_FLAGS.DM_PELSWIDTH | DEVMODE_FIELD_FLAGS.DM_PELSHEIGHT
                | DEVMODE_FIELD_FLAGS.DM_BITSPERPEL | DEVMODE_FIELD_FLAGS.DM_DISPLAYFREQUENCY | DEVMODE_FIELD_FLAGS.DM_DISPLAYFLAGS,
        };
        return Change(monitor, mode);
    }

    internal static int Attach(string identifier, string[]? placement)
    {
        var monitor = MonitorsProbe.Find(identifier);
        if (monitor is null)
        {
            return 1;
        }

        if (monitor.Attached)
        {
            Console.WriteLine($"やめました: {monitor.Label} は、もうデスクトップに付いています(付いているモニターの位置と大きさは変えません)");
            return 1;
        }

        var mode = new DEVMODEW { dmSize = (ushort)Unsafe.SizeOf<DEVMODEW>(), dmFields = DEVMODE_FIELD_FLAGS.DM_POSITION };
        if (placement is null)
        {
            // Learn: 付けるときは DM_POSITION を立て、いまあるモニターに隣り合う位置を入れる。大きさは Windows に任せる
            var rightmost = MonitorsProbe.All().Where(m => m.Attached).MaxBy(m => m.X + m.Width);
            if (rightmost is null)
            {
                Console.WriteLine("失敗: 付いているモニターが 1 枚もありません");
                return 1;
            }

            mode.dmPosition.x = rightmost.X + rightmost.Width;
            mode.dmPosition.y = rightmost.Y;
            Console.WriteLine($"位置と大きさの指定なし: {rightmost.DeviceName} の右隣 ({mode.dmPosition.x},{mode.dmPosition.y}) に置き、大きさは Windows に任せます");
        }
        else
        {
            if (!int.TryParse(placement[0], out var x) || !int.TryParse(placement[1], out var y)
                || !uint.TryParse(placement[2], out var width) || !uint.TryParse(placement[3], out var height) || width == 0 || height == 0)
            {
                Console.WriteLine("失敗: x y 幅 高さ は整数で、幅と高さは 1 以上にしてください");
                return 2;
            }

            mode.dmFields |= DEVMODE_FIELD_FLAGS.DM_PELSWIDTH | DEVMODE_FIELD_FLAGS.DM_PELSHEIGHT;
            mode.dmPosition.x = x;
            mode.dmPosition.y = y;
            mode.dmPelsWidth = width;
            mode.dmPelsHeight = height;
            Console.WriteLine($"指定: 位置 ({x},{y})、大きさ {width}x{height}");
        }

        return Confirm($"{monitor.Label} をデスクトップに付けます。続けますか [y/N]: ") ? Change(monitor, mode) : 0;
    }

    private static unsafe int Change(MonitorEntry monitor, DEVMODEW mode)
    {
        var result = PInvoke.ChangeDisplaySettingsEx(monitor.DeviceName, mode, CDS_TYPE.CDS_UPDATEREGISTRY, null);
        Console.WriteLine($"ChangeDisplaySettingsEx(CDS_UPDATEREGISTRY) の戻り値: {result} ({(int)result})");
        var after = MonitorsProbe.All().FirstOrDefault(m => m.DeviceName == monitor.DeviceName);
        Console.WriteLine(after is null
            ? "実行後: このモニターは一覧から消えました"
            : $"実行後: 付いているか = {(after.Attached ? "はい" : "いいえ")}、位置 ({after.X},{after.Y})、大きさ {after.Width}x{after.Height} [{after.Mode}]");
        return result == DISP_CHANGE.DISP_CHANGE_SUCCESSFUL ? 0 : 1;
    }

    private static bool Confirm(string question)
    {
        Console.Write(question);
        if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
        {
            return true;
        }

        Console.WriteLine("やめました");
        return false;
    }
}
