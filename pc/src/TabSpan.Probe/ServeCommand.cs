// serve: 窓を出さずに、App と同じ部品(Link・Platform)で待ち受けて映す(契約 §6)。呼び出し元の E2E の確かめ用。
// ペアリングは --pair で 120 秒受け付け、端末と同じ 6 桁が --approve-code(無ければ標準入力の 1 行)で渡された時だけ許可する。
// --lan で 0.0.0.0 で待ち、UDP の探索に返事をする(FR-2・FR-5。設定の lanEnabled は見ない)。1 秒ごとに状態を 1 行出す。終わる時に Bye(shutdown) と reverse --remove。置き場は TABSPAN_DATA_DIR で切り替えられる。
// タッチは既定で使わない。--touch log は注入せずに、注入するはずの接点を標準出力に出す(ファイルのログには座標を書かない。FR-28)。
// --touch inject は本当に注入する(人が手で動かすとき)。どちらも標準入力の t で有効と無効を切り替え、端末へ Notice(touchEnabled) を送る。
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using TabSpan.Core;
using TabSpan.Link;
using TabSpan.Platform;

namespace TabSpan.Probe;

internal static class ServeCommand
{
    private const string Usage = "serve [--monitor \\\\.\\DISPLAYn] [--fps 30|60] [--bitrate <Mbps>] [--seconds <N>] [--pair] [--approve-code <6桁>] [--no-adb] [--software] [--touch log|inject] [--lan]";

    private sealed record Options(string? Monitor, int? Fps, int? Bitrate, int Seconds, bool Pair, string? ApproveCode, bool NoAdb, bool Software, string? Touch, bool Lan);

    internal static int Run(string[] args)
    {
        var options = Parse(args);
        if (options is null)
        {
            Console.WriteLine($"使い方: TabSpan.Probe.exe {Usage}");
            return 2;
        }

        return RunAsync(options).GetAwaiter().GetResult();
    }

    private static Options? Parse(string[] args)
    {
        string? monitor = null, code = null, touch = null;
        int? fps = null, bitrate = null;
        int seconds = 0;
        bool pair = false, noAdb = false, software = false, lan = false;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            int? NextInt() => int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
            switch (args[i])
            {
                case "--monitor": monitor = Next(); if (monitor is null) { return null; } break;
                case "--fps": fps = NextInt(); if (fps is not (30 or 60)) { return null; } break;
                case "--bitrate": bitrate = NextInt(); if (bitrate is not (>= 2 and <= 40)) { return null; } break;
                case "--seconds": seconds = NextInt() ?? -1; if (seconds < 0) { return null; } break;
                case "--pair": pair = true; break;
                case "--approve-code": code = Next(); if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) { return null; } break;
                case "--no-adb": noAdb = true; break;
                case "--software": software = true; break;
                case "--lan": lan = true; break;
                case "--touch": touch = Next(); if (touch is not ("log" or "inject")) { return null; } break;
                default: return null;
            }
        }

        return new Options(monitor, fps, bitrate, seconds, pair, code, noAdb, software, touch, lan);
    }

    private static async Task<int> RunAsync(Options o)
    {
        var clock = SystemClock.Instance;
        var paths = DataPaths.Resolve();
        var log = new FileLog(paths.LogDir, clock);
        log.Written += line => Console.WriteLine($"[log] {line}");
        log.DeleteOld();
        Console.WriteLine($"置き場: {paths.DataDir}(ログ {paths.LogDir})");

        var (settings, brokenSettings) = new SettingsStore(paths.Settings, clock).Load();
        if (brokenSettings is not null)
        {
            log.Write($"設定を読めないので既定値で始める(元のファイルは {Path.GetFileName(brokenSettings)})");
        }

        var streamOptions = new StreamOptions(o.Fps ?? settings.FpsCap, o.Bitrate ?? settings.BitrateMbps, settings.MaxLongEdge, settings.BacklogLimitFrames);
        var (identity, created, brokenIdentity) = Identity.LoadOrCreate(paths.Identity, clock);
        using var identityLifetime = identity;
        var (entries, brokenDevices) = DeviceStore.Load(paths.Devices, clock);
        if (brokenIdentity is not null || brokenDevices is not null)
        {
            log.Write("ペアリングの情報を読めませんでした。端末を追加し直してください");
        }

        var registry = new DeviceRegistry(created ? [] : entries);
        var saveLock = new Lock();
        void Save()
        {
            lock (saveLock)
            {
                DeviceStore.Save(paths.Devices, registry.Entries);
            }
        }

        if (created)
        {
            Save(); // 鍵を作り直したので台帳を空にする(§10)
        }

        Console.WriteLine($"PC の証明書の指紋(先頭): {identity.Fingerprint[..16]}…  登録済みの端末: {registry.Entries.Count} 台");

        var monitors = Monitors.List(settings.VirtualMonitorMatch);
        var source = o.Monitor is { } m ? Monitors.Find(monitors, m) : Monitors.PickSource(monitors, settings.SourceMonitor);
        foreach (var mon in monitors)
        {
            Console.WriteLine($"  {(ReferenceEquals(mon, source) ? "*" : " ")} {mon.DeviceName}  {mon.Width}x{mon.Height}  ({mon.X},{mon.Y}){(mon.Primary ? "  主" : "")}{(mon.IsVirtual ? "  仮想" : "")}{(mon.Attached ? "" : "  外れている")}");
        }

        TouchThread? touch = null;
        if (o.Touch is "inject")
        {
            Windows.Win32.PInvoke.SetProcessDpiAwarenessContext(Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); // 座標を物理ピクセルで扱う(§9.8)
            touch = new TouchThread(() =>
            {
                var injector = Win32TouchInjector.Create(out var error);
                if (injector is null)
                {
                    Console.WriteLine($"タッチの注入を準備できない(InitializeTouchInjection のエラー {error})。タッチは使いません");
                }

                return injector;
            }, settings.TouchEnabled, settings.TouchRefreshMs, clock, log.Write);
        }
        else if (o.Touch is "log")
        {
            touch = new TouchThread(() => new CallbackTouchInjector(cs => Console.WriteLine("[touch] " + string.Join("  ", cs.Select(c => $"{c.Slot}:{c.Change}({c.X},{c.Y})")))),
                settings.TouchEnabled, settings.TouchRefreshMs, clock, log.Write);
        }

        using var touchLifetime = touch;
        Console.WriteLine(touch is null ? "タッチ: 使わない" : $"タッチ: {(o.Touch == "log" ? "注入せずに出す" : "注入する")}(今は{(touch.Enabled ? "有効" : "無効")}。t で切り替え)");
        Console.WriteLine(source is null ? "映す画面: 未選択(つないだ端末には Notice(paused) を送って待つ)" : $"映す画面: {source.DeviceName}");

        using var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Cancel();
        };

        MirrorStreamer? streamer = null;
        LinkSession? current = null;
        var sessionStarted = 0L;
        if (touch is not null)
        {
            touch.AutoDisabled += () =>
            {
                if (current is { } live)
                {
                    _ = SendTouchNoticeAsync(live, false);
                }
            };
        }

        async Task OnSession(LinkSession session, CancellationToken ct)
        {
            if (source is null)
            {
                await SendPausedAsync(session, ct).ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return;
            }

            var s = new MirrorStreamer(session.SendAsync, session.Hello.Decoder,
                () => WgcCapture.Open(source.DeviceName, clock, log.Write),
                es => new AutoH264Encoder(es, allowHardware: !o.Software, log.Write),
                source.IsVirtual, streamOptions, clock, log.Write, () => session.Latency.OffsetUs);
            (streamer, current) = (s, session);
            Interlocked.Exchange(ref sessionStarted, Environment.TickCount64);
            touch?.Begin(new ScreenRect(source.X, source.Y, source.Width, source.Height));
            var pump = PumpAsync(session, s, touch, ct);
            try
            {
                await s.RunAsync(ct).ConfigureAwait(false);
            }
            catch (StreamStoppedException ex) when (ex.Reason == "decoder")
            {
                log.Write("端末がどの大きさも復号できないので閉じる");
                await session.CloseAsync("decoder").ConfigureAwait(false);
            }
            catch (StreamStoppedException ex)
            {
                log.Write($"画面を取り込めない: {ex.InnerException?.Message ?? ex.Message}");
                await SendPausedAsync(session, ct).ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            finally
            {
                (streamer, current) = (null, null);
                touch?.End(); // 押したままの接点を取り消す(INV-7)
                await pump.ConfigureAwait(false);
            }
        }

        var server = new LinkServer(new LinkOptions { Port = settings.Port, LanEnabled = o.Lan, AppVersion = typeof(ServeCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", TouchEnabled = () => touch?.Enabled ?? false },
            identity, registry, Save, new AuthThrottle(clock), clock, log.Write, OnSession);
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"ポート {settings.Port} を使えません({ex.SocketErrorCode})。設定でポートを変えてください");
            return 1;
        }

        // 標準入力: ペアリングの番号を待っている間はその 1 行、そうでなければ q で終わる
        var pendingCode = (TaskCompletionSource<string>?)null;
        var stdin = Task.Run(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                if (Volatile.Read(ref pendingCode) is { } p)
                {
                    p.TrySetResult(line.Trim());
                }
                else if (line.Trim() == "q")
                {
                    quit.Cancel();
                    return;
                }
                else if (line.Trim() == "t" && touch is not null)
                {
                    touch.SetEnabled(!touch.Enabled);
                    Console.WriteLine($"タッチを{(touch.Enabled ? "有効" : "無効")}にしました");
                    if (current is { } live)
                    {
                        _ = SendTouchNoticeAsync(live, touch.Enabled);
                    }
                }
            }
        });

        IDisposable? pairing = null;
        if (o.Pair)
        {
            pairing = server.OpenPairing(async (prompt, ct) =>
            {
                Console.WriteLine($"PAIRING CODE {prompt.Code}");
                Console.WriteLine($"{prompt.DeviceName} からの要求です。端末に出ている番号と同じか確かめます");
                string typed;
                if (o.ApproveCode is { } approve)
                {
                    typed = approve;
                }
                else
                {
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref pendingCode, tcs);
                    Console.WriteLine("端末に出ている 6 桁を入力してください(120 秒で拒否)");
                    try
                    {
                        typed = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref pendingCode, null);
                    }
                }

                var ok = typed == prompt.Code; // 番号を比べずに許可する口は作らない(INV-6・D-9)
                Console.WriteLine(ok ? "番号が合ったので許可します" : "番号が違うので拒否します(denied)");
                return ok;
            });
            Console.WriteLine("ペアリングの受け付けを 120 秒開きました。端末の TabSpan で「PC を追加」を押してください");
        }

        var runner = new ProcessAdbRunner();
        AdbWatcher? watcher = null;
        Task adbLoop = Task.CompletedTask;
        if (!o.NoAdb && settings.UsbEnabled)
        {
            var adb = await AdbLocator.FindAsync(settings.AdbPath, Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), runner, quit.Token).ConfigureAwait(false);
            if (adb is null)
            {
                Console.WriteLine("adb が見つかりません。USB でつなぐには adb が要ります(--no-adb で、reverse を手で張って続けられます)");
            }
            else
            {
                Console.WriteLine($"adb: {adb}");
                watcher = new AdbWatcher(runner, adb, server.Port, () => settings.AutoLaunchAndroidApp && registry.Entries.Count > 0, log.Write);
                adbLoop = watcher.RunAsync(TimeSpan.FromSeconds(settings.AdbPollSec), quit.Token);
            }
        }

        Console.WriteLine($"{(o.Lan ? "0.0.0.0" : "127.0.0.1")}:{server.Port} で待ち受けています{(o.Lan ? "(UDP の探索にも返事をします)" : "")}。Ctrl+C か q で終わります");
        try
        {
            while (!quit.IsCancellationRequested)
            {
                await Task.Delay(1000, quit.Token).ConfigureAwait(false);
                if (streamer is { } s && current is { } session)
                {
                    var t = s.TakeSecond();
                    var elapsed = (Environment.TickCount64 - Interlocked.Read(ref sessionStarted)) / 1000;
                    var rtt = session.Latency.RttUs is { } r ? $"{r / 1000.0:F1} ms" : "—";
                    var total = t.Total.Count > 0 ? $" 合計 p50 {t.Total.P50Us / 1000.0:F1} ms p95 {t.Total.P95Us / 1000.0:F1} ms" : "";
                    var touchLatency = touch?.TakeLatency() is { Count: > 0 } tl ? $" タッチ {tl.Count} 回 p95 {tl.P95Us / 1000.0:F1} ms" : "";
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[{elapsed}s] 送信 {t.Frames} 枚(キー {t.KeyFrames}) {t.Bytes / 1024} kB/s 往復 {rtt} 端末 復号 {t.DeviceDecoded} 捨て {t.DeviceDropped} PC 捨て累計 {t.BacklogDropped} 取込→送信 p50 {t.CaptureToSend.P50Us / 1000.0:F1} ms{total} {t.Width}x{t.Height}@{t.Fps} {t.Encoder}({(t.HardwareEncoder ? "ハード" : "ソフト")}){touchLatency}"));
                    if (o.Seconds > 0 && elapsed >= o.Seconds)
                    {
                        Console.WriteLine($"{o.Seconds} 秒流したので終わります");
                        break;
                    }
                }
                else if (watcher is { Unauthorized.Count: > 0 })
                {
                    Console.WriteLine("端末の画面で「USB デバッグを許可しますか」に「許可」を押してください");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C か q
        }

        await quit.CancelAsync().ConfigureAwait(false);
        pairing?.Dispose();
        await server.DisposeAsync().ConfigureAwait(false); // Bye(shutdown)
        await adbLoop.ConfigureAwait(false);
        if (watcher is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await watcher.RemoveAllAsync(cleanup.Token).ConfigureAwait(false);
        }

        // 標準入力の読み(stdin)は待たない。プロセスの終わりで止まる
        Console.WriteLine("終わりました");
        return 0;
    }

    private static async Task PumpAsync(LinkSession session, MirrorStreamer streamer, TouchThread? touch, CancellationToken ct)
    {
        try
        {
            await foreach (var f in session.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                streamer.OnFrame(f);
                touch?.OnFrame(f);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task SendTouchNoticeAsync(LinkSession session, bool enabled)
    {
        try
        {
            await session.SendAsync(MessageType.Notice, Messages.ToJson(new NoticeMsg("touchEnabled", JsonSerializer.SerializeToElement(enabled))), session.Closing).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 切れた
        }
    }

    private static async Task SendPausedAsync(LinkSession session, CancellationToken ct) =>
        await session.SendAsync(MessageType.Notice, Messages.ToJson(new NoticeMsg("paused", JsonSerializer.SerializeToElement(true))), ct).ConfigureAwait(false);
}
