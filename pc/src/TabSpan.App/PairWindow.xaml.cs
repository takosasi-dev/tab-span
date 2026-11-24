// 「端末を追加」(画面設計書 P3、仮): 受け付けを 120 秒開き、要求が来たら端末の名前と 6 桁を出して、許可/拒否を待つ。
// 「許可」は番号が出てから 1 秒の間は押せない。閉じたら受け付けを止める(処理中の要求は拒否になる)。
using System.Windows;
using System.Windows.Threading;
using TabSpan.Link;

namespace TabSpan.App;

public partial class PairWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IDisposable? _pairing;
    private DateTime _endsAt;
    private TaskCompletionSource<bool>? _decision;

    public PairWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Open();
    }

    private void Open()
    {
        _pairing?.Dispose();
        _pairing = _host.OpenPairing(DecideAsync);
        _endsAt = DateTime.UtcNow + LinkServer.PairingWindowLength; // 残りの表示だけに使う(判定は LinkServer がする)
        ShowWaiting();
        _timer.Start();
    }

    private void ShowWaiting()
    {
        CodeText.Visibility = DenyButton.Visibility = ApproveButton.Visibility = RetryButton.Visibility = Visibility.Collapsed;
        Tick();
    }

    private void Tick()
    {
        if (_decision is not null)
        {
            return;
        }

        var left = _endsAt - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _timer.Stop();
            MessageText.Text = Strings.Get("Pair_TimedOut");
            RetryButton.Visibility = Visibility.Visible;
            return;
        }

        MessageText.Text = Strings.Format("Pair_Waiting", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    /// <summary>LinkServer から呼ばれる(UI 以外のスレッド)。画面で許可・拒否を押すか、取り消されるまで待つ。</summary>
    private async Task<bool> DecideAsync(PairingPrompt prompt, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() => ShowRequest(prompt, decision));
        bool approved;
        using (ct.Register(() => decision.TrySetResult(false)))
        {
            approved = await decision.Task.ConfigureAwait(false);
        }

        await Dispatcher.InvokeAsync(() => ShowResult(prompt, approved && !ct.IsCancellationRequested));
        return approved;
    }

    private void ShowRequest(PairingPrompt prompt, TaskCompletionSource<bool> decision)
    {
        _decision = decision;
        MessageText.Text = Strings.Format("Pair_Request", prompt.DeviceName);
        CodeText.Text = $"{prompt.Code[..3]} {prompt.Code[3..]}";
        CodeText.Visibility = DenyButton.Visibility = ApproveButton.Visibility = Visibility.Visible;
        ApproveButton.IsEnabled = false;
        var unlock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; // 番号を見ずに押すのを防ぐ
        unlock.Tick += (_, _) =>
        {
            unlock.Stop();
            ApproveButton.IsEnabled = true;
        };
        unlock.Start();
    }

    private void ShowResult(PairingPrompt prompt, bool approved)
    {
        _decision = null;
        CodeText.Visibility = DenyButton.Visibility = ApproveButton.Visibility = Visibility.Collapsed;
        _timer.Stop();
        if (approved)
        {
            MessageText.Text = Strings.Format("Pair_Added", prompt.DeviceName);
            var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            close.Tick += (_, _) =>
            {
                close.Stop();
                Close();
            };
            close.Start();
        }
        else
        {
            MessageText.Text = Strings.Get("Pair_Denied");
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void OnApprove(object sender, RoutedEventArgs e) => _decision?.TrySetResult(true);

    private void OnDeny(object sender, RoutedEventArgs e) => _decision?.TrySetResult(false);

    private void OnRetry(object sender, RoutedEventArgs e) => Open();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _decision?.TrySetResult(false);
        _pairing?.Dispose(); // 受け付けを止める
        base.OnClosed(e);
    }
}
