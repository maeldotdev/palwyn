// The emergency-screen helper. Not an installed app: the PC pushes this APK to the phone through adb and runs it with
// app_process as the shell user, which may capture the screen and inject input without asking the phone (docs/security.md).
plugins {
    id("com.android.application")
}

android {
    namespace = "dev.palwyn.emergency"
    compileSdk = 37

    defaultConfig {
        applicationId = "dev.palwyn.emergency"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "1"
    }

    buildTypes {
        release {
            isMinifyEnabled = false // classes are looked up by name from app_process
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

// Plain Java: AGP's built-in Kotlin would otherwise add the 2 MB Kotlin standard library to the helper.
configurations.matching { it.name == "debugRuntimeClasspath" || it.name == "releaseRuntimeClasspath" }
    .configureEach { exclude(group = "org.jetbrains.kotlin") }

dependencies {
    testImplementation("junit:junit:4.13.2")
}
