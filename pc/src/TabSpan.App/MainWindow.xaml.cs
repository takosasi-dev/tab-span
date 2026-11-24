// 仮のメインの画面(画面設計書 P2): 状態、つないでいる端末、送っている大きさと fps、映す画面の選択、「端末を追加」。
// 1 秒ごとと、部品の状態が変わった時に描き直す。文言は Strings.ja.resx。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TabSpan.Platform;

namespace TabSpan.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<MonitorInfo>? _shownMonitors;
    private StreamSecond? _lastSecond;
    private bool _updatingList;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _host.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _timer.Tick += (_, _) =>
        {
            _lastSecond = _host.Streamer?.TakeSecond();
            Refresh();
        };
        _timer.Start();
        Refresh();
    }

    private sealed record MonitorRow(MonitorInfo Info, string Label);

    private void Refresh()
    {
        var session = _host.Session;
        var streamer = _host.Streamer;
        StateText.Text = Strings.Get(
            _host.PortUnavailable ? "State_Problem"
            : session is null ? "State_Waiting"
            : streamer is null ? "State_Paused"
            : "State_Streaming");
        DeviceText.Text = session is null ? Strings.Get("Main_NoDevice")
            : Strings.Format("Main_DeviceLine", session.DeviceName, Strings.Get(session.IsUsb ? "Link_Usb" : "Link_Lan"));
        DisconnectButton.IsEnabled = session is not null;
        VideoText.Text = streamer is not null && _lastSecond is { Width: > 0 } s
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

        // 帯は優先の高い順に 1 つだけ(画面設計書 P2)
        var banner = _host.PortUnavailable ? Strings.Format("Banner_Port", _host.Settings.Port)
            : _host.PairingDataLost ? Strings.Get("Banner_PairingLost")
            : _host.AdbMissing ? Strings.Get("Banner_AdbMissing")
            : _host.Watcher is { Unauthorized.Count: > 0 } ? Strings.Get("Banner_Unauthorized")
            : _host.Watcher is { ReverseFailing: true } ? Strings.Get("Banner_ReverseFailing")
            : streamer is not null && _lastSecond is { Width: > 0, HardwareEncoder: false } ? Strings.Get("Banner_SoftwareEncoder")
            : null;
        BannerText.Text = banner ?? "";
        Banner.Visibility = banner is null ? Visibility.Collapsed : Visibility.Visible;

        FooterText.Text = Strings.Format("Footer", Strings.Get(_host.Settings.UsbEnabled && !_host.AdbMissing ? "Footer_UsbOn" : "Footer_UsbOff"), _host.Settings.Port);
    }

    private static string Label(MonitorInfo m, int number)
    {
        var tags = (m.Primary ? " " + Strings.Get("Monitor_Primary") : "")
            + (m.IsVirtual ? " " + Strings.Get("Monitor_Virtual") + " " + Strings.Get("Monitor_Extend") : "")
            + (m.Attached ? "" : " " + Strings.Get("Monitor_Detached"));
        return Strings.Format("Monitor_Line", number, m.DisplayName, m.Width, m.Height, tags);
    }

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

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
