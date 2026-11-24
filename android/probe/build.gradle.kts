// Phase 0 の probe アプリ(TabSpan)。端末のデコーダー・画面・TLS を調べて画面に出すだけ。
// 依存のライブラリは持たない(Android の SDK だけ)。
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.github.takosasidev.tabspan.probe"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.takosasidev.tabspan.probe"
        minSdk = 24
        targetSdk = 35
        versionCode = 2
        versionName = "0.0.2"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            // ponytail: 配布の署名の鍵は Q-6 が決まるまで debug の鍵で署名する
            signingConfig = signingConfigs.getByName("debug")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}
