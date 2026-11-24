// P4 設定(画面設計書 §3.4): つなぎ方・映像・タッチ・常駐・端末の 5 つの組。値は変えた時点で AppHost.ApplySettings で保存して反映する(保存のボタンは無い)。
// 範囲の外の数字は欄の下に「{最小}〜{最大} の数字を入れてください」と出して保存しない。端末の解除・鍵の作り直し・初期値に戻すは確認を出す。
// 欄へ値を入れ直す(Load)のは開いた時と初期値に戻した時だけ。1 秒ごとの Refresh は状態の文だけを直す(入力中の欄を上書きしない)。
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TabSpan.Link;

namespace TabSpan.App;

public partial class SettingsView : UserControl
{
    private readonly DispatcherTimer _bitrateDelay = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppHost? _host;
    private bool _loading;
    private string _shownDevices = "";

    public SettingsView()
    {
        InitializeComponent();
        _bitrateDelay.Tick += (_, _) =>
        {
            _bitrateDelay.Stop();
            Apply(s => s with { BitrateMbps = (int)BitrateSlider.Value }); // 動かすたびにエンコーダーを作り直さない
        };
    }

    public sealed record DeviceRow(string DeviceId, string Name, string PairedAt, string LastSeenAt);

    public void Attach(AppHost host)
    {
        _host = host;
        Load();
    }

    public void Detach() => _bitrateDelay.Stop();

    /// <summary>今の設定を欄へ入れる。</summary>
    public void Load()
    {
        if (_host is null)
        {
            return;
        }

        var s = _host.Settings;
        _loading = true;
        UsbCheck.IsChecked = s.UsbEnabled;
        AutoLaunchCheck.IsChecked = s.AutoLaunchAndroidApp;
        LanCheck.IsChecked = s.LanEnabled;
        PortBox.Text = s.Port.ToString(Strings.Culture);
        AdbPathBox.Text = s.AdbPath;
        SelectByTag(FpsCombo, s.FpsCap.ToString(Strings.Culture));
        BitrateSlider.Value = s.BitrateMbps;
        BitrateText.Text = Strings.Format("Set_BitrateValue", s.BitrateMbps);
        SelectByTag(MaxEdgeCombo, s.MaxLongEdge.ToString(Strings.Culture));
        DetachCheck.IsChecked = s.DetachOnDisconnect;
        DetachSecBox.Text = s.DetachDelaySec.ToString(Strings.Culture);
        TouchCheck.IsChecked = s.TouchEnabled;
        HotkeyBox.Text = s.TouchHotkey;
        StartupCheck.IsChecked = s.StartWithWindows;
        SelectByTag(LanguageCombo, s.Language);
        PortError.Visibility = DetachError.Visibility = Visibility.Collapsed;
        _loading = false;
        Refresh();
    }

    /// <summary>状態の文(adb の場所・ホットキーの失敗・端末の一覧・タッチ)だけ直す。</summary>
    public void Refresh()
    {
        if (_host is null)
        {
            return;
        }

        _loading = true;
        TouchCheck.IsChecked = _host.Settings.TouchEnabled; // ホットキーやトレイで変わる
        TouchCheck.IsEnabled = _host.TouchAvailable;
        _loading = false;
        AdbFoundText.Text = _host.AdbFound is { } adb ? Strings.Format("Set_AdbFound", adb)
            : _host.Settings.UsbEnabled && _host.AdbMissing ? Strings.Get("Set_AdbNotFound")
            : "";
        HotkeyError.Visibility = _host.HotKeyFailed ? Visibility.Visible : Visibility.Collapsed;

        var devices = _host.Devices;
        var key = string.Join("\n", devices.Select(d => $"{d.DeviceId}\t{d.Name}\t{d.LastSeenAt:O}"));
        if (key != _shownDevices)
        {
            _shownDevices = key;
            DeviceList.ItemsSource = devices.Select(d => new DeviceRow(d.DeviceId, d.Name, Day(d.PairedAt), Day(d.LastSeenAt))).ToList();
            NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string Day(DateTimeOffset t) => t.ToLocalTime().ToString("d", Strings.Culture);

    private static void SelectByTag(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    /// <summary>欄の文字が min〜max の整数なら真。</summary>
    internal static bool TryRange(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    /// <summary>押したキーをホットキーの文字("Ctrl+Alt+T" の形)にする。修飾キーなし・修飾キーだけなら null。</summary>
    internal static string? HotKeyText(ModifierKeys modifiers, Key key)
    {
        if (modifiers == ModifierKeys.None || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(new KeyConverter().ConvertToInvariantString(key) ?? key.ToString());
        return string.Join("+", parts);
    }

    private void Apply(Func<Settings, Settings> change)
    {
        if (_loading || _host is null)
        {
            return;
        }

        _host.ApplySettings(change(_host.Settings));
        Refresh();
    }

    private void OnUsb(object sender, RoutedEventArgs e) => Apply(s => s with { UsbEnabled = UsbCheck.IsChecked == true });

    private void OnAutoLaunch(object sender, RoutedEventArgs e) => Apply(s => s with { AutoLaunchAndroidApp = AutoLaunchCheck.IsChecked == true });

    private void OnLan(object sender, RoutedEventArgs e) => Apply(s => s with { LanEnabled = LanCheck.IsChecked == true });

    private void OnTouch(object sender, RoutedEventArgs e) => Apply(s => s with { TouchEnabled = TouchCheck.IsChecked == true });

    private void OnDetach(object sender, RoutedEventArgs e) => Apply(s => s with { DetachOnDisconnect = DetachCheck.IsChecked == true });

    private void OnStartup(object sender, RoutedEventArgs e) => Apply(s => s with { StartWithWindows = StartupCheck.IsChecked == true });

    private void OnFps(object sender, SelectionChangedEventArgs e)
    {
        if (TagOf(FpsCombo) is { } t)
        {
            Apply(s => s with { FpsCap = int.Parse(t, System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private void OnMaxEdge(object sender, SelectionChangedEventArgs e)
    {
        if (TagOf(MaxEdgeCombo) is { } t)
        {
            Apply(s => s with { MaxLongEdge = int.Parse(t, System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private void OnLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (TagOf(LanguageCombo) is { } t)
        {
            Apply(s => s with { Language = t });
        }
    }

    private void OnBitrate(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BitrateText is null || _loading || _host is null)
        {
            return; // InitializeComponent の途中
        }

        BitrateText.Text = Strings.Format("Set_BitrateValue", (int)BitrateSlider.Value);
        _bitrateDelay.Stop();
        _bitrateDelay.Start();
    }

    private void OnCommitKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit(sender);
        }
    }

    private void OnPortCommit(object sender, RoutedEventArgs e) => Commit(PortBox);

    private void OnDetachSecCommit(object sender, RoutedEventArgs e) => Commit(DetachSecBox);

    private void OnAdbPathCommit(object sender, RoutedEventArgs e) => Commit(AdbPathBox);

    private void Commit(object box)
    {
        if (ReferenceEquals(box, PortBox))
        {
            CommitNumber(PortBox, PortError, 1024, 65535, (s, v) => s with { Port = v });
        }
        else if (ReferenceEquals(box, DetachSecBox))
        {
            CommitNumber(DetachSecBox, DetachError, 5, 600, (s, v) => s with { DetachDelaySec = v });
        }
        else if (ReferenceEquals(box, AdbPathBox) && _host is not null && AdbPathBox.Text.Trim() != _host.Settings.AdbPath)
        {
            Apply(s => s with { AdbPath = AdbPathBox.Text.Trim() });
        }
    }

    private void CommitNumber(TextBox box, TextBlock error, int min, int max, Func<Settings, int, Settings> change)
    {
        if (!TryRange(box.Text, min, max, out var v))
        {
            error.Text = Strings.Format("Set_Range", min, max);
            error.Visibility = Visibility.Visible;
            return; // 保存しない
        }

        error.Visibility = Visibility.Collapsed;
        Apply(s => change(s, v));
    }

    private void OnBrowseAdb(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "adb.exe|adb.exe", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AdbPathBox.Text = dialog.FileName;
            Commit(AdbPathBox);
        }
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            return; // 欄の移動はそのまま
        }

        e.Handled = true;
        if (HotKeyText(Keyboard.Modifiers, key) is { } text)
        {
            HotkeyBox.Text = text;
            Apply(s => s with { TouchHotkey = text });
        }
    }

    private void OnUnpair(object sender, RoutedEventArgs e)
    {
        if (_host is null || (sender as FrameworkElement)?.Tag is not DeviceRow row)
        {
            return;
        }

        if (Confirm(Strings.Format("Set_UnpairConfirm", row.Name)))
        {
            _ = RunAsync(() => _host.UnpairAsync(row.DeviceId));
        }
    }

    private void OnResetIdentity(object sender, RoutedEventArgs e)
    {
        if (_host is not null && Confirm(Strings.Get("Set_ResetIdentityConfirm")))
        {
            _ = RunAsync(_host.ResetIdentityAsync);
        }
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        if (_host is not null && Confirm(Strings.Get("Set_ResetSettingsConfirm")))
        {
            _host.ResetSettings();
            Load();
        }
    }

    private bool Confirm(string text) =>
        MessageBox.Show(Window.GetWindow(this), text, Strings.Get("App_Title"), MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _host?.Log.Write($"設定の操作に失敗した: {ex.Message}");
        }

        Refresh();
    }
}
