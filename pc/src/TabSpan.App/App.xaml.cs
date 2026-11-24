// アプリの入口: 多重起動を防ぎ(2 つ目は先の方の窓を前に出して終わる)、部品(AppHost)・トレイ(P1)・メインの画面(P2)を作る。
// 起動の引数に --tray(自動起動)があればトレイだけで始める。× はトレイにしまい、トレイの「終了」で本当に終わる。
// 終わる時に Bye(shutdown)・接点の取り消し・reverse --remove を済ませる(AppHost.DisposeAsync)。1 秒ごとに AppHost.Sample を呼ぶ。
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TabSpan.App;

public partial class App : Application
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SingleInstance? _single;
    private AppHost? _host;
    private Tray? _tray;
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Assembly.GetEntryAssembly() != typeof(App).Assembly)
        {
            // 確かめの exe(Smoke)が new App() した後に Dispatcher を回すと、ここが呼ばれる。本物の部品・トレイ・窓を作らない
            return;
        }

        _single = SingleInstance.TryAcquire(SingleInstance.DefaultName, () => Dispatcher.BeginInvoke(ShowMain));
        if (_single is null)
        {
            Shutdown(); // 先に動いている方へ合図を送った
            return;
        }

        var host = _host = new AppHost();
        Strings.UseLanguage(host.Settings.Language); // 窓を作る前に 1 回だけ(次の起動から変わる)
        _main = new MainWindow(host);
        MainWindow = _main;
        var tray = _tray = new Tray(ShowMain, host.SetTouchEnabled, () => _ = host.DisconnectAsync(), ExitFromTray);
        host.Changed += () => Dispatcher.BeginInvoke(() => tray.Refresh(host));
        host.Notify += (title, body) => Dispatcher.BeginInvoke(() => tray.Notify(title, body));
        tray.Refresh(host);
        tray.Show();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _timer.Tick += (_, _) => host.Sample();
        _timer.Start();
        if (!e.Args.Contains(StartupRegistration.TrayArgument))
        {
            _main.ShowAndActivate();
        }
    }

    private void ShowMain() => _main?.ShowAndActivate();

    /// <summary>モニターの抜き差し・配置の変更で一覧を取り直す(P2)。</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _host?.RefreshMonitors();
        _main?.Refresh();
    });

    private void ExitFromTray()
    {
        if (_main is not null)
        {
            _main.AllowClose = true;
            _main.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _tray?.Dispose();
        // 端末へ Bye を送り、reverse を外すまで待つ(長くても数秒)
        _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(20));
        _single?.Dispose();
        base.OnExit(e);
    }
}
