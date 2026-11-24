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

## PC 側(参照するが、出力には入らない物)

| パッケージ | 版 | ライセンス | 役目 |
|---|---|---|---|
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT | `TabSpan.Link` が参照する。PC の識別の鍵を DPAPI で守る |

App(WPF)と Probe(Windows Forms)は Windows デスクトップの .NET ランタイムに同じ物があり、そちらが使われます。2026-11-24 のビルドでは、この DLL は exe の隣に出ず、`TabSpan.App.deps.json` にも載っていませんでした。

## PC 側(テストの時だけ使う物。出力には入らない)

| パッケージ | 版 | ライセンス |
|---|---|---|
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.CodeCoverage | 17.14.1 | MIT |
| Microsoft.TestPlatform.ObjectModel | 17.14.1 | MIT |
| Microsoft.TestPlatform.TestHost | 17.14.1 | MIT |
| Newtonsoft.Json | 13.0.3 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.assert | 2.9.3 | Apache-2.0 |
| xunit.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 |
| xunit.analyzers | 1.18.0 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| xunit.abstractions | 2.0.3 | nuspec にはライセンスの式が無く、xunit のリポジトリの `license.txt` への URL だけ(本文はまだ確かめていない) |

`pc\tests` の 3 つのテストのプロジェクトの `obj\project.assets.json` にある物の全部です。ライセンスは各 nuspec の `<license>` で確かめました。

## Android 側(app の APK に入る物)

`:app` が直接使うのは Compose の BOM 2024.10.00(`androidx.compose.material3:material3` 1.3.0 ほか)と `androidx.activity:activity-compose` 1.9.3 です。`:link` と `:video` は Android の SDK だけを使います。下は推移的な物を含む全部で、2026-11-24 に各版の POM の `<licenses>` で確かめました(POM に無い物は親 POM)。同じ日に `:app:dependencies --configuration releaseRuntimeClasspath` の出力と突き合わせ、81 件が過不足なく合うことを確かめました。

| ライブラリ | 版 | ライセンス |
|---|---|---|
| androidx.activity:activity-compose | 1.9.3 | Apache-2.0 |
| androidx.activity:activity-ktx | 1.9.3 | Apache-2.0 |
| androidx.activity:activity | 1.9.3 | Apache-2.0 |
| androidx.annotation:annotation-experimental | 1.4.1 | Apache-2.0 |
| androidx.annotation:annotation-jvm | 1.8.1 | Apache-2.0 |
| androidx.annotation:annotation | 1.8.1 | Apache-2.0 |
| androidx.arch.core:core-common | 2.2.0 | Apache-2.0 |
| androidx.arch.core:core-runtime | 2.2.0 | Apache-2.0 |
| androidx.autofill:autofill | 1.0.0 | Apache-2.0 |
| androidx.collection:collection-jvm | 1.4.4 | Apache-2.0 |
| androidx.collection:collection-ktx | 1.4.4 | Apache-2.0 |
| androidx.collection:collection | 1.4.4 | Apache-2.0 |
| androidx.compose.animation:animation-android | 1.7.4 | Apache-2.0 |
| androidx.compose.animation:animation-core-android | 1.7.4 | Apache-2.0 |
| androidx.compose.animation:animation-core | 1.7.4 | Apache-2.0 |
| androidx.compose.animation:animation | 1.7.4 | Apache-2.0 |
| androidx.compose.foundation:foundation-android | 1.7.4 | Apache-2.0 |
| androidx.compose.foundation:foundation-layout-android | 1.7.4 | Apache-2.0 |
| androidx.compose.foundation:foundation-layout | 1.7.4 | Apache-2.0 |
| androidx.compose.foundation:foundation | 1.7.4 | Apache-2.0 |
| androidx.compose.material3:material3-android | 1.3.0 | Apache-2.0 |
| androidx.compose.material3:material3 | 1.3.0 | Apache-2.0 |
| androidx.compose.material:material-icons-core-android | 1.7.4 | Apache-2.0 |
| androidx.compose.material:material-icons-core | 1.7.4 | Apache-2.0 |
| androidx.compose.material:material-ripple-android | 1.7.4 | Apache-2.0 |
| androidx.compose.material:material-ripple | 1.7.4 | Apache-2.0 |
| androidx.compose.runtime:runtime-android | 1.7.4 | Apache-2.0 |
| androidx.compose.runtime:runtime-saveable-android | 1.7.4 | Apache-2.0 |
| androidx.compose.runtime:runtime-saveable | 1.7.4 | Apache-2.0 |
| androidx.compose.runtime:runtime | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-android | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-geometry-android | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-geometry | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-graphics-android | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-graphics | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-text-android | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-text | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-unit-android | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-unit | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-util-android | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui-util | 1.7.4 | Apache-2.0 |
| androidx.compose.ui:ui | 1.7.4 | Apache-2.0 |
| androidx.compose:compose-bom | 2024.10.00 | Apache-2.0(版をそろえる表だけ。APK に中身は入らない) |
| androidx.concurrent:concurrent-futures | 1.1.0 | Apache-2.0 |
| androidx.core:core-ktx | 1.13.1 | Apache-2.0 |
| androidx.core:core | 1.13.1 | Apache-2.0 |
| androidx.customview:customview-poolingcontainer | 1.0.0 | Apache-2.0 |
| androidx.emoji2:emoji2 | 1.3.0 | Apache-2.0 |
| androidx.graphics:graphics-path | 1.0.1 | Apache-2.0 |
| androidx.interpolator:interpolator | 1.0.0 | Apache-2.0 |
| androidx.lifecycle:lifecycle-common-java8 | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-common-jvm | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-common | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-livedata-core | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-process | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-runtime-android | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-runtime-compose-android | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-runtime-compose | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-runtime-ktx-android | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-runtime-ktx | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-runtime | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-viewmodel-android | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-viewmodel-ktx | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-viewmodel-savedstate | 2.8.3 | Apache-2.0 |
| androidx.lifecycle:lifecycle-viewmodel | 2.8.3 | Apache-2.0 |
| androidx.profileinstaller:profileinstaller | 1.3.1 | Apache-2.0 |
| androidx.savedstate:savedstate-ktx | 1.2.1 | Apache-2.0 |
| androidx.savedstate:savedstate | 1.2.1 | Apache-2.0 |
| androidx.startup:startup-runtime | 1.1.1 | Apache-2.0 |
| androidx.tracing:tracing | 1.0.0 | Apache-2.0 |
| androidx.versionedparcelable:versionedparcelable | 1.1.1 | Apache-2.0 |
| com.google.guava:listenablefuture | 1.0 | Apache-2.0(親 POM に記載) |
| org.jetbrains.kotlin:kotlin-stdlib-common | 2.0.21 | Apache-2.0 |
| org.jetbrains.kotlin:kotlin-stdlib-jdk7 | 1.8.20 | Apache-2.0 |
| org.jetbrains.kotlin:kotlin-stdlib-jdk8 | 1.8.20 | Apache-2.0 |
| org.jetbrains.kotlin:kotlin-stdlib | 2.0.21 | Apache-2.0 |
| org.jetbrains.kotlinx:kotlinx-coroutines-android | 1.7.3 | Apache-2.0 |
| org.jetbrains.kotlinx:kotlinx-coroutines-bom | 1.7.3 | Apache-2.0(版をそろえる表だけ。APK に中身は入らない) |
| org.jetbrains.kotlinx:kotlinx-coroutines-core-jvm | 1.7.3 | Apache-2.0 |
| org.jetbrains.kotlinx:kotlinx-coroutines-core | 1.7.3 | Apache-2.0 |
| org.jetbrains:annotations | 23.0.0 | Apache-2.0 |

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
| com.vaadin.external.google:android-json | 0.0.20131108.vaadin1 | Apache-2.0 | `:link` の JVM の単体テストで org.json を動かす(AOSP の org.json を切り出した物。android.jar の org.json はテストでは中身が無い) |

junit と hamcrest-core の 2 つは、作者の判断でテスト専用の例外にしました。この表の物はどれも APK には入りません。

版は `cd android; .\gradlew.bat -q :app:dependencies --configuration releaseRuntimeClasspath`(probe は `:probe:dependencies`)で確かめられます。
