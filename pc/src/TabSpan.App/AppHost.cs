// アプリの部品の配線: 設定・ログ・identity・台帳を読み、待ち受け(Link)と adb の見回りを始め、つながった端末へ選んだモニターを映す(Platform)。
// 画面からは状態を読み、映す画面の選択・端末の追加・切断・タッチの切り替えを頼む。Changed は UI 以外のスレッドから来る。
// タッチ(§9.8)は注入のスレッド 1 本(TouchThread)に集め、端末からの枠を全部そこへも回す。
using System.Net.Sockets;
using System.Text.Json;
using TabSpan.Core;
using TabSpan.Link;
using TabSpan.Platform;

namespace TabSpan.App;

public sealed class AppHost : IAsyncDisposable
{
    private readonly IClock _clock = SystemClock.Instance;
    private readonly DataPaths _paths = DataPaths.Resolve();
    private readonly SettingsStore _settingsStore;
    private readonly Identity _identity;
    private readonly DeviceRegistry _registry;
    private readonly Lock _saveLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly LinkServer _server;
    private readonly TouchThread _touch;
    private Task _adbLoop = Task.CompletedTask;
    private CancellationTokenSource? _restart;

    public AppHost()
    {
        Log = new FileLog(_paths.LogDir, _clock);
        Log.DeleteOld();
        _settingsStore = new SettingsStore(_paths.Settings, _clock);
        (Settings, var brokenSettings) = _settingsStore.Load();
        if (brokenSettings is not null)
        {
            Log.Write($"設定を読めないので既定値で始める(元のファイルは {System.IO.Path.GetFileName(brokenSettings)})");
        }

        (_identity, var created, var brokenIdentity) = Identity.LoadOrCreate(_paths.Identity, _clock);
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
        _touch.AutoDisabled += () => SetTouchEnabled(false);
        _server = new LinkServer(new LinkOptions { Port = Settings.Port, AppVersion = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", TouchEnabled = () => _touch.Enabled },
            _identity, _registry, SaveDevices, new AuthThrottle(_clock), _clock, Log.Write, OnSessionAsync);
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

        if (Settings.UsbEnabled)
        {
            _adbLoop = StartAdbAsync();
        }
    }

    public FileLog Log { get; }

    public Settings Settings { get; private set; }

    public bool PortUnavailable { get; }

    public bool PairingDataLost { get; }

    public bool AdbMissing { get; private set; }

    public AdbWatcher? Watcher { get; private set; }

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; } = [];

    public MonitorInfo? Source { get; private set; }

    public LinkSession? Session => _server.Active;

    public MirrorStreamer? Streamer { get; private set; }

    public int RegisteredDevices => _registry.Entries.Count;

    /// <summary>端末のタッチを注入するか(FR-22)。</summary>
    public bool TouchEnabled => _touch.Enabled;

    /// <summary>注入の準備ができた(§9.8。偽なら画面にその旨を出す)。</summary>
    public bool TouchAvailable => _touch.Available;

    /// <summary>状態が変わった(UI 以外のスレッドから来る)。</summary>
    public event Action? Changed;

    public void RefreshMonitors()
    {
        Monitors = Platform.Monitors.List(Settings.VirtualMonitorMatch);
        Source = Platform.Monitors.PickSource(Monitors, Settings.SourceMonitor);
    }

    /// <summary>映す画面を選んで保存する。映していれば切り替える。</summary>
    public void SelectSource(string deviceName)
    {
        Settings = Settings with { SourceMonitor = deviceName };
        _settingsStore.Save(Settings);
        RefreshMonitors();
        try
        {
            _restart?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ちょうど切り替わったところ。次の回は新しい選択で始まる
        }

        Changed?.Invoke();
    }

    /// <summary>タッチを切り替えて保存し、つないでいる端末へ Notice(touchEnabled) を送る(FR-22・FR-23)。</summary>
    public void SetTouchEnabled(bool enabled)
    {
        Settings = Settings with { TouchEnabled = enabled };
        _settingsStore.Save(Settings);
        _touch.SetEnabled(enabled);
        Log.Write($"タッチを{(enabled ? "有効" : "無効")}にした");
        if (Session is { } session)
        {
            _ = SendTouchNoticeAsync(session, _touch.Enabled);
        }

        Changed?.Invoke();
    }

    public IDisposable OpenPairing(Func<PairingPrompt, CancellationToken, Task<bool>> decide) => _server.OpenPairing(decide);

    public Task DisconnectAsync() => _server.DisconnectAsync();

    private async Task StartAdbAsync()
    {
        var runner = new ProcessAdbRunner();
        var adb = await AdbLocator.FindAsync(Settings.AdbPath, Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), runner, _stop.Token).ConfigureAwait(false);
        if (adb is null)
        {
            AdbMissing = true;
            Log.Write("adb が見つからない");
            Changed?.Invoke();
            return;
        }

        Watcher = new AdbWatcher(runner, adb, _server.Port, () => Settings.AutoLaunchAndroidApp && _registry.Entries.Count > 0, Log.Write);
        Watcher.Changed += () => Changed?.Invoke();
        await Watcher.RunAsync(TimeSpan.FromSeconds(Settings.AdbPollSec), _stop.Token).ConfigureAwait(false);
    }

    private async Task OnSessionAsync(LinkSession session, CancellationToken ct)
    {
        _touch.Begin(RectOf(Source));
        try
        {
            await StreamAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _touch.End(); // 押したままの接点を取り消す(INV-7)
        }
    }

    private static ScreenRect? RectOf(MonitorInfo? m) => m is null ? null : new ScreenRect(m.X, m.Y, m.Width, m.Height);

    private async Task StreamAsync(LinkSession session, CancellationToken ct)
    {
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

            var settings = Settings;
            var streamer = new MirrorStreamer(session.SendAsync, session.Hello.Decoder,
                () => WgcCapture.Open(source.DeviceName, _clock, Log.Write),
                es => new AutoH264Encoder(es, allowHardware: true, Log.Write),
                source.IsVirtual, new StreamOptions(settings.FpsCap, settings.BitrateMbps, settings.MaxLongEdge, settings.BacklogLimitFrames),
                _clock, Log.Write, () => session.Latency.OffsetUs);
            Streamer = streamer;
            Changed?.Invoke();
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
                Log.Write($"画面を取り込めない: {ex.InnerException?.Message ?? ex.Message}");
                await SendPausedAsync(session, ct).ConfigureAwait(false);
                await WaitAsync(restart.Token).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is System.IO.IOException or OperationCanceledException or ObjectDisposedException)
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
        await _stop.CancelAsync().ConfigureAwait(false);
        await _server.DisposeAsync().ConfigureAwait(false); // Bye(shutdown)
        await _adbLoop.ConfigureAwait(false);
        _touch.Dispose();
        if (Watcher is { } w)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await w.RemoveAllAsync(cleanup.Token).ConfigureAwait(false);
        }

        _identity.Dispose();
        _stop.Dispose();
    }
}
