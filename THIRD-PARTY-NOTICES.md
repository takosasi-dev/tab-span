# Third-party notices

TabSpan 自身のコードは MIT です。ビルドのときに NuGet から取る物と、そのライセンスは次のとおりです(2026-11-24 に各パッケージの nuspec とライセンスの本文で確認)。

## PC 側(ビルドの時だけ使う物。出力には入らない)

| パッケージ | 版 | ライセンス | 役目 |
|---|---|---|---|
| Microsoft.Windows.CsWin32 | 0.3.371 | MIT | Win32 と COM の宣言をビルドの時に生成する |
| Microsoft.Windows.SDK.Win32Metadata | 71.0.45-preview | Microsoft Windows SDK のライセンス | 上が読むメタデータ |
| Microsoft.Windows.WDK.Win32Metadata | 0.13.25-experimental | Microsoft Windows SDK のライセンス | 同上 |
| Microsoft.Windows.SDK.Win32Docs | 0.1.42-alpha | Microsoft Windows SDK のライセンス | 同上(説明文) |

## PC 側(ビルドした exe の隣に入る物)

| 物 | 版 | ライセンス | 役目 |
|---|---|---|---|
| Microsoft.Windows.SDK.NET.Ref(`Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll`) | 10.0.22621.57 | Microsoft Windows SDK のライセンス | Windows.Graphics.Capture(WinRT)を C# から呼ぶための投影 |

この 2 つの DLL は、リポジトリには入っていません(ソースだけを置いています)。ビルドすると exe の隣に出ます。ビルド済みの exe を配る前に、再配布の条項を確かめます(まだ確かめていません)。

## Android 側

まだありません。
