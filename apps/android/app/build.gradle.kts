plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "dev.palwyn"
    compileSdk = 37

    defaultConfig {
        applicationId = "dev.palwyn"
        minSdk = 29
        targetSdk = 36
        versionCode = 3
        versionName = "0.13.0"
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-dev"
        }
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }

    buildFeatures {
        compose = true
    }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.02.01"))
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-core:1.7.3")
    implementation("androidx.compose.material:material-icons-extended:1.7.3")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")

    testImplementation("junit:junit:4.13.2")
    // Android's bundled org.json is a stub in JVM unit tests; use the real implementation there.
    testImplementation("org.json:json:20250517")
}
