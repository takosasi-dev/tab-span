// Android 側の Gradle の入口。Phase 0 は probe だけを含む。
// app・link は Phase 1 で足す。
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
