// Android 側の Gradle の入口。
// probe は Phase 0 の調べ物、app・link・video が本体(依存の向きは app → link、app → video)。
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "TabSpanAndroid"
include(":probe")
include(":app")
include(":link")
include(":video")
