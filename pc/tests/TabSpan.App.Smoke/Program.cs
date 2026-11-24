// App の窓を出さない確かめ: STA で App.InitializeComponent を通し、AppHost(一時フォルダ・偽の adb・偽のホットキー・偽の Run キー)と各窓を作って閉じる。
// 窓を Show しない。トレイのアイコンを出さない。クリップボード・本物のレジストリ・adb・ホットキーに触らない。待ち受けは 127.0.0.1 の空いているポートだけ。
// 日本語と英語の 2 周で窓を作る。失敗が 1 つでもあれば終了コード 1。
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Input;
using System.Windows.Threading;
using TabSpan.App;
using TabSpan.Core;
using TabSpan.Link;
using TabSpan.Platform;

namespace TabSpan.App.Smoke;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    /// <param name="args">"en-US" などを渡すと、その UI の言語の OS として動かす(英語の OS で画面の定義を読めるかの確かめ)。</param>
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0)
        {
            var ui = CultureInfo.GetCultureInfo(args[0]);
            CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = ui;
            Check(Strings.Culture.Name == (ui.TwoLetterISOLanguageName == "ja" ? "ja" : "en"), $"auto の言語が OS の UI の言語({ui.Name})に合う: {Strings.Culture.Name}");
        }

        var dir = Path.Combine(Path.GetTempPath(), "tabspan-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(DataPaths.OverrideVariable, dir);
        try
        {
            Run(dir);
        }
#pragma warning disable CA1031 // 確かめの途中の例外は失敗として数えて出す
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failures.Add("例外: " + ex);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // ログが開いたままなど。一時フォルダなので残してよい
            }
        }

        foreach (var f in Failures)
        {
            Console.WriteLine("FAIL " + f);
        }

        Console.WriteLine(Failures.Count == 0 ? $"SMOKE OK ({_passed} checks)" : $"SMOKE FAILED ({Failures.Count} of {_passed + Failures.Count})");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        if (ok)
        {
            _passed++;
        }
        else
        {
            Failures.Add(what);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Thread.Sleep(25);
            Pump();
        }

        return condition();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void Run(string dir)
    {
        CheckStrings();
        CheckPureParts();

        // 一時の設定: 空いているポート(1024 以上)。USB は既定のオンのまま(adb は偽物で、version が通らない)
        var paths = DataPaths.Resolve();
        var port = FreePort();
        File.WriteAllText(paths.Settings, $$"""{ "schemaVersion": 1, "port": {{port}}, "touchEnabled": false }""");
        // 鍵を先に作り、台帳に 2 台を置く(鍵が新しく作られると台帳は空にされるため)
        var clock = new SmokeClock();
        Identity.LoadOrCreate(paths.Identity, clock).Identity.Dispose();
        var now = DateTimeOffset.UtcNow;
        DeviceStore.Save(paths.Devices,
        [
            new DeviceEntry("dev-a", "端末 A", new string('a', 64), now.AddDays(-3), now.AddDays(-1)),
            new DeviceEntry("dev-b", "Device B", new string('b', 64), now.AddDays(-2), now),
        ]);

        var app = new App();
        app.InitializeComponent(); // 飛ばさない(画面の定義の読み込みを通す)
        // new App() は OnStartup を Dispatcher に積む。回しても、入口が TabSpan.App でなければ本物の部品・トレイ・窓を作らないこと
        var cultureBefore = Strings.Culture;
        Pump();
        Check(app.MainWindow is null && app.Windows.Count == 0 && Strings.Culture == cultureBefore, "Smoke では App.OnStartup が本物の部品と窓を作らない");

        var runKey = new FakeRunKey();
        var adb = new FakeAdb();
        var hotKeys = new FakeHotKeys();
        var host = new AppHost(runKey, adb, hotKeys.Register, clock);
        try
        {
            CheckHost(host, paths, runKey, adb, hotKeys, clock);
            foreach (var lang in new[] { "ja", "en" })
            {
                Strings.UseLanguage(lang);
                CheckWindows(host, lang);
            }
        }
        finally
        {
            Strings.UseLanguage("ja");
            Check(host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30)), "AppHost を 30 秒以内に閉じる");
        }

        Check(adb.Calls.All(c => c.Length == 1 && c[0] == "version"), "adb には version だけを(偽物に)頼む: " + string.Join(" | ", adb.Calls.Select(c => string.Join(' ', c))));
        Check(hotKeys.Live == 0, "閉じたらホットキーを全部外す");
    }

    private static void CheckStrings()
    {
        var ja = Strings.SetOf(CultureInfo.GetCultureInfo("ja"))!.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var en = Strings.SetOf(CultureInfo.GetCultureInfo("en"))?.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Check(en is not null, "AC-FR30 英語の Satellite(en\\TabSpan.App.resources.dll)を読める");
        if (en is null)
        {
            return;
        }

        var onlyJa = ja.Keys.Except(en.Keys).ToList();
        var onlyEn = en.Keys.Except(ja.Keys).ToList();
        Check(onlyJa.Count == 0 && onlyEn.Count == 0, $"ja と en のキーがそろう(ja だけ: {string.Join(",", onlyJa)} / en だけ: {string.Join(",", onlyEn)})");
        Check(ja.Values.Concat(en.Values).All(v => v.Length > 0), "空の文言が無い");
        Check(en.Values.All(v => !v.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u4e00' and <= '\u9fff') || v == "日本語"), "英語の文言に日本語が混ざらない");
        var differ = ja.Count(kv => en[kv.Key] != kv.Value);
        Check(differ > ja.Count * 3 / 4, $"英語の文言が日本語と違う({differ}/{ja.Count})");
        Check(ja.Values.Concat(en.Values).All(v => !v.EnumerateRunes().Any(r => r.Value >= 0x1F000)), "絵文字を使わない");

        Strings.UseLanguage("en");
        Check(Strings.Get("Main_State") == "Status:", "UseLanguage(en) で英語を引く");
        Strings.UseLanguage("ja");
        Check(Strings.Get("Main_State") == "状態:", "UseLanguage(ja) で日本語を引く");
        Check(Strings.Resolve("auto", CultureInfo.GetCultureInfo("ja-JP")).Name == "ja", "auto + ja-JP → 日本語");
        Check(Strings.Resolve("auto", CultureInfo.GetCultureInfo("en-US")).Name == "en", "auto + en-US → 英語");
        Check(Strings.Resolve("auto", CultureInfo.GetCultureInfo("de-DE")).Name == "en", "auto + de-DE → 英語");
        Check(Strings.Get("No_Such_Key") == "[No_Such_Key]", "無いキーは [キー]");
    }

    private static void CheckPureParts()
    {
        // 自動起動(FR-25): 偽の書き込み先で
        var key = new FakeRunKey();
        StartupRegistration.Apply(key, true, @"C:\x\TabSpan.App.exe");
        StartupRegistration.Apply(key, true, @"C:\x\TabSpan.App.exe");
        Check(key.Value == "\"C:\\x\\TabSpan.App.exe\" --tray" && key.Sets == 1, "AC-FR25 真なら Run に \"<exe>\" --tray を 1 回だけ書く");
        StartupRegistration.Apply(key, false, @"C:\x\TabSpan.App.exe");
        Check(key.Value is null, "AC-FR25 偽なら Run の値を消す");

        // 多重起動の防止(FR-24・AC-41 の単体の分)
        var name = @"Local\TabSpan-smoke-" + Guid.NewGuid().ToString("N");
        using var signaled = new ManualResetEventSlim();
        var first = SingleInstance.TryAcquire(name, signaled.Set);
        Check(first is not null, "AC41 1 つ目は持ち主になる");
        var second = SingleInstance.TryAcquire(name, () => { });
        Check(second is null, "AC41 2 つ目は持ち主になれない");
        Check(signaled.Wait(TimeSpan.FromSeconds(5)), "AC41 2 つ目の起動で 1 つ目に「前に出して」が届く");
        first?.Dispose();
        var third = SingleInstance.TryAcquire(name, () => { });
        Check(third is not null, "AC41 1 つ目が終われば次が持ち主になる");
        third?.Dispose();

        // 設定の欄の確かめ
        Check(!SettingsView.TryRange("1023", 1024, 65535, out _) && SettingsView.TryRange(" 65535 ", 1024, 65535, out var p) && p == 65535
            && !SettingsView.TryRange("abc", 1024, 65535, out _) && !SettingsView.TryRange("-5", 5, 600, out _) && !SettingsView.TryRange("", 5, 600, out _),
            "範囲の外・数字でない物は保存しない");
        var hk = SettingsView.HotKeyText(ModifierKeys.Control | ModifierKeys.Alt, Key.T);
        Check(hk == "Ctrl+Alt+T" && HotKey.Parse(hk) is not null, "押したキーをホットキーの文字にし、それを読める: " + hk);
        Check(SettingsView.HotKeyText(ModifierKeys.None, Key.T) is null && SettingsView.HotKeyText(ModifierKeys.Control, Key.LeftCtrl) is null, "修飾キーなし・修飾キーだけは受けない");
        Check(SettingsView.HotKeyText(ModifierKeys.Control | ModifierKeys.Shift, Key.F9) is { } f9 && HotKey.Parse(f9) is not null, "Ctrl+Shift+F9 も読める");

        // トレイのアイコン(形でも分かれる: 待機は中が空、接続中は中に点、映しているは塗り)
        foreach (var size in new[] { 16, 32 })
        {
            using var waiting = Tray.DrawIcon(HostState.Waiting, size).ToBitmap();
            using var connected = Tray.DrawIcon(HostState.Connected, size).ToBitmap();
            using var streaming = Tray.DrawIcon(HostState.Streaming, size).ToBitmap();
            using var problem = Tray.DrawIcon(HostState.Problem, size).ToBitmap();
            var c = size / 2;
            Check(waiting.GetPixel(c, c).A == 0 && connected.GetPixel(c, c).A > 200 && streaming.GetPixel(c, c).A > 200, $"AC41 アイコン {size}px: 待機は中が空、接続中と映しているは中が埋まる");
            Check(streaming.GetPixel(size * 4 / 16, c).A > 200 && connected.GetPixel(size * 4 / 16, c).A == 0, $"AC41 アイコン {size}px: 映しているは全体が塗り、接続中は点だけ");
            Check(problem.GetPixel(size * 11 / 16, size - 1).A > 200 && waiting.GetPixel(size * 11 / 16, size - 1).A == 0, $"AC41 アイコン {size}px: 問題ありは右下に警告の印");
            Check(Tray.DrawIcon(HostState.Paused, size).ToBitmap().GetPixel(c, c) == connected.GetPixel(c, c), $"アイコン {size}px: 一時停止中は接続中と同じ");
        }

        // 診断(P5)
        var s = new StreamSecond(60, 1, 640_000, 2, new LatencySummary(60, 6_000, 11_000), new LatencySummary(60, 23_000, 41_000), 59, 1, 1920, 1080, 60, "Microsoft H.264 Encoder", false);
        var d = DiagnosticsView.DiagText.Of(s, 2_000, new LatencySummary(10, 1_000, 3_000));
        Strings.UseLanguage("ja");
        var text = d.ToCopyText();
        Check(d.CaptureP50 == "6.0" && d.TotalP95 == "41.0" && d.Fps == "59 / 60" && d.Rate == "640 kB/s" && d.Rtt == "2.0 ms" && d.Size == "1920x1080" && d.Touch == "3.0 ms",
            "AC30 診断の数字: " + string.Join(", ", d.CaptureP50, d.TotalP95, d.Fps, d.Rate, d.Rtt, d.Size, d.Touch));
        Check(d.ArriveP50 == "—" && d.DecodeP95 == "—", "送信から到着・到着から復号は L の合流まで「—」");
        Check(text.Contains("1920x1080", StringComparison.Ordinal) && text.Contains("取り込みから送信まで\t6.0\t11.0", StringComparison.Ordinal), "数値をコピーの文に数字が入る");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(text, @"\b\d{1,3}(\.\d{1,3}){3}\b"), "数値をコピーの文に IP アドレスが入らない");
        var none = DiagnosticsView.DiagText.Of(null, null, null);
        Check(none.TotalP50 == "—" && none.Fps == "—" && none.Touch == "—", "映していない間は「—」");
        var pts = DiagnosticsView.GraphPoints([1_000, 2_000, 4_000], 590, 90, out var max);
        Check(pts.Count == 3 && Math.Abs(pts[2].X - 590) < 0.01 && max == 4_000 && Math.Abs(pts[2].Y - 2) < 0.01, "グラフ: 右端が今、縦は最大値に合わせる");
    }

    private static void CheckHost(AppHost host, DataPaths paths, FakeRunKey runKey, FakeAdb adb, FakeHotKeys hotKeys, SmokeClock clock)
    {
        Check(!host.PortUnavailable && host.State == HostState.Waiting, $"127.0.0.1 で待ち受けて待機中({host.State})");
        Check(hotKeys.Registered.LastOrDefault() == "Ctrl+Alt+T" && !host.HotKeyFailed, "起動でホットキー(既定 Ctrl+Alt+T)を偽物に登録する");
        Check(WaitFor(() => host.AdbMissing), "偽の adb の version が通らないと「adb が見つからない」");
        Check(host.AdbFound is null && host.Watcher is null, "adb が無ければ見回りを始めない");
        Check(host.Devices.Count == 2 && host.Devices.Any(d => d.Name == "端末 A"), "端末の一覧(Devices)に台帳の 2 台");
        Check(GuideView.StepsDone(host) is { Adb: false, UsbDebugging: false }, "はじめに: adb と USB デバッグはまだ");

        // ApplySettings(P4)
        var changed = 0;
        host.Changed += () => Interlocked.Increment(ref changed);
        host.ApplySettings(host.Settings with { StartWithWindows = true });
        Check(runKey.Value == StartupRegistration.CommandFor(Environment.ProcessPath!) && runKey.Sets == 1, "AC-FR25 startWithWindows を真にすると Run に書く(偽物)");
        host.ApplySettings(host.Settings with { StartWithWindows = false });
        Check(runKey.Value is null, "AC-FR25 偽にすると消す");
        host.ApplySettings(host.Settings with { TouchHotkey = "Ctrl+Shift+F9" });
        Check(hotKeys.Registered.Last() == "Ctrl+Shift+F9" && hotKeys.Live == 1 && !host.HotKeyFailed, "FR-23 ホットキーを変えると前のを外して登録し直す");
        hotKeys.FailNext = true;
        host.ApplySettings(host.Settings with { TouchHotkey = "Ctrl+Alt+Q" });
        Check(host.HotKeyFailed && hotKeys.Live == 0, "FR-23 登録できなければ HotKeyFailed(設定の画面に出す)");
        host.ApplySettings(host.Settings with { BitrateMbps = 99, FpsCap = 30 });
        Check(host.Settings.BitrateMbps == 10 && host.Settings.FpsCap == 30, "範囲の外は Normalize で直す");
        var saved = new SettingsStore(paths.Settings, clock).Load().Settings;
        Check(saved.FpsCap == 30 && saved.TouchHotkey == "Ctrl+Alt+Q" && saved.Port == host.Settings.Port, "AC29 変えた値はすぐ settings.json に保存される");
        host.ApplySettings(host.Settings with { Language = "en" });
        Check(Strings.Culture.Name == "ja", "言語は次の起動から(今の文言は変えない)");
        var callsBefore = adb.Calls.Count;
        host.ApplySettings(host.Settings with { UsbEnabled = false });
        Check(WaitFor(() => !host.AdbMissing) && host.Watcher is null, "USB を切ると adb の見回りを止める");
        Thread.Sleep(200);
        Check(adb.Calls.Count == callsBefore, "USB が切れている間は adb を呼ばない");
        host.ApplySettings(host.Settings with { UsbEnabled = true });
        Check(WaitFor(() => host.AdbMissing), "USB を入れ直すと adb を探し直す");
        Check(changed > 0, "ApplySettings で Changed が来る");

        // ResetSettings(ペアリングは残す)
        host.ApplySettings(host.Settings with { DetachDelaySec = 120, VirtualMonitorMatch = ["SMOKE"] });
        host.ResetSettings();
        Check(host.Settings.DetachDelaySec == 30 && host.Settings.TouchHotkey == "Ctrl+Alt+T" && host.Settings.Language == "auto"
            && host.Settings.VirtualMonitorMatch.SequenceEqual(["SMOKE"]) && host.Devices.Count == 2, "初期値に戻す: P4 の項目は既定、端末と virtualMonitorMatch は残る");
        Check(hotKeys.Registered.Last() == "Ctrl+Alt+T" && !host.HotKeyFailed, "初期値に戻すとホットキーも既定で登録し直す");

        // 通知(画面 §5: 同じ種類は 60 秒に 1 回まで)
        var notes = new List<(string, string)>();
        host.Notify += (t, b) => notes.Add((t, b));
        host.RaiseNotify("x", "1");
        host.RaiseNotify("x", "2");
        host.RaiseNotify("y", "3");
        clock.Advance(TimeSpan.FromSeconds(59));
        host.RaiseNotify("x", "4");
        clock.Advance(TimeSpan.FromSeconds(2));
        host.RaiseNotify("x", "5");
        Check(string.Join(",", notes.Select(n => n.Item2)) == "1,3,5" && notes[0].Item1 == "TabSpan", "通知は同じ種類を 60 秒に 1 回まで: " + string.Join(",", notes.Select(n => n.Item2)));
        notes.Clear();
        host.ApplySettings(host.Settings with { TouchEnabled = true }); // 注入を準備できない PC でも Settings の値で切り替わる
        host.ToggleTouchByHotKey();
        Check(!host.Settings.TouchEnabled && notes.Count == 1 && notes[0].Item2.Contains("Ctrl+Alt+T", StringComparison.Ordinal), "ホットキーでタッチを止めると通知(戻すキーを添える)");
        host.ToggleTouchByHotKey();
        Check(host.Settings.TouchEnabled && notes.Count == 1, "ホットキーでタッチを戻したときは通知しない");

        // 解除と鍵の作り直し(LinkServer の UnpairAsync・ResetIdentityAsync)
        host.UnpairAsync("dev-a").Wait(TimeSpan.FromSeconds(10));
        Check(host.Devices.Count == 1 && DeviceStore.Load(paths.Devices, clock).Entries.Count == 1, "FR-4 解除すると台帳から消えて保存される");
        var keyBefore = File.ReadAllBytes(paths.Identity);
        host.ResetIdentityAsync().Wait(TimeSpan.FromSeconds(10));
        Check(host.Devices.Count == 0 && DeviceStore.Load(paths.Devices, clock).Entries.Count == 0 && File.Exists(paths.Identity) && !File.ReadAllBytes(paths.Identity).AsSpan().SequenceEqual(keyBefore), "鍵の作り直し: 台帳が空になり identity.bin が新しい鍵になる");

        host.Sample();
        Check(host.LastSecond is null && host.History.Count == 0, "映していなければ Sample は数字を持たない");
    }

    private static void CheckWindows(AppHost host, string lang)
    {
        var main = new MainWindow(host);
        var closed = false;
        main.Closed += (_, _) => closed = true;
        Check(main.Title == "TabSpan", $"[{lang}] メインの題");
        Check(((System.Windows.Controls.TabItem)main.Tabs.Items[1]).Header as string == (lang == "ja" ? "設定" : "Settings"), $"[{lang}] タブの見出しが言語に合う");
        for (var i = 0; i < 4; i++)
        {
            main.SelectTab(i);
            main.Refresh();
        }

        Pump();
        Check(main.StateText.Text == (lang == "ja" ? "待機中" : "Waiting"), $"[{lang}] 状態の文: {main.StateText.Text}");
        Check(main.FooterText.Text.Contains(host.Settings.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), $"[{lang}] 下の帯にポート");
        Check(main.Banner.Visibility == System.Windows.Visibility.Visible && main.BannerButton.Visibility == System.Windows.Visibility.Visible,
            $"[{lang}] 帯に案内とボタン(adb が無い): {main.BannerText.Text}");
        main.SettingsPage.Load();
        Check(main.SettingsPage.PortBox.Text == host.Settings.Port.ToString(CultureInfo.InvariantCulture) && main.SettingsPage.HotkeyBox.Text == host.Settings.TouchHotkey, $"[{lang}] 設定の欄に今の値");
        Check(main.GuidePage.Command1.Text == GuideView.VirtualDisplayCommand && main.GuidePage.Mark2Text.Text == Strings.Get("Guide_NotDone"), $"[{lang}] はじめにのコマンドと印: {main.GuidePage.Mark2Text.Text}");
        Check(main.DiagnosticsPage.TotalP50.Text == "—", $"[{lang}] 診断は映していない間「—」");

        main.Close();
        Pump();
        Check(!closed, $"[{lang}] × ではトレイにしまう(閉じない)");
        main.AllowClose = true;
        main.Close();
        Pump();
        Check(closed, $"[{lang}] 終了のときは閉じる");

        var pair = new PairWindow(host);
        var pairClosed = false;
        pair.Closed += (_, _) => pairClosed = true;
        pair.Close();
        Check(pairClosed, $"[{lang}] 端末を追加の窓を作って閉じる");

        var tray = new Tray(() => { }, _ => { }, () => { }, () => { });
        tray.Refresh(host);
        Check(tray.State == HostState.Waiting && tray.Tip == Strings.Get("Tray_Waiting"), $"[{lang}] トレイ: 待機中のツールチップ");
        tray.Notify("TabSpan", "x"); // 出していないので何もしない
        tray.Dispose();
    }

    private sealed class SmokeClock : IClock
    {
        private long _offsetUs;

        public long NowUs => SystemClock.Instance.NowUs + Interlocked.Read(ref _offsetUs);

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetUs) * 10);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetUs, (long)by.TotalMicroseconds);
    }

    private sealed class FakeRunKey : IRunKey
    {
        public string? Value { get; private set; }

        public int Sets { get; private set; }

        public string? Get() => Value;

        public void Set(string command)
        {
            Value = command;
            Sets++;
        }

        public void Delete() => Value = null;
    }

    private sealed class FakeAdb : IAdbRunner
    {
        public List<string[]> Calls { get; } = [];

        public Task<AdbResult> RunAsync(string adbPath, IReadOnlyList<string> args, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add([.. args]);
            }

            return Task.FromResult(new AdbResult(1, "")); // version が通らない = 使えない
        }
    }

    private sealed class FakeHotKeys
    {
        public List<string> Registered { get; } = [];

        public int Live { get; private set; }

        public bool FailNext { get; set; }

        public IDisposable? Register(string text, Action pressed)
        {
            Registered.Add(text);
            if (FailNext)
            {
                FailNext = false;
                return null;
            }

            Live++;
            return new Releaser(this);
        }

        private sealed class Releaser(FakeHotKeys owner) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (!_done)
                {
                    _done = true;
                    owner.Live--;
                }
            }
        }
    }
}
