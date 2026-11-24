// 接続の部品: 枠・メッセージ・ペアリングの番号・TLS・保存。映像とタッチの型を参照しない(INV-14)。
// 実行時の依存は持たない(JSON は Android 標準の org.json)。
plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.github.takosasidev.tabspan.link"
    compileSdk = 35

    defaultConfig {
        minSdk = 24
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
    // JVM の単体テストでは android.jar の org.json がスタブなので、AOSP の org.json を切り出した物を使う(Apache-2.0、テストだけ)
    testImplementation("com.vaadin.external.google:android-json:0.0.20131108.vaadin1")
}
