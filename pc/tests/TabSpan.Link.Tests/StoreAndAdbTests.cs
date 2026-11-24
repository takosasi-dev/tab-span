// 置き場の読み書き(設定 FR-26、identity、台帳、ログ FR-28)と、adb の引数(INV-10)・見回り(FR-1・FR-7)・探し方(FR-29)を確かめる。
// adb は偽の runner だけを使い、本物は呼ばない。
using System.Text;
using TabSpan.Core;

namespace TabSpan.Link.Tests;

public sealed class StoreTests
{
    [Fact]
    public void Settings_round_trip_broken_and_normalize()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new SettingsStore(path, clock);
        var settings = new Settings { Port = 50000, FpsCap = 30, SourceMonitor = @"\\.\DISPLAY2", VirtualMonitorMatch = ["MTT1337"] };
        store.Save(settings);
        var (loaded, broken) = store.Load();
        Assert.Null(broken);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(settings), System.Text.Json.JsonSerializer.Serialize(loaded));

        File.WriteAllText(path, "{ 壊れた");
        (loaded, broken) = store.Load();
        Assert.Equal(new Settings().Port, loaded.Port);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.broken-*.json"));
        Assert.False(File.Exists(path));

        File.WriteAllText(path, """{ "schemaVersion": 1, "port": 80, "fpsCap": 45, "bitrateMbps": 99, "maxLongEdge": 1000, "adbPollSec": 0, "backlogLimitFrames": 31, "language": "fr", "adbPath": null }""");
        (loaded, broken) = store.Load();
        Assert.Null(broken);
        Assert.Equal((47815, 60, 10, 0, 3, 3, "auto", ""), (loaded.Port, loaded.FpsCap, loaded.BitrateMbps, loaded.MaxLongEdge, loaded.AdbPollSec, loaded.BacklogLimitFrames, loaded.Language, loaded.AdbPath));

        File.WriteAllText(path, """{ "schemaVersion": 2 }""");
        Assert.NotNull(store.Load().BrokenCopy);
    }

    [Fact]
    public void Identity_is_kept_and_a_broken_file_is_replaced()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var path = Path.Combine(dir.Path, "identity.bin");
        var (first, created, _) = Identity.LoadOrCreate(path, clock);
        using (first)
        {
            Assert.True(created);
            Assert.True(first.Certificate.HasPrivateKey);
            var (again, createdAgain, _) = Identity.LoadOrCreate(path, clock);
            using (again)
            {
                Assert.False(createdAgain);
                Assert.Equal(first.PcInstanceId, again.PcInstanceId);
                Assert.Equal(first.CertDer, again.CertDer);
            }
        }

        File.WriteAllBytes(path, [1, 2, 3]);
        var (fresh, recreated, brokenCopy) = Identity.LoadOrCreate(path, clock);
        using (fresh)
        {
            Assert.True(recreated);
            Assert.NotNull(brokenCopy);
            Assert.True(File.Exists(brokenCopy));
        }
    }

    [Fact]
    public void Devices_round_trip_and_broken()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var path = Path.Combine(dir.Path, "devices.json");
        var registry = new DeviceRegistry([]);
        registry.Upsert("dev-1", "dtab d-01J", new byte[32], clock.UtcNow);
        DeviceStore.Save(path, registry.Entries);
        Assert.Contains("\"tokenSha256\"", File.ReadAllText(path), StringComparison.Ordinal);
        var (entries, broken) = DeviceStore.Load(path, clock);
        Assert.Null(broken);
        Assert.Equal(registry.Entries, entries);

        File.WriteAllText(path, "[{]");
        (entries, broken) = DeviceStore.Load(path, clock);
        Assert.Empty(entries);
        Assert.NotNull(broken);
    }

    [Fact]
    public void Log_rotates_at_5_MB_and_keeps_14_days()
    {
        using var dir = new TempDir();
        var clock = new FakeClock { UtcNow = new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero) };
        var log = new FileLog(dir.Path, clock);
        var today = clock.UtcNow.ToLocalTime();
        File.WriteAllBytes(Path.Combine(dir.Path, $"tabspan-{today:yyyyMMdd}.log"), new byte[FileLog.MaxBytes]);
        log.Write("次の行");
        Assert.Equal(2, Directory.GetFiles(dir.Path, $"tabspan-{today:yyyyMMdd}*.log").Length);

        var old15 = Path.Combine(dir.Path, $"tabspan-{today.AddDays(-15):yyyyMMdd}.log");
        var old14 = Path.Combine(dir.Path, $"tabspan-{today.AddDays(-14):yyyyMMdd}.log");
        File.WriteAllText(old15, "x");
        File.WriteAllText(old14, "x");
        log.DeleteOld();
        Assert.False(File.Exists(old15));
        Assert.True(File.Exists(old14));
    }
}

public sealed class AdbTests
{
    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, AdbResult> answer) : IAdbRunner
    {
        public List<(string Path, string[] Args)> Calls { get; } = [];

        public Task<AdbResult> RunAsync(string adbPath, IReadOnlyList<string> args, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((adbPath, [.. args]));
            }

            return Task.FromResult(answer(adbPath, args));
        }
    }

    [Fact]
    public void Commands_have_the_table_shapes_and_the_serial_stays_one_argument()
    {
        const string serial = "AB 12;&rm -rf";
        Assert.Equal(["version"], AdbCommands.Version());
        Assert.Equal(["devices"], AdbCommands.Devices());
        Assert.Equal(["-s", serial, "reverse", "tcp:47815", "tcp:47815"], AdbCommands.Reverse(serial, 47815));
        Assert.Equal(["-s", serial, "reverse", "--remove", "tcp:47815"], AdbCommands.ReverseRemove(serial, 47815));
        Assert.Equal(["-s", serial, "shell", "am", "start", "-n", "io.github.takosasidev.tabspan/.MainActivity"], AdbCommands.AmStart(serial));
    }

    [Fact]
    public async Task Watcher_reverses_once_launches_once_and_removes_at_exit()
    {
        const string serial = "DE 12;&x";
        var devicesOutput = $"List of devices attached\r\n{serial}\tdevice\r\nUN99\tunauthorized\r\n\r\n";
        var runner = new FakeRunner((_, args) => args[0] == "devices" ? new AdbResult(0, devicesOutput) : new AdbResult(0, ""));
        var logs = new List<string>();
        var watcher = new AdbWatcher(runner, "adb.exe", 47815, () => true, logs.Add);

        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Equal([serial], watcher.Reversed);
        Assert.Equal(["UN99"], watcher.Unauthorized);

        devicesOutput = "List of devices attached\r\n\r\n"; // 抜いた
        await watcher.PollOnceAsync(CancellationToken.None);
        Assert.Empty(watcher.Reversed);
        devicesOutput = $"List of devices attached\r\n{serial}\tdevice\r\n"; // 挿し直した
        await watcher.PollOnceAsync(CancellationToken.None);
        await watcher.RemoveAllAsync(CancellationToken.None);

        string[][] expected =
        [
            ["devices"], AdbCommands.Reverse(serial, 47815), AdbCommands.AmStart(serial),
            ["devices"],
            ["devices"],
            ["devices"], AdbCommands.Reverse(serial, 47815), AdbCommands.AmStart(serial),
            AdbCommands.ReverseRemove(serial, 47815),
        ];
        Assert.Equal(expected, runner.Calls.Select(c => c.Args));
        Assert.DoesNotContain(logs, l => l.Contains(serial, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watcher_reports_three_reverse_failures()
    {
        var runner = new FakeRunner((_, args) => args[0] == "devices" ? new AdbResult(0, "List of devices attached\nX1\tdevice\n") : new AdbResult(1, "error"));
        var watcher = new AdbWatcher(runner, "adb.exe", 47815, () => false, _ => { });
        for (var i = 0; i < 3; i++)
        {
            Assert.False(watcher.ReverseFailing);
            await watcher.PollOnceAsync(CancellationToken.None);
        }

        Assert.True(watcher.ReverseFailing);
        Assert.DoesNotContain(runner.Calls, c => c.Args.Contains("am"));
    }

    [Fact]
    public async Task Locator_order_and_skipping()
    {
        using var dir = new TempDir();
        string Make(string sub)
        {
            var p = Path.Combine(dir.Path, sub, "adb.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
            return p;
        }

        var configured = Make("configured");
        var onPath = Make("onpath");
        var local = Path.Combine(dir.Path, "local");
        var older = Make(@"local\Microsoft\WinGet\Packages\Google.PlatformTools_A\platform-tools");
        var newer = Make(@"local\Microsoft\WinGet\Packages\Google.PlatformTools_B\platform-tools");
        File.SetLastWriteTimeUtc(older, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var path = $@"C:\nowhere;""{Path.GetDirectoryName(onPath)}""";

        Assert.Equal([configured, onPath, newer, older], AdbLocator.Candidates(configured, path, local));

        var failing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { configured, onPath };
        var runner = new FakeRunner((p, _) => new AdbResult(failing.Contains(p) ? 1 : 0, ""));
        Assert.Equal(newer, await AdbLocator.FindAsync(configured, path, local, runner, CancellationToken.None));
        Assert.All(runner.Calls, c => Assert.Equal(["version"], c.Args));

        failing.Add(newer);
        failing.Add(older);
        Assert.Null(await AdbLocator.FindAsync(configured, path, local, runner, CancellationToken.None));
        Assert.Null(await AdbLocator.FindAsync("", null, Path.Combine(dir.Path, "none"), runner, CancellationToken.None));
    }

    [Fact]
    public void Parse_devices_matches_the_real_output_shape()
    {
        var parsed = AdbWatcher.ParseDevices(Encoding.UTF8.GetString("List of devices attached\r\nDE0123\tdevice\r\n\r\n"u8));
        Assert.Equal([("DE0123", "device")], parsed);
    }
}
