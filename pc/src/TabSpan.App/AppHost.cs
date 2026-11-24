// アプリの部品の配線: 設定・ログ・identity・台帳を読み、待ち受け(Link)と adb の見回りを始め、つながった端末へ選んだモニターを映す(Platform)。
// 画面からは状態を読み、設定の反映・映す画面の選択・端末の追加と解除・切断・タッチの切り替えを頼む。Changed と Notify は UI 以外のスレッドからも来る。
// タッチ(§9.8)は注入のスレッド 1 本(TouchThread)に集める。1 秒ごとの数字(StreamSecond)は Sample() だけが取り、画面はそれを読む。
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using TabSpan.Core;
using TabSpan.Link;
using TabSpan.Platform;

namespace TabSpan.App;

public enum HostState
{
    Waiting,
    Connected,
    Streaming,
    Paused,
    Problem,
}

public sealed class AppHost : IAsyncDisposable
{
    public const int HistorySeconds = 60;
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromSeconds(60);

    private readonly IClock _clock;
    private readonly DataPaths _paths = DataPaths.Resolve();
    private readonly SettingsStore _settingsStore;
    private readonly DeviceRegistry _registry;
    private readonly Lock _saveLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkServer _server;
    private readonly TouchThread _touch;
    private readonly VirtualDisplayKeeper _keeper;
    private readonly IRunKey _runKey;
    private readonly IAdbRunner _adbRunner;
    private readonly Func<string, Action, IDisposable?> _registerHotKey;
    private readonly Dictionary<string, DateTimeOffset> _lastNotified = [];
    private readonly Queue<StreamSecond> _history = new();
    private readonly SemaphoreSlim _adbGate = new(1, 1); // adb の見回りの作り直しを 1 本ずつにする(2 本走らせない)
    private IDisposable? _hotKey;
    private CancellationTokenSource _adbStop = new();
    private Task _adbLoop = Task.CompletedTask;
    private CancellationTokenSource? _restart;

    /// <param name="runKey">自動起動の書き込み先(既定は HKCU の Run キー)。</param>
    /// <param name="adbRunner">adb を呼ぶ口(既定は本物のプロセス)。</param>
    /// <param name="registerHotKey">ホットキーの登録(既定は RegisterHotKey。登録できなければ null を返す)。UI のスレッドで呼ぶ。</param>
    /// <param name="clock">時計(既定は本物。通知の間引きの確かめに偽物を渡す)。</param>
    public AppHost(IRunKey? runKey = null, IAdbRunner? adbRunner = null, Func<string, Action, IDisposable?>? registerHotKey = null, IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _runKey = runKey ?? new RegistryRunKey();
        _adbRunner = adbRunner ?? new ProcessAdbRunner();
        _registerHotKey = registerHotKey ?? HotKey.TryRegister;
        Log = new FileLog(_paths.LogDir, _clock);
        Log.DeleteOld();
        _settingsStore = new SettingsStore(_paths.Settings, _clock);
        (Settings, var brokenSettings) = _settingsStore.Load();
        if (brokenSettings is not null)
        {
            Log.Write($"設定を読めないので既定値で始める(元のファイルは {Path.GetFileName(brokenSettings)})");
        }

        var (identity, created, brokenIdentity) = Identity.LoadOrCreate(_paths.Identity, _clock); // 鍵は LinkServer が持ち、作り直しも LinkServer がする
        var (entries, brokenDevices) = DeviceStore.Load(_paths.Devices, _clock);
        PairingDataLost = brokenIdentity is not null || brokenDevices is not null;
        _registry = new DeviceRegistry(created ? [] : entries);
        if (created)
        {
            SaveDevices(); // 鍵を作り直したので台帳を空にする(§10)
        }

        RefreshMonitors();
        _touch = new TouchThread(() =>
        {
            var injector = Win32TouchInjector.Create(out var error);
            if (injector is null)
            {
                Log.Write($"タッチの注入を準備できない(InitializeTouchInjection のエラー {error})。タッチを使わない");
            }

            return injector;
        }, Settings.TouchEnabled, Settings.TouchRefreshMs, _clock, Log.Write);
        _touch.AutoDisabled += () =>
        {
            SetTouchEnabled(false);
            RaiseNotify("touchFailed", Strings.Get("Notify_TouchFailed"));
        };
        _server = new LinkServer(new LinkOptions { Port = Settings.Port, LanEnabled = Settings.LanEnabled, AppVersion = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", TouchEnabled = () => _touch.Enabled },
            identity, _registry, SaveDevices, new AuthThrottle(_clock), _clock, Log.Write, OnSessionAsync);
        _server.Changed += () => Changed?.Invoke();
        try
        {
            _server.Start();
        }
        catch (SocketException ex)
        {
            PortUnavailable = true;
            Log.Write($"ポート {Settings.Port} を使えない: {ex.SocketErrorCode}");
        }

        // 仮想ディスプレイ(FR-10・FR-19・D-18)。外せなかったとき(R-5)はログだけ(keeper に知らせの口が無い)
        _keeper = new VirtualDisplayKeeper(new Win32DisplayControl(), IsVirtual, _paths.State, _clock,
            () => Settings.DetachOnDisconnect, () => Settings.DetachDelaySec, Log.Write);
        _keeper.Detached += _ =>
        {
            RaiseNotify("detached", Strings.Get("Notify_Detached"));
            RefreshMonitors();
        };
        _keeper.CleanupFromState();
        if (Settings.UsbEnabled)
        {
            _adbLoop = StartAdbAsync(_adbStop.Token);
        }

        RegisterHotKey();
    }

    public FileLog Log { get; }

    public Settings Settings { get; private set; }

    public bool PortUnavailable { get; private set; }

    public bool PairingDataLost { get; }

    public bool AdbMissing { get; private set; }

    /// <summary>見つけて version が通った adb の場所(P4・P6)。まだ探していない・見つからなければ null。</summary>
    public string? AdbFound { get; private set; }

    public AdbWatcher? Watcher { get; private set; }

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];

    public MonitorInfo? Source { get; private set; }

    public LinkSession? Session => _server.Active;

    public MirrorStreamer? Streamer { get; private set; }

    public int RegisteredDevices => _registry.Entries.Count;

    /// <summary>設定の画面の端末の一覧(P4)。</summary>
    public IReadOnlyList<DeviceEntry> Devices => _registry.Entries;

    /// <summary>端末のタッチを注入するか(FR-22)。</summary>
    public bool TouchEnabled => _touch.Enabled;

    /// <summary>注入の準備ができた(§9.8。偽なら画面にその旨を出す)。</summary>
    public bool TouchAvailable => _touch.Available;

    /// <summary>touchHotkey を登録できなかった(FR-23。画面にその旨を出す)。</summary>
    public bool HotKeyFailed { get; private set; }

    /// <summary>画面を取り込めずに止まっている理由(P2 の帯)。止まっていなければ null。</summary>
    public string? CaptureError { get; private set; }

    /// <summary>直近の 1 秒の数字。映していなければ null。</summary>
    public StreamSecond? LastSecond { get; private set; }

    /// <summary>直近 60 秒(古い順。P5 のグラフ)。</summary>
    public IReadOnlyList<StreamSecond> History => [.. _history];

    /// <summary>直近の 1 秒のタッチの注入の時間(§9.9)。</summary>
    public LatencySummary? TouchLatency { get; private set; }

    /// <summary>状態が変わった(UI 以外のスレッドからも来る)。</summary>
    public event Action? Changed;

    /// <summary>(題, 本文)。同じ種類は 60 秒に 1 回まで(画面 §5)。UI 以外のスレッドからも来る。</summary>
    public event Action<string, string>? Notify;

    /// <summary>P2 の「状態」と P1 のアイコン。一時停止中は、つないでいるが映す画面が無い・取り込めないとき。</summary>
    public HostState State =>
        PortUnavailable ? HostState.Problem
        : Session is null ? HostState.Waiting
        : Streamer is not null ? HostState.Streaming
        : Source is null || CaptureError is not null ? HostState.Paused
        : HostState.Connected;

    private bool IsVirtual(string deviceName) =>
        Monitors.Any(m => m.IsVirtual && string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    public void RefreshMonitors()
    {
        Monitors = Platform.Monitors.List(Settings.VirtualMonitorMatch);
        Source = Platform.Monitors.PickSource(Monitors, Settings.SourceMonitor);
    }

    /// <summary>1 秒ごとに UI のスレッドから呼ぶ。映していれば数字を取り、履歴に足す。TakeSecond は読むと消えるので、ここ以外で呼ばない。</summary>
    public void Sample()
    {
        LastSecond = Streamer?.TakeSecond();
        TouchLatency = _touch.TakeLatency();
        if (LastSecond is { } s)
        {
            _history.Enqueue(s);
            while (_history.Count > HistorySeconds)
            {
                _history.Dequeue();
            }
        }
        else
        {
            _history.Clear();
        }

        _keeper.Tick();
        Changed?.Invoke();
    }

    /// <summary>映す画面を選んで保存する。映していれば切り替える。</summary>
    public void SelectSource(string deviceName) => ApplySettings(Settings with { SourceMonitor = deviceName });

    /// <summary>取り込みのエラーで止まっているのを、もう一度試す(P2 の帯)。</summary>
    public void RetryCapture()
    {
        CaptureError = null;
        Restart();
        Changed?.Invoke();
    }

    private void Restart()
    {
        try
        {
            _restart?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ちょうど切り替わったところ。次の回は新しい選択で始まる
        }
    }

    /// <summary>タッチを切り替えて保存し、つないでいる端末へ Notice(touchEnabled) を送る(FR-22・FR-23)。</summary>
    public void SetTouchEnabled(bool enabled) => ApplySettings(Settings with { TouchEnabled = enabled });

    /// <summary>ホットキーで切り替える。止めたら通知を出す(画面 §5)。</summary>
    public void ToggleTouchByHotKey()
    {
        SetTouchEnabled(!Settings.TouchEnabled);
        if (!Settings.TouchEnabled)
        {
            RaiseNotify("touchOff", Strings.Format("Notify_TouchOff", Settings.TouchHotkey));
        }
    }

    /// <summary>設定を保存し、効く物に反映する(P4。値は変えた時点で呼ぶ)。</summary>
    public void ApplySettings(Settings next)
    {
        next = next.Normalize();
        var prev = Settings;
        Settings = next;
        _settingsStore.Save(next);

        if (prev.TouchEnabled != next.TouchEnabled)
        {
            _touch.SetEnabled(next.TouchEnabled);
            Log.Write($"タッチを{(next.TouchEnabled ? "有効" : "無効")}にした");
            if (Session is { } session)
            {
                _ = SendTouchNoticeAsync(session, _touch.Enabled);
            }
        }

        if (prev.TouchHotkey != next.TouchHotkey)
        {
            RegisterHotKey();
        }

        if ((prev.FpsCap, prev.BitrateMbps, prev.MaxLongEdge, prev.BacklogLimitFrames) != (next.FpsCap, next.BitrateMbps, next.MaxLongEdge, next.BacklogLimitFrames))
        {
            Streamer?.Update(OptionsOf(next)); // FR-15: エンコーダーを作り直し、キーフレームから
        }

        if (prev.StartWithWindows != next.StartWithWindows)
        {
            ApplyRunKey(next.StartWithWindows);
        }

        if (!prev.VirtualMonitorMatch.SequenceEqual(next.VirtualMonitorMatch) || prev.SourceMonitor != next.SourceMonitor)
        {
            RefreshMonitors();
            Restart();
        }

        if ((prev.UsbEnabled, prev.AdbPath, prev.AdbPollSec) != (next.UsbEnabled, next.AdbPath, next.AdbPollSec))
        {
            _ = RestartAdbAsync();
        }

        if ((prev.Port, prev.LanEnabled) != (next.Port, next.LanEnabled))
        {
            _ = ReconfigureAsync(next, prev.Port != next.Port);
        }

        // language は次の起動から(Strings.UseLanguage は App の起動で 1 回だけ)。touchRefreshMs も次の起動から。
        // detachOnDisconnect・detachDelaySec・autoLaunchAndroidApp は読む側が毎回 Settings を見るので、ここで何もしない。
        Changed?.Invoke();
    }

    /// <summary>待ち受けを作り直す(FR-2)。ポートを変えたら adb の見回り(reverse の port)も作り直す。</summary>
    private async Task ReconfigureAsync(Settings next, bool portChanged)
    {
        try
        {
            await _server.ReconfigureAsync(next.Port, next.LanEnabled).ConfigureAwait(false);
            PortUnavailable = false;
            Log.Write($"待ち受けを作り直した(ポート {next.Port}、LAN {(next.LanEnabled ? "オン" : "オフ")})");
        }
        catch (SocketException ex)
        {
            PortUnavailable = true;
            Log.Write($"ポート {next.Port} を使えない: {ex.SocketErrorCode}");
        }

        if (portChanged)
        {
            await RestartAdbAsync().ConfigureAwait(false);
        }

        Changed?.Invoke();
    }

    /// <summary>P4 の項目を既定値に戻す(ペアリングは残す)。画面に出していない sourceMonitor と virtualMonitorMatch は残す。</summary>
    public void ResetSettings() =>
        ApplySettings(new Settings { SourceMonitor = Settings.SourceMonitor, VirtualMonitorMatch = Settings.VirtualMonitorMatch });

    /// <summary>端末を解除する(FR-4)。</summary>
    public async Task UnpairAsync(string deviceId)
    {
        await _server.UnpairAsync(deviceId).ConfigureAwait(false); // 台帳から消して保存し、つないでいれば Bye(unpaired)
        Log.Write("端末を 1 台解除した");

        Changed?.Invoke();
    }

    /// <summary>この PC の鍵を作り直す(§10)。すべての端末のペアリングが外れる。</summary>
    public async Task ResetIdentityAsync()
    {
        await _server.ResetIdentityAsync().ConfigureAwait(false); // 鍵を作り直して台帳を空にし、Bye(unpaired) で閉じ、新しい鍵で待ち直す
        Log.Write("この PC の鍵を作り直した");
        Changed?.Invoke();
    }

    public IDisposable OpenPairing(Func<PairingPrompt, CancellationToken, Task<bool>> decide) => _server.OpenPairing(decide);

    public Task DisconnectAsync() => _server.DisconnectAsync();

    /// <summary>同じ種類の通知を 60 秒に 1 回までにして Notify を出す。</summary>
    internal void RaiseNotify(string kind, string body)
    {
        var now = _clock.UtcNow;
        lock (_lastNotified)
        {
            if (_lastNotified.TryGetValue(kind, out var last) && now - last < NotifyInterval)
            {
                return;
            }

            _lastNotified[kind] = now;
        }

        Notify?.Invoke(Strings.Get("Notify_Title"), body);
    }

    private void RegisterHotKey()
    {
        _hotKey?.Dispose();
        _hotKey = _registerHotKey(Settings.TouchHotkey, ToggleTouchByHotKey);
        HotKeyFailed = _hotKey is null;
        if (HotKeyFailed)
        {
            Log.Write($"ホットキー {Settings.TouchHotkey} を登録できない");
        }
    }

    private void ApplyRunKey(bool enabled)
    {
        try
        {
            StartupRegistration.Apply(_runKey, enabled, Environment.ProcessPath ?? "");
            Log.Write($"Windows の起動時に開始する: {(enabled ? "オン" : "オフ")}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write($"自動起動の設定を書けない: {ex.Message}");
        }
    }

    private async Task RestartAdbAsync()
    {
        await _adbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RestartAdbCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _adbGate.Release();
        }
    }

    private async Task RestartAdbCoreAsync()
    {
        await _adbStop.CancelAsync().ConfigureAwait(false);
        await _adbLoop.ConfigureAwait(false);
        if (Watcher is { } w)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await w.RemoveAllAsync(cleanup.Token).ConfigureAwait(false);
        }

        Watcher = null;
        AdbMissing = false;
        AdbFound = null;
        _adbStop.Dispose();
        _adbStop = new CancellationTokenSource();
        if (Settings.UsbEnabled && !_stop.IsCancellationRequested)
        {
            _adbLoop = StartAdbAsync(_adbStop.Token);
        }

        Changed?.Invoke();
    }

    private async Task StartAdbAsync(CancellationToken ct)
    {
        try
        {
            var adb = await AdbLocator.FindAsync(Settings.AdbPath, Environment.GetEnvironmentVariable("PATH"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _adbRunner, ct).ConfigureAwait(false);
            if (adb is null)
            {
                AdbMissing = true;
                Log.Write("adb が見つからない");
                Changed?.Invoke();
                return;
            }

            AdbFound = adb;
            Watcher = new AdbWatcher(_adbRunner, adb, Settings.Port, () => Settings.AutoLaunchAndroidApp && _registry.Entries.Count > 0, Log.Write);
            Watcher.Changed += () => Changed?.Invoke();
            Changed?.Invoke();
            await Watcher.RunAsync(TimeSpan.FromSeconds(Settings.AdbPollSec), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 止めた(USB を切った・adb の設定を変えた・終了)
        }
    }

    private static StreamOptions OptionsOf(Settings s) => new(s.FpsCap, s.BitrateMbps, s.MaxLongEdge, s.BacklogLimitFrames);

    private async Task OnSessionAsync(LinkSession session, CancellationToken ct)
    {
        _keeper.OnSessionStarted();
        _touch.Begin(RectOf(Source));
        try
        {
            await StreamAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _touch.End(); // 押したままの接点を取り消す(INV-7)
            CaptureError = null;
            _keeper.OnSessionEnded();
        }
    }

    private static ScreenRect? RectOf(MonitorInfo? m) => m is null ? null : new ScreenRect(m.X, m.Y, m.Width, m.Height);

    private async Task StreamAsync(LinkSession session, CancellationToken ct)
    {
        var notified = false;
        while (!ct.IsCancellationRequested)
        {
            using var restart = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _restart = restart;
            var source = Source;
            _touch.SetRect(RectOf(source));
            if (source is null)
            {
                await SendPausedAsync(session, ct).ConfigureAwait(false);
                await WaitAsync(restart.Token).ConfigureAwait(false);
                continue;
            }

            CaptureError = null;
            if (source.IsVirtual && !source.Attached)
            {
                // FR-10: 外れている仮想ディスプレイは付けてから取り込む。付けられなければ取り込みを始めない
                if (!_keeper.EnsureAttached(source.DeviceName))
                {
                    CaptureError = Strings.Get("Error_AttachFailed");
                    Changed?.Invoke();
                    await SendPausedAsync(session, ct).ConfigureAwait(false);
                    await WaitAsync(restart.Token).ConfigureAwait(false);
                    continue;
                }

                RefreshMonitors();
                source = Source ?? source;
            }

            var streamer = new MirrorStreamer(session.SendAsync, session.Hello.Decoder,
                () => WgcCapture.Open(source.DeviceName, _clock, Log.Write),
                es => new AutoH264Encoder(es, allowHardware: true, Log.Write),
                source.IsVirtual, OptionsOf(Settings), _clock, Log.Write, () => session.Latency.OffsetUs);
            Streamer = streamer;
            Changed?.Invoke();
            if (!notified)
            {
                notified = true;
                RaiseNotify("streaming", Strings.Format("Notify_Streaming", session.DeviceName));
            }

            var pump = PumpAsync(session, streamer, _touch, restart.Token);
            try
            {
                await streamer.RunAsync(restart.Token).ConfigureAwait(false);
            }
            catch (StreamStoppedException ex) when (ex.Reason == "decoder")
            {
                Log.Write("端末がどの大きさも復号できないので閉じる");
                await session.CloseAsync("decoder").ConfigureAwait(false);
                return;
            }
            catch (StreamStoppedException ex)
            {
                CaptureError = ex.InnerException?.Message ?? ex.Message;
                Log.Write($"画面を取り込めない: {CaptureError}");
                Streamer = null;
                Changed?.Invoke();
                await SendPausedAsync(session, ct).ConfigureAwait(false);
                await WaitAsync(restart.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or IOException or ObjectDisposedException or SocketException)
                && !session.Closing.IsCancellationRequested)
            {
                // エンコーダーのエラーなど(切断で送れなくなった時は出さない)。セッションは LinkServer が閉じる
                RaiseNotify("streamFailed", Strings.Get("Notify_StreamFailed"));
                throw;
            }
            finally
            {
                Streamer = null;
                _restart = null;
                await pump.ConfigureAwait(false);
                Changed?.Invoke();
            }
        }
    }

    private static async Task WaitAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 映す画面が選び直された、またはセッションが終わった
        }
    }

    private static async Task PumpAsync(LinkSession session, MirrorStreamer streamer, TouchThread touch, CancellationToken ct)
    {
        try
        {
            await foreach (var f in session.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                streamer.OnFrame(f);
                touch.OnFrame(f);
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

    private void SaveDevices()
    {
        lock (_saveLock)
        {
            DeviceStore.Save(_paths.Devices, _registry.Entries);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _hotKey?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _adbGate.WaitAsync().ConfigureAwait(false); // 作り直しの途中なら終わるのを待つ(_stop を見て新しい見回りは始めない)
        await _adbStop.CancelAsync().ConfigureAwait(false);
        _keeper.DetachOwnedNow(); // FR-19: TabSpan が付けた・映していた仮想ディスプレイを外す
        await _server.DisposeAsync().ConfigureAwait(false); // Bye(shutdown)
        await _adbLoop.ConfigureAwait(false);
        _touch.Dispose();
        if (Watcher is { } w)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await w.RemoveAllAsync(cleanup.Token).ConfigureAwait(false);
        }

        _adbStop.Dispose();
        _stop.Dispose();
    }
}
