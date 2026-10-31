plugins {
    id("com.android.application")
}

android {
    namespace = "com.wds.sixstarsstandalone"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.wds.sixstarsstandalone"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    lint {
        abortOnError = true
    }
}

// The standalone APK ships a local snapshot of the exact desktop Content folder. When this project
// is built from the full repository, refresh that snapshot automatically so Stars/templates/voice
// clones remain byte-for-byte aligned with Wds.SixStars.Api/Content.
val desktopContentDir = file("../../../src/Wds.SixStars.Api/Content")
val standaloneContentDir = file("src/main/assets/sixstars_content")
val syncSixStarsContent by tasks.registering(Sync::class) {
    onlyIf { desktopContentDir.exists() }
    from(desktopContentDir)
    into(standaloneContentDir)
}

tasks.configureEach {
    if (name == "preBuild") dependsOn(syncSixStarsContent)
}

dependencies {
    // Explicit GIF frames keep idle artwork and lip anchors on one frame index.
    implementation("com.github.bumptech.glide:gifdecoder:4.16.0")
    implementation("androidx.activity:activity:1.12.4")
    implementation("androidx.core:core:1.17.0")
    implementation("com.squareup.okhttp3:okhttp:5.4.0")
}
