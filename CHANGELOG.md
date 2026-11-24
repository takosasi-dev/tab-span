# Changelog

この形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に従います。

## [Unreleased]

## [0.0.1] - 2026-11-24

### Added

- Phase 0: PC 側の測定用ツール `TabSpan.Probe` を追加(monitors / capture / encoders / encode / detach / attach / touch / adb / clock / tls)。
- Phase 0: Android 側の測定用アプリ(probe)を追加。`video/avc` のデコーダーの一覧と候補の大きさごとの可否・configure の試し、画面の大きさ、PC の `tls` への TLS 1.2 の接続テスト(証明書の SHA-256 の指紋を表示)。結果は画面と logcat(タグ `TabSpanProbe`)に出す。adb の `am start --es action tls --ei port <N>` でも TLS のテストを始められる。

[Unreleased]: https://github.com/takosasi-dev/tab-span/compare/v0.0.1...HEAD
[0.0.1]: https://github.com/takosasi-dev/tab-span/releases/tag/v0.0.1
