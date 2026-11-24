// P1 トレイ: 4 つの状態を形と色の両方で分けたアイコン(待機 = 輪郭、接続中 = 輪郭に点、映している = 塗り、問題あり = 警告の印)とメニュー。
// メニュー: 開く / タッチを受け付ける / 映すのを止める(映している間だけ) / 終了。ダブルクリックで開く。通知(画面 §5)もここから出す。
// Show() を呼ぶまでアイコンは出ない(確かめでは出さない)。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace TabSpan.App;

public sealed class Tray : IDisposable
{
    // NotifyIcon.Text の上限(超えると例外)。端末の名前が長いときに切る
    private const int MaxTipLength = 63;

    private readonly Forms.NotifyIcon _icon = new();
    private readonly Forms.ToolStripMenuItem _open, _touch, _stopStreaming, _exit;
    private readonly Dictionary<HostState, Icon> _icons = [];

    public Tray(Action open, Action<bool> setTouch, Action stopStreaming, Action exit)
    {
        foreach (var s in Enum.GetValues<HostState>())
        {
            _icons[s] = DrawIcon(s, Forms.SystemInformation.SmallIconSize.Width);
        }

        _open = new Forms.ToolStripMenuItem(Strings.Get("Tray_Open"), null, (_, _) => open());
        _open.Font = new Font(_open.Font, System.Drawing.FontStyle.Bold);
        _touch = new Forms.ToolStripMenuItem(Strings.Get("Tray_Touch")) { CheckOnClick = true };
        _touch.Click += (_, _) => setTouch(_touch.Checked);
        _stopStreaming = new Forms.ToolStripMenuItem(Strings.Get("Tray_StopStreaming"), null, (_, _) => stopStreaming());
        _exit = new Forms.ToolStripMenuItem(Strings.Get("Tray_Exit"), null, (_, _) => exit());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_open, _touch, _stopStreaming, new Forms.ToolStripSeparator(), _exit]);
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => open();
        Update(HostState.Waiting, Strings.Get("Tray_Waiting"), touchEnabled: false, touchAvailable: false);
    }

    public HostState State { get; private set; }

    public string Tip => _icon.Text;

    public void Show() => _icon.Visible = true;

    /// <summary>部品の状態からアイコン・ツールチップ・メニューを描き直す。</summary>
    public void Refresh(AppHost host)
    {
        var state = host.State;
        var name = host.Session?.DeviceName ?? "";
        var tip = state switch
        {
            HostState.Waiting => Strings.Get("Tray_Waiting"),
            HostState.Problem => Strings.Get("Tray_Problem"),
            HostState.Streaming when host.LastSecond is { Width: > 0 } s => Strings.Format("Tray_Streaming", name, s.Width, s.Height, s.Fps),
            _ => Strings.Format("Tray_Connected", name),
        };
        Update(state, tip, host.TouchEnabled, host.TouchAvailable);
    }

    private void Update(HostState state, string tip, bool touchEnabled, bool touchAvailable)
    {
        State = state;
        _icon.Icon = _icons[state];
        _icon.Text = tip.Length > MaxTipLength ? tip[..(MaxTipLength - 1)] + "…" : tip;
        _touch.Checked = touchEnabled;
        _touch.Enabled = touchAvailable;
        _stopStreaming.Visible = state == HostState.Streaming;
    }

    /// <summary>通知を出す。アイコンが出ていなければ何もしない。</summary>
    public void Notify(string title, string body)
    {
        if (_icon.Visible)
        {
            _icon.ShowBalloonTip(5000, title, body, Forms.ToolTipIcon.None);
        }
    }

    /// <summary>状態ごとのアイコン(端末の形の角丸の四角)。形でも分けるので、色が見分けられなくても分かる。</summary>
    internal static Icon DrawIcon(HostState state, int size)
    {
        if (state == HostState.Paused)
        {
            state = HostState.Connected; // アイコンは 4 つ(一時停止中は接続中と同じ)
        }

        var color = state switch
        {
            HostState.Waiting => Color.FromArgb(0x70, 0x70, 0x70),
            HostState.Connected => Color.FromArgb(0x1E, 0x6F, 0xC8),
            HostState.Streaming => Color.FromArgb(0x1E, 0x8A, 0x4A),
            _ => Color.FromArgb(0xC8, 0x5A, 0x00),
        };
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var u = size / 16f;
            var body = new RectangleF(2 * u, 3 * u, 12 * u, 10 * u);
            using var path = RoundRect(body, 2 * u);
            using var pen = new Pen(color, 1.6f * u);
            using var brush = new SolidBrush(color);
            if (state == HostState.Streaming)
            {
                g.FillPath(brush, path);
            }
            else
            {
                g.DrawPath(pen, path);
            }

            if (state == HostState.Connected)
            {
                g.FillEllipse(brush, 6 * u, 6 * u, 4 * u, 4 * u);
            }

            if (state == HostState.Problem)
            {
                // 右下に警告の三角と「!」
                PointF[] tri = [new(11 * u, 7 * u), new(16 * u, 16 * u), new(6 * u, 16 * u)];
                g.FillPolygon(brush, tri);
                using var white = new Pen(Color.White, 1.4f * u);
                g.DrawLine(white, 11 * u, 10 * u, 11 * u, 13 * u);
                g.DrawLine(white, 11 * u, 14.2f * u, 11 * u, 15 * u);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone(); // 写しを持ち、元のハンドルはすぐ返す
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        foreach (var i in _icons.Values)
        {
            i.Dispose();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
