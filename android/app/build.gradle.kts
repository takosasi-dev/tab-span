// 端末側の本体アプリ(ホーム・ペアリング・映像・設定の画面)。Compose + Material 3、固定の暗い配色。
// 依存の向きは app → link、app → video。足した依存のライセンスはリポジトリ直下の THIRD-PARTY-NOTICES.md。
plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "io.github.takosasidev.tabspan"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.takosasidev.tabspan"
        minSdk = 24
        targetSdk = 35
        versionCode = 4
        versionName = "0.3.0"
    }

    // 配布の鍵は環境変数で渡したときだけ使う(docs/signing.md)。鍵とパスワードをリポジトリに入れない
    val keystore = System.getenv("TABSPAN_KEYSTORE")
    signingConfigs {
        if (!keystore.isNullOrEmpty()) {
            create("release") {
                storeFile = file(keystore)
                storePassword = System.getenv("TABSPAN_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("TABSPAN_KEY_ALIAS")
                keyPassword = System.getenv("TABSPAN_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    implementation(project(":link"))
    implementation(project(":video"))
    implementation(platform("androidx.compose:compose-bom:2024.10.00"))
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.9.3")
    testImplementation("junit:junit:4.13.2")
}
