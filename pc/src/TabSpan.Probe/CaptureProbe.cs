// モニター 1 枚を 5 秒取り込み、届いたフレームの数と大きさ、カーソルを含める設定と枠を消す設定が通ったかを出す(仕様書 §8.1、D-3、R-2)。
// 取り込んだ絵は数えるだけで、保存しない。枠が本当に消えたかは、動かす人が画面を見て確かめる。
using System.Diagnostics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Win32;

namespace TabSpan.Probe;

internal static class CaptureProbe
{
    private const int CaptureMs = 5000;

    internal static int Run(string identifier)
    {
        using var rig = CaptureRig.Open(identifier);
        if (rig is null)
        {
            return 1;
        }

        var session = rig.Session;
        Attempt.Step("IsCursorCaptureEnabled = true", () => session.IsCursorCaptureEnabled = true);
        Console.WriteLine($"  読み直し IsCursorCaptureEnabled: {Attempt.Read(() => session.IsCursorCaptureEnabled.ToString())}");

        // 枠を消す設定。Microsoft Learn は「先に RequestAccessAsync(Borderless) で許可を取る。許可が無いと、設定は通るが無視される」と書く。
        // パッケージ化していないアプリでどうなるかが未確認なので、許可を求める前と後の両方で設定して、読み直す。
        Console.WriteLine($"IsBorderRequired のプロパティがあるか: {Attempt.Read(() => ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired").ToString())}");
        Attempt.Step("IsBorderRequired = false (許可を求める前)", () => session.IsBorderRequired = false);
        Console.WriteLine($"  読み直し IsBorderRequired: {Attempt.Read(() => session.IsBorderRequired.ToString())}");
        Console.WriteLine($"GraphicsCaptureAccess.RequestAccessAsync(Borderless) (30 秒で打ち切り): {Attempt.Read(RequestBorderless)}");
        Attempt.Step("IsBorderRequired = false (許可を求めた後)", () => session.IsBorderRequired = false);
        Console.WriteLine($"  読み直し IsBorderRequired: {Attempt.Read(() => session.IsBorderRequired.ToString())}");

        var frames = 0;
        var firstMs = -1L;
        var sizes = new SortedSet<string>();
        PInvoke.timeBeginPeriod(1); // Sleep(1) を 1 ms 前後にして、置き場(2 枚)があふれる前に取り出す
        try
        {
            if (!Attempt.Step("StartCapture", session.StartCapture))
            {
                return 1;
            }

            Console.WriteLine($"{CaptureMs / 1000} 秒取り込みます。画面が変わらないとフレームは届かないので、このモニターの上でマウスを動かしてください。");
            Console.WriteLine($"取り込みのループ: {Attempt.Read(() =>
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < CaptureMs)
                {
                    using var frame = rig.Pool.TryGetNextFrame();
                    if (frame is null)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    if (frames++ == 0)
                    {
                        firstMs = clock.ElapsedMilliseconds;
                    }

                    sizes.Add($"{frame.ContentSize.Width}x{frame.ContentSize.Height}");
                }

                return "OK";
            })}");
        }
        finally
        {
            PInvoke.timeEndPeriod(1);
        }

        Console.WriteLine($"届いたフレームの数: {frames} ({frames * 1000.0 / CaptureMs:F1} fps)");
        Console.WriteLine($"最初のフレームまで: {(firstMs < 0 ? "届かなかった" : $"{firstMs} ms")}");
        Console.WriteLine($"フレームの大きさ: {(sizes.Count == 0 ? "(なし)" : string.Join(" ", sizes))}");
        Console.WriteLine("取り込みの間、このモニターの縁に色の枠が出たかどうかを、目で見て書き足してください(読み直しが False でも、許可が無ければ枠は出ます)。");
        return 0;
    }

    private static string RequestBorderless()
    {
        var task = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
        return task.Wait(TimeSpan.FromSeconds(30)) ? task.Result.ToString() : "時間切れ(30 秒待っても返事が無い)";
    }
}
