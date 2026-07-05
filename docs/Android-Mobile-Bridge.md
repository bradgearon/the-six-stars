# Six Stars Android Mobile Bridge

This update adds a small Android client plus a LAN bridge inside the desktop WPF harness.

## What it does

- The desktop UI starts a tiny HTTP bridge on TCP `8066` by default.
- The Android app defaults to `http://grasshoppah:8066` and pings `/health` every 5 seconds.
- The Android app can send typed text to the selected Star.
- The Android app can record 16 kHz mono PCM voice, wrap it as WAV, send it to the desktop, let the desktop Whisper endpoint transcribe it, then run the same Six Stars runtime and optional desktop TTS playback.
- Both the phone and the desktop UI show debug output for the request, transcript, Star response, state deltas, and errors.

## Desktop setup

1. Start the normal desktop app with `launch.cmd`, `start.cmd`, or Visual Studio.
2. In the right-side panel, find **Android Phone Bridge**.
3. The host starts automatically if `MobileBridge.EnabledOnStartup` is true in `src/Wds.SixStars.Ui/appsettings.json`.
4. The visible URL should be:

   ```text
   http://grasshoppah:8066
   ```

5. If the phone cannot connect, open Windows Firewall for inbound TCP `8066`, or change the Android URL to your desktop LAN IP, for example:

   ```text
   http://192.168.1.25:8066
   ```

The bridge uses a raw `TcpListener`, not `HttpListener`, so it does not require a Windows URL ACL or administrator mode.

## Android build

Android project path:

```text
android/six-stars-mobile
```

From Windows:

```bat
build-android-mobile.cmd
```

or:

```bat
cd android\six-stars-mobile
build-android-debug.cmd
```

The custom `gradlew.bat` bootstraps Gradle 9.6.1 into `android/six-stars-mobile/.gradle/bootstrap` the first time it runs. You can also open `android/six-stars-mobile` in Android Studio and build the `app` module.

Debug APK output:

```text
android/six-stars-mobile/app/build/outputs/apk/debug/app-debug.apk
```

To install with adb:

```bat
cd android\six-stars-mobile
install-android-debug.cmd
```

## Android app usage

1. Install and open **Six Stars Mobile**.
2. Grant microphone permission.
3. Confirm the bridge URL. Try `http://grasshoppah:8066` first. Use your PC's LAN IP if the name does not resolve from Android.
4. Tap **Test Connection**.
5. Pick a Star and request type.
6. Send a typed message, or tap **Record Voice**, speak, then tap **Stop / Send**.
7. Watch the response and debug window on the phone. The desktop UI also updates its Star output and debug panes.

## API endpoints

```text
GET  /health
GET  /api/mobile/stars
POST /api/mobile/message
POST /api/mobile/audio
```

### Text request JSON

```json
{
  "sessionId": "android-phone-dev",
  "starId": "Elusiaa",
  "requestType": "Chat",
  "playerText": "Elusiaa, answer from my Android phone.",
  "area": "VirethornCaves",
  "quest": "AndroidPhoneBridge",
  "romanceMode": true,
  "speakOnDesktop": true,
  "clientName": "SixStarsMobileAndroid"
}
```

### Audio request JSON

```json
{
  "sessionId": "android-phone-dev",
  "starId": "Elusiaa",
  "requestType": "Chat",
  "audioFormat": "wav",
  "sampleRate": 16000,
  "channels": 1,
  "bitsPerSample": 16,
  "audioBase64": "...",
  "area": "VirethornCaves",
  "quest": "AndroidPhoneBridge",
  "romanceMode": true,
  "speakOnDesktop": true,
  "clientName": "SixStarsMobileAndroid"
}
```

## Notes

- Android cleartext HTTP is explicitly enabled for local development.
- The app targets Android API 36 and uses native Java/Android SDK only, with no AndroidX dependency.
- Voice transcription still depends on your desktop Whisper/faster-whisper endpoint. Use `SIX_STARS_USE_MOCK_WHISPER=true` only for quick testing without real transcription.
- If Windows firewall blocks the port, the phone will show connection errors even though the desktop says the bridge is running.
