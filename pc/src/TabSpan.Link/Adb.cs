// adb の操作: 引数の組み立て(§9.6 の 5 つの形だけ。INV-10)、場所の探し方(FR-29)、端末の見回りと reverse・am start(FR-1・FR-7)。
// 引数は 1 つずつ分けて渡し、シェルを通さない。1 回の実行は 10 秒で打ち切る。adb の実行は IAdbRunner の裏に置く(テストは偽物)。
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TabSpan.Link;

/// <summary>adb を 1 回実行した結果。時間切れや起動の失敗は ExitCode -1。</summary>
public sealed record AdbResult(int ExitCode, string Output);

public interface IAdbRunner
{
    Task<AdbResult> RunAsync(string adbPath, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>§9.6 の表の形だけを作る。</summary>
public static class AdbCommands
{
    public const string AppComponent = "io.github.takosasidev.tabspan/.MainActivity";

    public static string[] Version() => ["version"];

    public static string[] Devices() => ["devices"];

    public static string[] Reverse(string serial, int port) => ["-s", serial, "reverse", Tcp(port), Tcp(port)];

    public static string[] ReverseRemove(string serial, int port) => ["-s", serial, "reverse", "--remove", Tcp(port)];

    public static string[] AmStart(string serial) => ["-s", serial, "shell", "am", "start", "-n", AppComponent];

    private static string Tcp(int port) => "tcp:" + port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>本物の adb を動かす。終了だけを待ち、パイプの終わりは待たない(adb devices が起こしたサーバーがパイプを握ったままになる)。</summary>
public sealed class ProcessAdbRunner : IAdbRunner
{
    public const int TimeoutMs = 10_000;

    public Task<AdbResult> RunAsync(string adbPath, IReadOnlyList<string> args, CancellationToken ct) => Task.Run(() =>
    {
        var info = new ProcessStartInfo(adbPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        var output = new StringBuilder();
        void Collect(string? line)
        {
            if (line is not null)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }
            }
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new AdbResult(-1, ex.Message);
        }

        if (process is null)
        {
            return new AdbResult(-1, "プロセスを始められない");
        }

        using (process)
        {
            process.OutputDataReceived += (_, e) => Collect(e.Data);
            process.ErrorDataReceived += (_, e) => Collect(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill();
                return new AdbResult(-1, $"{TimeoutMs / 1000} 秒で打ち切った");
            }

            Thread.Sleep(100); // 届く途中の行を少し待つ
            lock (output)
            {
                return new AdbResult(process.ExitCode, output.ToString());
            }
        }
    }, ct);
}

/// <summary>FR-29: 設定の場所 → PATH → winget の既定の場所(新しい順)。adb version の終了コードが 0 でない物は飛ばす。</summary>
public static class AdbLocator
{
    public static IEnumerable<string> Candidates(string configured, string? pathVariable, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            yield return configured;
        }

        foreach (var dir in (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = Path.Combine(dir.Trim('"'), "adb.exe");
            if (File.Exists(p))
            {
                yield return p;
            }
        }

        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var p in Directory.EnumerateDirectories(packages, "Google.PlatformTools_*")
                         .Select(d => Path.Combine(d, "platform-tools", "adb.exe"))
                         .Where(File.Exists)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                yield return p;
            }
        }
    }

    /// <summary>使える adb の場所。無ければ null。</summary>
    public static async Task<string?> FindAsync(string configured, string? pathVariable, string localAppData, IAdbRunner runner, CancellationToken ct)
    {
        foreach (var p in Candidates(configured, pathVariable, localAppData).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if ((await runner.RunAsync(p, AdbCommands.Version(), ct).ConfigureAwait(false)).ExitCode == 0)
            {
                return p;
            }
        }

        return null;
    }
}

/// <summary>
/// FR-1・FR-7: adbPollSec ごとに adb devices を見て、状態が device の端末に reverse を 1 回張る。消えた端末は表から外す。
/// 張った直後に、autoLaunch が真なら am start を 1 回(同じ端末には一覧から消えるまで再実行しない)。終わる時に張った端末へ reverse --remove。
/// </summary>
public sealed class AdbWatcher(IAdbRunner runner, string adbPath, int port, Func<bool> autoLaunch, Action<string> log)
{
    public const int FailuresToReport = 3;

    private readonly Lock _lock = new();
    private readonly HashSet<string> _reversed = [];
    private readonly HashSet<string> _launched = [];
    private readonly Dictionary<string, int> _failures = [];
    private string[] _unauthorized = [];

    /// <summary>見回りの結果が変わった(画面の更新用)。</summary>
    public event Action? Changed;

    /// <summary>reverse を張った端末のシリアル。</summary>
    public IReadOnlyList<string> Reversed { get { lock (_lock) { return [.. _reversed]; } } }

    /// <summary>USB デバッグを許可していない端末(案内の対象)。</summary>
    public IReadOnlyList<string> Unauthorized { get { lock (_lock) { return _unauthorized; } } }

    /// <summary>reverse が 3 回続けて失敗している端末がある(画面に出す)。</summary>
    public bool ReverseFailing { get { lock (_lock) { return _failures.Values.Any(f => f >= FailuresToReport); } } }

    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>1 回見回る。</summary>
    public async Task PollOnceAsync(CancellationToken ct)
    {
        var devices = await runner.RunAsync(adbPath, AdbCommands.Devices(), ct).ConfigureAwait(false);
        if (devices.ExitCode != 0)
        {
            log($"adb devices が失敗した(終了コード {devices.ExitCode})");
            return;
        }

        var listed = ParseDevices(devices.Output);
        lock (_lock)
        {
            _unauthorized = [.. listed.Where(d => d.State == "unauthorized").Select(d => d.Serial)];
            var present = listed.Select(d => d.Serial).ToHashSet();
            _reversed.RemoveWhere(s => !present.Contains(s));
            _launched.RemoveWhere(s => !present.Contains(s));
            foreach (var gone in _failures.Keys.Where(s => !present.Contains(s)).ToList())
            {
                _failures.Remove(gone);
            }
        }

        foreach (var (serial, _) in listed.Where(d => d.State == "device"))
        {
            bool already;
            lock (_lock)
            {
                already = _reversed.Contains(serial);
            }

            if (already)
            {
                continue;
            }

            var r = await runner.RunAsync(adbPath, AdbCommands.Reverse(serial, port), ct).ConfigureAwait(false);
            if (r.ExitCode != 0)
            {
                lock (_lock)
                {
                    _failures[serial] = _failures.GetValueOrDefault(serial) + 1;
                }

                log($"adb reverse が失敗した(終了コード {r.ExitCode}): {r.Output.Trim()}");
                continue;
            }

            bool launch;
            lock (_lock)
            {
                _reversed.Add(serial);
                _failures.Remove(serial);
                launch = autoLaunch() && _launched.Add(serial);
            }

            log($"端末に reverse tcp:{port} を張った");
            if (launch)
            {
                var a = await runner.RunAsync(adbPath, AdbCommands.AmStart(serial), ct).ConfigureAwait(false);
                log($"端末のアプリを起動した(終了コード {a.ExitCode})");
            }
        }

        Changed?.Invoke();
    }

    /// <summary>張った端末それぞれに reverse --remove を行う(終了時)。</summary>
    public async Task RemoveAllAsync(CancellationToken ct)
    {
        string[] serials;
        lock (_lock)
        {
            serials = [.. _reversed];
            _reversed.Clear();
        }

        foreach (var serial in serials)
        {
            var r = await runner.RunAsync(adbPath, AdbCommands.ReverseRemove(serial, port), ct).ConfigureAwait(false);
            log($"reverse --remove tcp:{port}(終了コード {r.ExitCode})");
        }
    }

    /// <summary>adb devices の出力の「シリアル + タブ + 状態」の行を読む(§8.8 の実物の形)。</summary>
    public static IReadOnlyList<(string Serial, string State)> ParseDevices(string output) =>
        [.. output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Contains('\t', StringComparison.Ordinal))
            .Select(l => l.Split('\t', 2))
            .Where(p => p[0].Length > 0)
            .Select(p => (p[0], p[1].Trim()))];
}
