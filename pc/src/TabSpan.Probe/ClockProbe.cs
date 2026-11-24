// ミリ秒まで出る時計のウィンドウを出す(仕様書 §12 Phase 0 (d)。2 つの画面を 1 枚の写真に収めて、時刻の差から遅延を読む)。
// 出すのは起動からの経過時間(分:秒.ミリ秒)で、Stopwatch から取る。ウィンドウを出すので、人が手で実行する。
using System.Diagnostics;

namespace TabSpan.Probe;

internal static class ClockProbe
{
    internal static int Run()
    {
        Console.WriteLine("時計のウィンドウを出します(起動からの 分:秒.ミリ秒)。ウィンドウを閉じると終わります。大きさを変えると字も変わります。");
        var result = "";
        // WinForms は STA のスレッドが要る。入口のスレッドは、取り込みと Media Foundation のために MTA のままにしてある
        var thread = new Thread(() => result = Attempt.Read(() =>
        {
            using var form = new ClockForm();
            Application.Run(form);
            return "OK";
        }));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine($"時計のウィンドウ: {result}");
        return result == "OK" ? 0 : 1;
    }

    private sealed class ClockForm : Form
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        internal ClockForm()
        {
            Text = "TabSpan.Probe clock";
            ClientSize = new Size(1200, 300);
            BackColor = Color.Black;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var elapsed = _clock.Elapsed;
            var text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}";
            using var font = new Font(FontFamily.GenericMonospace, Math.Max(8f, Math.Min(ClientSize.Height * 0.7f, ClientSize.Width / 6.5f)), FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(text, font, Brushes.White, ClientRectangle, format);

            // 描くたびに次の描画を頼み、できるだけ新しい時刻を出し続ける(タイマーだと 15 ms ほど古い値が映ることがある)
            Invalidate();
        }
    }
}
