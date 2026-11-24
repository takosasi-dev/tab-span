// P2 メイン(画面設計書 §3.2): タブ(メイン・設定・診断・はじめに)、状態、つないでいる端末、送っている大きさと fps、映す画面の選択、「端末を追加」、帯と下の帯。
// 部品の状態が変わった時(1 秒ごとの Sample を含む)に描き直す。× と Esc はトレイにしまう(AllowClose が真のときだけ本当に閉じる)。
// キーボード(§6): Ctrl+1〜4 でタブ、F5 でモニターの一覧を取り直す。
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabSpan.Platform;

namespace TabSpan.App;

public partial class MainWindow : Window
{
    public const int TabMain = 0, TabSettings = 1, TabDiagnostics = 2, TabGuide = 3;

    private readonly AppHost _host;
    private IReadOnlyList<MonitorInfo>? _shownMonitors;
    private bool _updatingList;
    private Action? _bannerAction;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        SettingsPage.Attach(host);
        DiagnosticsPage.Attach(host);
        GuidePage.Attach(host, () => OnRefresh(this, new RoutedEventArgs()));
        _host.Changed += OnHostChanged;
        Refresh();
    }

    /// <summary>真なら × で本当に閉じる(トレイの「終了」のとき)。偽ならトレイにしまう。</summary>
    public bool AllowClose { get; set; }

    /// <summary>トレイからやほかの起動の合図で前に出す。</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void SelectTab(int index) => Tabs.SelectedIndex = index;

    private void OnHostChanged() => Dispatcher.BeginInvoke(Refresh);

    private sealed record MonitorRow(MonitorInfo Info, string Label);

    internal void Refresh()
    {
        var session = _host.Session;
        var second = _host.LastSecond;
        StateText.Text = Strings.Get(_host.State switch
        {
            HostState.Problem => "State_Problem",
            HostState.Waiting => "State_Waiting",
            HostState.Streaming => "State_Streaming",
            HostState.Paused => "State_Paused",
            _ => "State_Connected",
        });
        DeviceText.Text = session is null ? Strings.Get("Main_NoDevice")
            : Strings.Format("Main_DeviceLine", session.DeviceName, Strings.Get(session.IsUsb ? "Link_Usb" : "Link_Lan"));
        DisconnectButton.IsEnabled = session is not null;
        VideoText.Text = _host.Streamer is not null && second is { Width: > 0 } s
            ? Strings.Format("Main_VideoLine", s.Width, s.Height, s.Frames, Strings.Get(s.HardwareEncoder ? "Encoder_Hardware" : "Encoder_Software"))
            : "—";

        if (!ReferenceEquals(_shownMonitors, _host.Monitors))
        {
            _shownMonitors = _host.Monitors;
            _updatingList = true;
            MonitorList.ItemsSource = _host.Monitors.Select((m, i) => new MonitorRow(m, Label(m, i + 1))).ToList();
            _updatingList = false;
        }

        _updatingList = true;
        MonitorList.SelectedItem = MonitorList.Items.Cast<MonitorRow>().FirstOrDefault(r => ReferenceEquals(r.Info, _host.Source));
        _updatingList = false;

        var usbBlocked = !_host.Settings.UsbEnabled || _host.AdbMissing;
        AddDeviceButton.IsEnabled = !usbBlocked && !_host.PortUnavailable;
        HintText.Text = usbBlocked ? Strings.Get("Hint_UsbUnavailable")
            : _host.RegisteredDevices == 0 ? Strings.Get("Hint_NoDevices")
            : _host.Source is null ? Strings.Get("Hint_ChooseSource")
            : "";

        var (banner, button, action) = PickBanner(second);
        BannerText.Text = banner ?? "";
        Banner.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;
        BannerButton.Content = button ?? "";
        BannerButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        _bannerAction = action;

        TouchCheck.Content = _host.HotKeyFailed ? Strings.Get("Touch_CheckNoHotkey") : Strings.Format("Touch_Check", _host.Settings.TouchHotkey);
        TouchCheck.IsChecked = _host.TouchEnabled;
        TouchCheck.IsEnabled = _host.TouchAvailable;
        var touchNote = !_host.TouchAvailable ? Strings.Get("Touch_Unavailable")
            : _host.HotKeyFailed ? Strings.Format("Touch_HotkeyFailed", _host.Settings.TouchHotkey)
            : null;
        TouchNote.Text = touchNote ?? "";
        TouchNote.Visibility = touchNote is null ? Visibility.Collapsed : Visibility.Visible;

        FooterText.Text = Strings.Format("Footer",
            Strings.Get(_host.Settings.UsbEnabled && !_host.AdbMissing ? "Footer_UsbOn" : "Footer_UsbOff"),
            Strings.Get(_host.Settings.LanEnabled ? "Footer_LanOn" : "Footer_LanOff"), // 待ち受けに失敗したときは帯(ポートを使えない)で出す
            _host.Settings.Port);

        SettingsPage.Refresh();
        DiagnosticsPage.Refresh();
        GuidePage.Refresh();
    }

    /// <summary>帯は優先の高い順に 1 つだけ(画面設計書 P2)。(文, ボタンの文字, ボタンの動き)。</summary>
    private (string? Text, string? Button, Action? Action) PickBanner(TabSpan.Platform.StreamSecond? second)
    {
        if (_host.PortUnavailable)
        {
            return (Strings.Format("Banner_Port", _host.Settings.Port), Strings.Get("Banner_OpenSettings"), () => SelectTab(TabSettings));
        }

        if (_host.CaptureError is { } error)
        {
            return (Strings.Format("Banner_CaptureError", error), Strings.Get("Banner_Retry"), _host.RetryCapture);
        }

        if (_host.PairingDataLost)
        {
            return (Strings.Get("Banner_PairingLost"), null, null);
        }

        if (_host.Settings.UsbEnabled && _host.AdbMissing)
        {
            return (Strings.Get("Banner_AdbMissing"), Strings.Get("Banner_OpenGuide"), () => SelectTab(TabGuide));
        }

        if (_host.Watcher is { Unauthorized.Count: > 0 })
        {
            return (Strings.Get("Banner_Unauthorized"), null, null);
        }

        if (_host.Watcher is { ReverseFailing: true })
        {
            return (Strings.Get("Banner_ReverseFailing"), null, null);
        }

        if (!_host.Monitors.Any(m => m.IsVirtual))
        {
            return (Strings.Get("Banner_NoVirtual"), Strings.Get("Banner_OpenGuide"), () => SelectTab(TabGuide));
        }

        if (_host.Streamer is not null && second is { Width: > 0, HardwareEncoder: false })
        {
            return (Strings.Get("Banner_SoftwareEncoder"), Strings.Get("Banner_OpenSettings"), () => SelectTab(TabSettings));
        }

        return (null, null, null);
    }

    private static string Label(MonitorInfo m, int number)
    {
        var tags = (m.Primary ? "  " + Strings.Get("Monitor_Primary") : "")
            + (m.IsVirtual ? "  " + Strings.Get("Monitor_Virtual") + "  " + Strings.Get("Monitor_Extend") : "")
            + (m.Attached ? "" : "  " + Strings.Get("Monitor_Detached"));
        return Strings.Format("Monitor_Line", number, m.DisplayName, m.Width, m.Height, tags);
    }

    private void OnBannerButton(object sender, RoutedEventArgs e) => _bannerAction?.Invoke();

    private void OnSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingList && MonitorList.SelectedItem is MonitorRow row && !ReferenceEquals(row.Info, _host.Source))
        {
            _host.SelectSource(row.Info.DeviceName);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _host.RefreshMonitors();
        Refresh();
    }

    private void OnAddDevice(object sender, RoutedEventArgs e) => new PairWindow(_host) { Owner = this }.ShowDialog();

    private void OnDisconnect(object sender, RoutedEventArgs e) => _ = _host.DisconnectAsync();

    private void OnTouchClick(object sender, RoutedEventArgs e) => _host.SetTouchEnabled(TouchCheck.IsChecked == true);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D4)
        {
            SelectTab(e.Key - Key.D1);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OnRefresh(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Hide(); // トレイにしまう
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true; // × はトレイにしまう(本当の終了はトレイの「終了」)
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _host.Changed -= OnHostChanged;
        SettingsPage.Detach();
        base.OnClosed(e);
    }
}
