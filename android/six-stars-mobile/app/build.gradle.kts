plugins {
    id("com.android.application")
}

android {
    namespace = "com.wds.sixstarsmobile"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.wds.sixstarsmobile"
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
        error += "Deprecated"
    }

}


dependencies {
    implementation("androidx.activity:activity:1.12.4")
    implementation("androidx.core:core:1.17.0")
}
