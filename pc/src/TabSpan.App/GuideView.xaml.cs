// P6 はじめに(画面設計書 §3.6): 仮想ディスプレイ・adb・USB デバッグの 3 つの手順を、済んだかどうかの印つきで並べる。
// 印は TabSpan が見て付ける(仮想のモニターがある / adb の version が通った / device の端末がある)。コマンドはコピーするだけで実行しない(INV-1・INV-2)。
using System.Windows;
using System.Windows.Controls;

namespace TabSpan.App;

public partial class GuideView : UserControl
{
    public const string VirtualDisplayCommand = "winget install --id=VirtualDrivers.Virtual-Display-Driver -e";
    public const string AdbCommand = "winget install Google.PlatformTools";

    private AppHost? _host;
    private Action? _refreshMonitors;

    public GuideView()
    {
        InitializeComponent();
        Command1.Text = VirtualDisplayCommand;
        Command2.Text = AdbCommand;
    }

    public void Attach(AppHost host, Action refreshMonitors)
    {
        _host = host;
        _refreshMonitors = refreshMonitors;
        Refresh();
    }

    /// <summary>3 つの手順が済んだか。</summary>
    internal static (bool VirtualDisplay, bool Adb, bool UsbDebugging) StepsDone(AppHost host) =>
        (host.Monitors.Any(m => m.IsVirtual), host.AdbFound is not null, host.Watcher is { Reversed.Count: > 0 });

    public void Refresh()
    {
        if (_host is null)
        {
            return;
        }

        var (v, a, u) = StepsDone(_host);
        SetMark(Mark1, Mark1Text, v);
        SetMark(Mark2, Mark2Text, a);
        SetMark(Mark3, Mark3Text, u);
    }

    private void SetMark(Border mark, TextBlock text, bool done)
    {
        text.Text = Strings.Get(done ? "Guide_Done" : "Guide_NotDone");
        // 済み = 塗り、まだ = 輪郭だけ(色が見分けられなくても形で分かる)
        mark.SetResourceReference(Border.BackgroundProperty, done ? SystemColors.HighlightBrushKey : SystemColors.WindowBrushKey);
        mark.SetResourceReference(Border.BorderBrushProperty, done ? SystemColors.HighlightBrushKey : SystemColors.GrayTextBrushKey);
        text.SetResourceReference(TextBlock.ForegroundProperty, done ? SystemColors.HighlightTextBrushKey : SystemColors.GrayTextBrushKey);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string command)
        {
            return;
        }

        try
        {
            Clipboard.SetText(command); // 実行はしない
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // ほかのアプリがクリップボードを開いている。もう一度押せばよい
        }
    }

    private void OnRefreshMonitors(object sender, RoutedEventArgs e) => _refreshMonitors?.Invoke();
}
