# Android Mobile Bridge Quick Start

This archive now includes a phone test client under:

```text
android/six-stars-mobile
```

Start the desktop Six Stars UI first. The right-side debug panel has **Android Phone Bridge** and hosts TCP port `8066` by default. The Android app defaults to:

```text
http://grasshoppah:8066
```

If your phone cannot resolve `grasshoppah`, enter your PC LAN IP in the app, for example `http://192.168.1.25:8066`. Also allow inbound Windows Firewall traffic for TCP `8066`.

Build the debug APK from Windows:

```bat
build-android-mobile.cmd
```

or:

```bat
cd android\six-stars-mobile
build-android-debug.cmd
```

Install with adb:

```bat
cd android\six-stars-mobile
install-android-debug.cmd
```

More details are in:

```text
docs/Android-Mobile-Bridge.md
```

## Timeout / phone voice playback patch

This build includes the fix for the Visual Studio `client was null` exception in `MobileBridgeServer.HandleClientAsync`. That bug made the Android app time out even though the desktop bridge checkbox was enabled.

The Android checkbox now says **Play Star voice on this phone**. When checked, the phone sends `returnAudio=true` and `speakOnDesktop=false`; the desktop returns generated WAV chunks in the JSON response and the Android app plays them locally.
