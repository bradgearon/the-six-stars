# Android foreground request stuck / socket recovery fix

This update addresses Android text/voice/photo requests getting stuck behind background at-rest Star initiative work and then failing with errors such as:

`java.net.SocketException: Software caused connection abort`

## What changed

- Android foreground text, voice, and image requests now pause the Android at-rest initiative background service before sending.
- The Android at-rest service now waits about 30 seconds before its first quiet check instead of immediately competing with a fresh foreground session.
- Desktop mobile bridge foreground requests now preempt current at-rest/background runtime operations before waiting for the mobile runtime gate.
- At-rest Star initiative requests no longer wait behind an active foreground request. If the bridge is busy, the at-rest check is skipped and will try again later.
- Desktop now publishes a queued `MobileConversationResponseReady` / `MobileConversationResponseFailed` event for direct `/api/mobile/message` and `/api/mobile/audio` replies.
- Android now handles those queued conversation response events, so if the direct HTTP wait is interrupted, the phone can still show the transcript/reply when the desktop finishes.
- Android text/voice/image HTTP failures now keep polling bridge events for a short grace period instead of acting like the reply is permanently lost.
- Fixed a Java compile issue in the Android debug-details string assembly where a duplicated chained string line was left after a semicolon.

## Intended effect

Foreground replies should feel reliable: background girlfriend/Star initiative checks may still happen while exploring, but they should not block player replies or force a desktop restart. If Wi-Fi/Android aborts the direct HTTP socket, the response can still arrive through the event channel.
