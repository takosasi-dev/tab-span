// 映像の部品: video/avc のデコーダーの能力の調べと、MediaCodec で復号して Surface に出す所。
// 依存のライブラリは持たない(Android の SDK だけ)。link を参照しない。
plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.github.takosasidev.tabspan.video"
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
