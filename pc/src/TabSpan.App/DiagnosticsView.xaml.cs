// P5 診断(画面設計書 §3.5・仕様 §9.9): 遅延の内訳の 50%/95%、フレームレート、捨てたフレーム、往復の時間、送信量、エンコーダー、大きさ、直近 60 秒の合計のグラフ。
// 数字は AppHost.Sample が 1 秒ごとに取った物を読む。映していない間は「—」。「数値をコピー」は端末の名前と IP アドレスを入れない。
// 送信から到着・到着から復号も StreamSecond から出す。
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TabSpan.Core;
using TabSpan.Platform;

namespace TabSpan.App;

public partial class DiagnosticsView : UserControl
{
    private const string None = "—";
    private AppHost? _host;
    private DiagText _shown = DiagText.Of(null, null, null);

    public DiagnosticsView()
    {
        InitializeComponent();
    }

    public void Attach(AppHost host)
    {
        _host = host;
        Refresh();
    }

    /// <summary>画面に出す文字(コピーの文にも使う)。</summary>
    internal sealed record DiagText(string CaptureP50, string CaptureP95, string ArriveP50, string ArriveP95, string DecodeP50, string DecodeP95,
        string TotalP50, string TotalP95, string Fps, string Dropped, string Rtt, string Rate, string Encoder, string Size, string Touch)
    {
        public static DiagText Of(StreamSecond? s, long? rttUs, LatencySummary? touch)
        {
            var c = Strings.Culture;
            static string Ms(LatencySummary? l, bool p95) =>
                l is { Count: > 0 } ? ((p95 ? l.P95Us : l.P50Us) / 1000.0).ToString("0.0", Strings.Culture) : None;
            return new DiagText(
                Ms(s?.CaptureToSend, false), Ms(s?.CaptureToSend, true),
                Ms(s?.SendToArrive, false), Ms(s?.SendToArrive, true),
                Ms(s?.ArriveToDecode, false), Ms(s?.ArriveToDecode, true),
                Ms(s?.Total, false), Ms(s?.Total, true),
                s is null ? None : $"{s.DeviceDecoded.ToString(c)} / {s.Fps.ToString(c)}",
                s is null ? None : Strings.Format("Diag_DroppedValue", s.BacklogDropped, s.DeviceDropped),
                s is null || rttUs is not { } rtt ? None : $"{(rtt / 1000.0).ToString("0.0", c)} ms",
                s is null ? None : $"{(s.Bytes / 1000.0).ToString("0", c)} kB/s",
                s is null ? None : Strings.Format("Diag_EncoderValue", s.Encoder, Strings.Get(s.HardwareEncoder ? "Diag_Hardware" : "Diag_Software")),
                s is null ? None : $"{s.Width.ToString(c)}x{s.Height.ToString(c)}",
                Ms(touch, true) is var t && t != None ? $"{t} ms" : None);
        }

        /// <summary>「数値をコピー」の文。端末の名前と IP アドレスは入れない。</summary>
        public string ToCopyText()
        {
            var b = new StringBuilder();
            b.AppendLine(CultureInfo.InvariantCulture, $"TabSpan {typeof(DiagnosticsView).Assembly.GetName().Version?.ToString(3)}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Latency")}\t{Strings.Get("Diag_P50")}\t{Strings.Get("Diag_P95")}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_CaptureToSend")}\t{CaptureP50}\t{CaptureP95}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_SendToArrive")}\t{ArriveP50}\t{ArriveP95}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_ArriveToDecode")}\t{DecodeP50}\t{DecodeP95}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Total")}\t{TotalP50}\t{TotalP95}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Fps")}\t{Fps}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Dropped")}\t{Dropped}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Rtt")}\t{Rtt}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Rate")}\t{Rate}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Encoder")}\t{Encoder}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Size")}\t{Size}");
            b.AppendLine(CultureInfo.InvariantCulture, $"{Strings.Get("Diag_Touch")}\t{Touch}");
            return b.ToString();
        }
    }

    public void Refresh()
    {
        if (_host is null)
        {
            return;
        }

        var s = _host.Streamer is null ? null : _host.LastSecond;
        var d = _shown = DiagText.Of(s, _host.Session?.Latency.RttUs, _host.TouchLatency);
        NotStreamingText.Visibility = s is null ? Visibility.Visible : Visibility.Collapsed;
        CaptureP50.Text = d.CaptureP50;
        CaptureP95.Text = d.CaptureP95;
        ArriveP50.Text = d.ArriveP50;
        ArriveP95.Text = d.ArriveP95;
        DecodeP50.Text = d.DecodeP50;
        DecodeP95.Text = d.DecodeP95;
        TotalP50.Text = d.TotalP50;
        TotalP95.Text = d.TotalP95;
        FpsText.Text = d.Fps;
        DroppedText.Text = d.Dropped;
        RttText.Text = d.Rtt;
        RateText.Text = d.Rate;
        EncoderText.Text = d.Encoder;
        SizeText.Text = d.Size;
        TouchText.Text = d.Touch;
        DrawGraph();
    }

    /// <summary>直近 60 秒の合計(50%)を折れ線にする。左端が 60 秒前、右端が今。縦は最大値に合わせる。</summary>
    internal static PointCollection GraphPoints(IReadOnlyList<long> valuesUs, double width, double height, out long maxUs)
    {
        maxUs = Math.Max(valuesUs.Count == 0 ? 0 : valuesUs.Max(), 1000); // 1 ms より小さく縮めない
        var points = new PointCollection(valuesUs.Count);
        var step = width / Math.Max(AppHost.HistorySeconds - 1, 1);
        var x0 = width - step * (valuesUs.Count - 1);
        for (var i = 0; i < valuesUs.Count; i++)
        {
            points.Add(new Point(x0 + step * i, height - 2 - (height - 4) * valuesUs[i] / maxUs));
        }

        return points;
    }

    private void DrawGraph()
    {
        if (_host is null || GraphCanvas.ActualWidth <= 0)
        {
            return;
        }

        var values = _host.History.Where(h => h.Total.Count > 0).Select(h => h.Total.P50Us).ToList();
        GraphLine.Points = GraphPoints(values, GraphCanvas.ActualWidth, GraphCanvas.ActualHeight, out var max);
        GraphMaxText.Text = values.Count == 0 ? "" : $"{(max / 1000.0).ToString("0", CultureInfo.InvariantCulture)} ms";
    }

    private void OnGraphSizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_shown.ToCopyText());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // ほかのアプリがクリップボードを開いている。もう一度押せばよい
        }
    }
}
