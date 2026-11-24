// adb の場所(PATH → winget の既定の場所。仕様書 D-10)と、adb version・adb devices の出力を出す(仕様書 §8.8、§9.6)。
// 引数は 1 つずつ分けて渡し、シェルを通さない。1 回の実行は 10 秒で打ち切る。
// 端末のシリアルと、利用者のフォルダの名前は伏せて出す(出力を貼った先に残さないため)。
using System.Diagnostics;
using System.Text;

namespace TabSpan.Probe;

internal static class AdbProbe
{
    private const int TimeoutMs = 10_000;

    internal static int Run()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages", "Google.PlatformTools_Microsoft.Winget.Source_8wekyb3d8bbwe", "platform-tools", "adb.exe");
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "adb.exe"))
            .FirstOrDefault(File.Exists);
        var adb = fromPath ?? (File.Exists(winget) ? winget : null);
        if (adb is null)
        {
            Console.WriteLine($"adb の場所: 見つからない(PATH と {Scrub(winget)} を見た)");
            return 1;
        }

        Console.WriteLine($"adb の場所: {Scrub(adb)} ({(fromPath is null ? "winget の既定の場所" : "PATH")})");
        Execute(adb, "version", line => line);
        Console.WriteLine("(adb devices は、adb のサーバーが動いていなければ起こします。サーバーはこの Probe が終わっても残ります)");
        Execute(adb, "devices", MaskSerial);
        return 0;
    }

    private static void Execute(string adb, string argument, Func<string, string> mask)
    {
        Console.WriteLine();
        Console.WriteLine($"--- adb {argument}");
        var lines = new StringBuilder();
        void Collect(string prefix, string? line)
        {
            if (line is not null)
            {
                lock (lines)
                {
                    lines.AppendLine(prefix + Scrub(mask(line)));
                }
            }
        }

        var result = Attempt.Read(() =>
        {
            var info = new ProcessStartInfo(adb) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("プロセスを始められません");
            process.OutputDataReceived += (_, e) => Collect("", e.Data);
            process.ErrorDataReceived += (_, e) => Collect("[stderr] ", e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill();
                return $"失敗: {TimeoutMs / 1000} 秒で打ち切りました";
            }

            // 終了だけを待ち、パイプの終わりは待たない(adb devices が起こしたサーバーがパイプを握ったままになることがある)。届く途中の行を少し待つ
            Thread.Sleep(300);
            return $"終了コード {process.ExitCode}";
        });
        lock (lines)
        {
            Console.Write(lines);
        }

        Console.WriteLine($"({result})");
    }

    /// <summary>「シリアル + タブ + 状態」の行のシリアルを、先頭 2 文字 + *** にする。</summary>
    private static string MaskSerial(string line)
    {
        var tab = line.IndexOf('\t');
        return tab <= 0 ? line : string.Concat(line.AsSpan(0, Math.Min(2, tab)), "***", line.AsSpan(tab));
    }

    /// <summary>利用者のフォルダの場所を、環境変数の名前に置き換える。</summary>
    private static string Scrub(string text) => text
        .Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase)
        .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
}
