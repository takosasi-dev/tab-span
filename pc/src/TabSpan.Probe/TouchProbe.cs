// 指定の位置にタッチを注入する(InitializeTouchInjection / InjectTouchInput。仕様書 §8.1、§9.8)。1 回のタップと、3 秒の長押しを更新の間隔を変えて 4 回。
// 入力を注入するので、自動では動かさず、人が手で実行する。実行の前に確認を求める。
// フラグの組み合わせは Microsoft Learn「InjectTouchInput」の表、接点の値の範囲は「POINTER_TOUCH_INFO」のとおり。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TabSpan.Probe;

internal static class TouchProbe
{
    private const int HoldMs = 3000;
    private const int Gap = 30; // 続けて注入すると ERROR_NOT_READY になることがある(Learn: 0.1 ms より近い呼び出し)ので、少し空ける
    private const POINTER_FLAGS Down = POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT | POINTER_FLAGS.POINTER_FLAG_DOWN;
    private const POINTER_FLAGS Update = POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT | POINTER_FLAGS.POINTER_FLAG_UPDATE;
    private const POINTER_FLAGS Up = POINTER_FLAGS.POINTER_FLAG_UP;

    internal static int Run(string xText, string yText)
    {
        if (!int.TryParse(xText, out var x) || !int.TryParse(yText, out var y))
        {
            Console.WriteLine("失敗: x と y は整数(物理ピクセルの画面座標)にしてください");
            return 2;
        }

        Console.Write($"({x},{y}) を 1 回タップし、続けて {HoldMs / 1000} 秒の長押しを 4 回(更新の間隔 50・100・200・500 ms)注入します。その位置にある物が押されます。続けますか [y/N]: ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y")
        {
            Console.WriteLine("やめました");
            return 0;
        }

        var initialized = PInvoke.InitializeTouchInjection(10, TOUCH_FEEDBACK_MODE.TOUCH_FEEDBACK_DEFAULT);
        Console.WriteLine($"InitializeTouchInjection(10, TOUCH_FEEDBACK_DEFAULT): {Result(initialized)}");
        if (!initialized)
        {
            return 1;
        }

        var tapDown = Inject(x, y, Down);
        Thread.Sleep(Gap);
        Console.WriteLine($"タップ: 押す = {tapDown}、離す = {Inject(x, y, Up)}");

        PInvoke.timeBeginPeriod(1); // Sleep の刻みを細かくして、更新の間隔を指定に近づける
        try
        {
            foreach (var interval in new[] { 50, 100, 200, 500 })
            {
                Thread.Sleep(500);
                var down = Inject(x, y, Down);
                var updates = 0;
                var failures = 0;
                var firstFailure = "";
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds + interval <= HoldMs)
                {
                    Thread.Sleep(interval);
                    updates++;
                    var result = Inject(x, y, Update);
                    if (result != "通った" && failures++ == 0)
                    {
                        firstFailure = $"、最初の失敗は押してから {clock.ElapsedMilliseconds} ms 後で {result}";
                    }
                }

                Thread.Sleep(Gap);
                var up = Inject(x, y, Up);
                Console.WriteLine($"長押し(更新の間隔 {interval} ms): 押す = {down}、更新 {updates} 回のうち失敗 {failures} 回{firstFailure}、離す({clock.ElapsedMilliseconds} ms 後)= {up}");
            }
        }
        finally
        {
            PInvoke.timeEndPeriod(1);
        }

        Console.WriteLine("戻り値が通っていても、相手のウィンドウで長押しが途中で切れたかどうかは、この数字からは分かりません。画面で見た結果を書き足してください。");
        Console.WriteLine("管理者で動いているウィンドウには届きません。");
        return 0;
    }

    private static string Inject(int x, int y, POINTER_FLAGS flags)
    {
        var contact = new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_TOUCH,
                pointerId = 0,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(x, y),
            },
            touchMask = PInvoke.TOUCH_MASK_CONTACTAREA | PInvoke.TOUCH_MASK_ORIENTATION | PInvoke.TOUCH_MASK_PRESSURE,
            rcContact = new RECT { left = x - 2, top = y - 2, right = x + 2, bottom = y + 2 },
            orientation = 90,
            pressure = 512,
        };
        return Result(PInvoke.InjectTouchInput([contact]));
    }

    private static string Result(BOOL succeeded) => succeeded ? "通った" : $"失敗 (GetLastError={Marshal.GetLastPInvokeError()})";
}
