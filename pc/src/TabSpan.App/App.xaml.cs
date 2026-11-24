// アプリの入口: 部品(AppHost)を作ってメインの画面を出し、終わる時に Bye(shutdown) と reverse --remove を済ませる。
// トレイ・多重起動の防止・自動起動は Phase 5(今は入れない)。
using System.Windows;

namespace TabSpan.App;

public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = new AppHost();
        MainWindow = new MainWindow(_host);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 端末へ Bye を送り、reverse を外すまで待つ(長くても数秒)
        _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(20));
        base.OnExit(e);
    }
}
