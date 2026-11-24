# Changelog

この形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に従います。

## [Unreleased]

## [0.2.0] - 2026-11-24

端末のタッチで PC を操作できるようになった(Phase 3)。dtab d-01J で、触った位置が PC の画面の座標に正しく直って届くこと(画面の真ん中 → 2 枚目のモニターの真ん中)、黒い余白で触り始めた指を送らないこと、長押しの間に更新を送り直すこと、PC と端末のどちらで切っても送らないことを、注入をしない形(`serve --touch log`)で確かめた。

### Added

- PC: タッチの注入(§9.8)。接点の表(10 点まで)、映す画面の物理ピクセルへの変換、押したままの接点の送り直し(`touchRefreshMs`)、端末から 2 秒枠が来なければ取り消し、セッションの終わりと切り替えのときの取り消し、注入の失敗が 10 秒で 10 回続いたら自分で切る。注入は専用のスレッド 1 本から行う。
- PC: `TabSpan.App` に「端末のタッチで操作する」のチェックと、切り替えのホットキー(`Ctrl+Alt+T`、設定の `touchHotkey`)。切り替えは保存し、端末に `Notice(touchEnabled)` で知らせる。
- PC: `TabSpan.Probe serve --touch log|inject`。
- Android: 映像の画面のタッチを PC へ送る(10 本まで。映像の外で触り始めた指は送らない)。戻る操作で開くメニュー(端末側のタッチ・重ね表示・切断)。
- テストベクタ `frames.json` に Touch の枠を足した(PC と Android の両方が読む)。

### Changed

- Android: 映像の画面のタップで重ね表示を出し入れするのをやめた(タップは PC へ送る)。重ね表示はメニューから切り替える。
- Android: 戻る操作で切断していたのを、メニューを開くようにした。切断はメニューから。

### Known issues

- 2 本以上の指と、PC で実際に注入して操作することは、まだ確かめていない(adb では 1 本の指しか起こせないため)。
- 端末側のタッチの切り替えは保存しない(アプリを開き直すとオン)。
- 0.1.0 の Known issues のうち、App の画面の確かめ・タッチ以外の物(ハードウェアのエンコーダー、LAN、拡張、常駐、署名の鍵など)は残っている。

### Fixed

- PC の `TabSpan.App` が起動の直後に落ちていた(「リソース 'app.xaml' を検索できません」)。文言の既定の言語の置き場を別の DLL(Satellite)にしていたため、WPF が画面の定義まで別の DLL に探しに行っていた。文言を本体の DLL に入れるようにした。

## [0.1.1] - 2026-11-24

### Changed

- 端末が PC に伝える「復号できる大きさ」に、デコーダーの申告に加えて、実際に configure が通った大きさを足すようにした。dtab d-01J は申告が実力より狭く(60 fps で 1280x800 まで)、60 fps を選ぶと 1280x720 に縮めて送っていたが、1920x1080 を縮めずに送るようになった。復号に失敗したときは、今までどおり PC が大きさを 1 段ずつ下げる。

## [0.1.0] - 2026-11-24

PC の 1 枚のモニターを、USB で端末に複製して映せるようになった(Phase 1 と、Phase 2 のうち実在のモニターの複製まで)。dtab d-01J(Android 7.0)で、ペアリングから映像までを確かめた。1920x1080@30 で、PC で取り込んでから端末で復号されるまでの遅れの中央値は約 16 ms、捨てたフレームは 0(端末の画面に出るまでの時間は含まない)。

### Added

- PC: `TabSpan.Core`(OS に依らない部品: 枠・メッセージ・ペアリング・接続の状態・送る大きさの決め方など)。
- PC: `TabSpan.Link`。127.0.0.1:47815 での TLS の待ち受け、ペアリング(USB だけ。PC と端末の 6 桁を見比べて PC で許可)、端末の台帳、adb を探して `adb reverse` を張る。識別の鍵は DPAPI で守って `%APPDATA%\TabSpan` に置く。ログは `%LOCALAPPDATA%\TabSpan\logs`。
- PC: `TabSpan.Platform`。モニターの一覧、Windows.Graphics.Capture での取り込み、NV12 への変換と縮小、H.264 の圧縮、送る側の流れ。60 fps を選ぶと、端末のデコーダーの申告の範囲に縮めて送る。
- PC: `TabSpan.App`(WPF の仮の画面)。メインの画面(状態・映す画面の選択・切断)と「端末を追加」。
- PC: `TabSpan.Probe serve`。App と同じ部品で、窓を出さずに待ち受けて映す。`--pair` で `PAIRING CODE` を出し、端末の 6 桁を標準入力か `--approve-code` で渡す。
- Android: 本体のアプリ(`io.github.takosasidev.tabspan`)。ホーム・ペアリング・映像の 3 画面(Compose)。2 回目からは開くと自動でつながる。接続の部品 `link` と、復号と表示の部品 `video`。権限は INTERNET だけ。
- 依存: PC に `System.Security.Cryptography.ProtectedData` 10.0.0(MIT)。Android の本体に androidx・Compose・kotlinx など(Apache-2.0)。一覧は THIRD-PARTY-NOTICES.md。

### Known issues

- PC のハードウェアの H.264 エンコーダーは、まだ通らない。ソフトウェアのエンコーダーで圧縮する。
- `TabSpan.App` の画面は、まだ誰も起動して確かめていない。部品の組み立てまでは窓を出さずに確かめ、映像は `TabSpan.Probe serve` で確かめた。
- まだ無い物: タッチ、LAN と探索、仮想ディスプレイ(拡張)の取り付けと取り外し、トレイ・自動起動・多重起動の防止・診断の画面、PC の画面の英語の文言、端末側で PC を消す画面(やり直すときは端末で TabSpan のアプリのデータを消す)。
- PC 側はビルド済みの exe を配らない(Windows SDK の DLL の再配布の条件を確かめ終えていないため)。ソースからビルドする。
- APK は debug の鍵で署名した仮の物。
- 確かめた端末は dtab d-01J だけ。

## [0.0.2] - 2026-11-24

### Added

- Android の probe に「復号のテスト」を追加。置いた H.264 の映像を、デコーダーごとに Surface へ復号し、できるだけ速く入れたときの fps と、実時間で入れたときの遅れ(入れてから出るまで)を表にする。adb の `am start --es action decode-test` でも始められる。

### Changed

- PC の `encode`: ハードウェアのエンコーダーに D3D11 のデバイス マネージャーを渡すようにし、ハードウェアは全部を試すようにした(どれが通るかを知るため)。入力の形(システム メモリか D3D11 のテクスチャか)も出す。

## [0.0.1] - 2026-11-24

### Added

- Phase 0: PC 側の測定用ツール `TabSpan.Probe` を追加(monitors / capture / encoders / encode / detach / attach / touch / adb / clock / tls)。
- Phase 0: Android 側の測定用アプリ(probe)を追加。`video/avc` のデコーダーの一覧と候補の大きさごとの可否・configure の試し、画面の大きさ、PC の `tls` への TLS 1.2 の接続テスト(証明書の SHA-256 の指紋を表示)。結果は画面と logcat(タグ `TabSpanProbe`)に出す。adb の `am start --es action tls --ei port <N>` でも TLS のテストを始められる。

[Unreleased]: https://github.com/takosasi-dev/tab-span/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/takosasi-dev/tab-span/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/takosasi-dev/tab-span/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/takosasi-dev/tab-span/compare/v0.0.2...v0.1.0
[0.0.2]: https://github.com/takosasi-dev/tab-span/releases/tag/v0.0.2
[0.0.1]: https://github.com/takosasi-dev/tab-span/releases/tag/v0.0.1
