# Android IPv4-only bridge and resume refresh

The Android companion now intentionally uses the configured LAN bridge URL only. Saved/discovered IPv6 promotion is disabled and legacy IPv6 preferences are cleared on startup.

This change addresses two failure modes seen after the phone sleeps:

1. The previous health bootstrap could promote the control WebSocket from IPv4 to IPv6 while another request was in flight. `MobileBridgeWebSocketClient` correctly failed promises already sent on the superseded route to avoid duplicate LLM requests, but that meant a voice request started at the wrong moment could appear to receive no reply.
2. Android may keep a TCP/WebSocket object marked connected across Wi-Fi sleep even when the underlying route is stale. `isConnected()` therefore was not sufficient immediately after screen-on.

On every resume after the first app start, the app now refreshes the idle control WebSocket to the configured bridge and refreshes the separate phone voice TCP socket once the bridge health handshake completes. In-flight bridge requests and active/queued speech are not interrupted.
