# Third-party notices

TabSpan 自身のコードは MIT です。ビルドのときに NuGet と Maven から取る物と、そのライセンスは次のとおりです(2026-11-24 に確認。PC 側は各パッケージの nuspec とライセンスの本文、Android 側は jar に入っているライセンスの本文か pom。kotlin-stdlib の jar には記述が無いため、Kotlin のライセンス Apache-2.0 によります)。

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

## Android 側(probe の APK に入る物)

| ライブラリ | 版 | ライセンス | 役目 |
|---|---|---|---|
| org.jetbrains.kotlin:kotlin-stdlib | 2.0.21 | Apache-2.0 | Kotlin の標準ライブラリ |
| org.jetbrains:annotations | 13.0 | Apache-2.0 | kotlin-stdlib が使う注釈 |

## Android 側(テストの時だけ使う物。APK には入らない)

| ライブラリ | 版 | ライセンス | 役目 |
|---|---|---|---|
| junit:junit | 4.13.2 | EPL-1.0 | 単体テスト |
| org.hamcrest:hamcrest-core | 1.3 | BSD-3-Clause | junit が使う |

この 2 つは、作者の判断でテスト専用の例外にしました。APK には入りません。

版は `cd android; .\gradlew.bat -q :probe:dependencies --configuration releaseRuntimeClasspath` で確かめられます。
