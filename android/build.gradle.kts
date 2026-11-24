// Android 側の全体のビルド設定。プラグインの版だけをここで決める。
// 版は 2026-11-24 に家の PC の Gradle キャッシュにあった物(仕様書 §8.6)。
plugins {
    id("com.android.application") version "8.7.0" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
}
