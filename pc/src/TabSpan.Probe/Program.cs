// TabSpan.Probe の入口。Phase 0 の実測用で、引数でサブコマンドを選ぶ(仕様書 §12 Phase 0 (b))。
// 出力は仕様書 §8.1・§8.2・§8.8 に貼る。製品の本体からは参照しない。
using System.Runtime.InteropServices;
using TabSpan.Probe;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

if (args.Length == 0)
{
    return Usage();
}

// 大きさ・位置・タッチの座標を物理ピクセルで扱うため(仕様書 §9.8)。マニフェストは既定のままにして、ここで設定する
if (!PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
{
    Console.WriteLine($"注意: SetProcessDpiAwarenessContext(PerMonitorV2) が失敗しました (GetLastError={Marshal.GetLastPInvokeError()})。座標が拡大率の分だけずれることがあります");
}

return args switch
{
    ["monitors"] => MonitorsProbe.Run(),
    ["capture", var id] => CaptureProbe.Run(id),
    ["encoders"] => EncodersProbe.Run(),
    ["encode", var id] => EncodeProbe.Run(id),
    ["detach", var id] => DisplayProbe.Detach(id),
    ["attach", var id] => DisplayProbe.Attach(id, null),
    ["attach", var id, var x, var y, var width, var height] => DisplayProbe.Attach(id, [x, y, width, height]),
    ["touch", var x, var y] => TouchProbe.Run(x, y),
    ["adb"] => AdbProbe.Run(),
    ["clock"] => ClockProbe.Run(),
    ["tls", var port] => TlsProbe.Run(port),
    ["serve", .. var rest] => ServeCommand.Run(rest),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        TabSpan.Probe  (管理者でない PowerShell から動かす。<識別子> は monitors が出す DeviceName。例: \\.\DISPLAY2)
          monitors                    モニターの一覧(DeviceName・DeviceString・DeviceID・大きさ・位置・付いているか・主モニターか)
          capture <識別子>            そのモニターを 5 秒取り込み、フレームの数・大きさ・カーソルと枠の設定が通ったかを出す
          encoders                    H.264 のエンコーダーの一覧(名前・ハードウェアかどうか)と、使えるプロパティを出す
          encode <識別子>             5 秒取り込んで H.264 に圧縮し、フレームの数・キーフレームの数・バイト数・圧縮の時間を出す
          detach <識別子>             そのモニターをデスクトップから外す(確認あり。主モニターと最後の 1 枚は断る)
          attach <識別子> [x y 幅 高さ]  そのモニターをデスクトップに付ける(確認あり。位置と大きさを省くと右端に置き、大きさは Windows に任せる)
          touch <x> <y>               その位置を 1 回タップし、3 秒の長押しを更新の間隔 50・100・200・500 ms で試す(確認あり)
          adb                         adb の場所・adb version・adb devices を出す(シリアルは伏せる)
          clock                       ミリ秒まで出る時計のウィンドウを出す(撮影で遅延を測るため)
          tls <port>                  自己署名の証明書(ECDSA P-256)で、127.0.0.1 に TLS 1.2 以上の待ち受けを 60 秒開く
          serve [--monitor <識別子>] [--fps 30|60] [--bitrate <Mbps>] [--seconds <N>] [--pair] [--approve-code <6桁>] [--no-adb] [--software]
                                      窓を出さずに App と同じ部品で待ち受けて映す(TABSPAN_DATA_DIR で置き場を切り替えられる)
        """);
    return 2;
}
