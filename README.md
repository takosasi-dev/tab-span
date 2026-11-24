# TabSpan

Android の端末を、Windows PC の拡張モニターにするソフトです。[English](README.en.md)

> **開発中です。まだ使える物はありません。**
> いまリポジトリにあるのは、設計を決めるための測定用ツール(Probe)の PC 側と Android 側だけです。アプリ本体は、これから作ります。

## 作ろうとしている物

PC 側(C# / .NET 10 / WPF の常駐アプリ)が画面を取り込み、H.264 に圧縮して送ります。Android 側(Kotlin)が復号して全画面で映し、タッチを PC に返します。つなぎ方は USB(adb の転送)と LAN です。

設計で守ること:

- 仮想ディスプレイのドライバを作りません。入れることも、設定を書き換えることもしません。利用者が別に入れた仮想ディスプレイを「映す画面」として選ぶだけです。実在のモニターを選べば、ドライバなしで複製として動きます。
- PC 側は管理者で動かしません。
- 通信は USB でも TLS で、ペアリングした端末だけを通します。初回のペアリングは USB 経由に限ります。
- 映像・タッチの座標・画面の内容を保存しません。ペアリングした端末のほかへ通信しません。

## 開発の状況

| 段階 | 中身 | 状態 |
|---|---|---|
| Phase 0 | 測定用ツール(Probe) | PC 側と Android 側を作った。家の PC と dtab(Android 7.0)で monitors・encoders・capture・encode(ソフトウェア)・adb・tls と Android の probe を実行。detach / attach・touch・clock と仮想ディスプレイはこれから |
| Phase 1〜6 | 本体 | 未着手 |

## いまある物: PC 側の Probe

動作環境: Windows 11(x64)、.NET 10 SDK。

```powershell
dotnet build pc\TabSpan.sln -c Release
```

`TabSpan.Probe.exe` を引数なしで動かすと、使い方が出ます。管理者でない PowerShell から動かしてください。

| サブコマンド | やること |
|---|---|
| `monitors` | モニターの一覧を出す |
| `capture <識別子>` | そのモニターを 5 秒取り込み、フレームの数と、枠を消す設定が通ったかを出す |
| `encoders` | H.264 のエンコーダーの一覧と、使えるプロパティを出す |
| `encode <識別子>` | 5 秒取り込んで圧縮し、フレームの数・バイト数・かかった時間を出す |
| `detach <識別子>` / `attach <識別子>` | モニターをデスクトップから外す・付ける(確認あり) |
| `touch <x> <y>` | その位置にタッチを注入して試す(確認あり) |
| `adb` | adb の場所と版、つながっている端末を出す(シリアルは伏せる) |
| `clock` | ミリ秒まで出る時計のウィンドウを出す(撮影で遅延を測るため) |
| `tls <port>` | 127.0.0.1 に TLS の待ち受けを 60 秒開く(端末からの接続テスト用) |

注意:

- `detach` は、実際にモニターをデスクトップから外します。主モニターと最後の 1 枚は断りますが、仮想ディスプレイで試してください。
- `touch` は、実際にタッチを注入します。
- `capture` と `encode` は、取り込んだ絵をファイルに保存しません。出すのは数と時間だけです。
- Probe の出力には、PC のモニターの名前などが入ります。そのまま公開の場所に貼らないでください。

## いまある物: Android 側の Probe

端末の `video/avc` のデコーダーの一覧と、候補の大きさごとに使えるか(configure も試す)、画面の大きさを調べて画面に出します。「TLS のテスト」で、PC の `tls` への TLS の接続を試します。出した行は logcat(タグ `TabSpanProbe`)にも出ます。

ビルド(JDK 17 と Android SDK。`android\local.properties` に `sdk.dir` を書いておく):

```powershell
cd android
.\gradlew.bat assembleDebug
```

TLS のテストの手順:

1. PC で `TabSpan.Probe.exe tls 47815` を動かす。
2. `adb reverse tcp:47815 tcp:47815` で端末の 127.0.0.1:47815 を PC へつなぐ。
3. アプリの「TLS のテスト」を押す。adb からなら `adb shell am start -n io.github.takosasidev.tabspan.probe/.MainActivity --es action tls --ei port 47815`。
4. PC と端末の両方に出る証明書の指紋(SHA-256)を見比べる。

注意:

- 権限は INTERNET だけです。つなぐ先は 127.0.0.1 だけです。
- TLS のテストは証明書を検査しません(probe だけの扱い)。相手は指紋を見比べて確かめてください。

## ダウンロード

[Releases](https://github.com/takosasi-dev/tab-span/releases) の v0.0.1(プレリリース)に、Android の probe の APK だけを置いています。

- APK は**デバッグ用の鍵で署名した仮の物**です。正式な鍵に変えた後は、いったん消して入れ直す必要があります。
- PC 側の exe は配りません。隣に入る Windows SDK の DLL の再配布の条件を確かめ終えていないためです。上の手順でソースからビルドしてください。

## ライセンス

MIT([LICENSE](LICENSE))。使っているライブラリのライセンスは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) にあります。
