# Android multiplexed WebSocket bridge

## Why this changed

The Android Activity previously opened repeated HTTP connections and polled the desktop event queue every 450 ms. It also performed periodic health/state requests while foreground chat was active. That produced unnecessary sockets, JSON parsing, executor work, and opportunities for foreground UI updates to wait behind maintenance traffic.

## New transport

The Activity now maintains one persistent WebSocket at:

```text
/api/mobile/socket
```

The same desktop `TcpListener` performs the RFC 6455 upgrade, so there is no second server or port. The existing binary phone-audio playback socket remains separate because it already streams WAV data efficiently and avoids wrapping generated speech in JSON/base64.

## Request correlation

Each Android request has:

- a positive integer `requestId`
- an `operation` (`message`, `audio`, `dispatch`, `state`, `health`, or `stars`)
- a JSON `payload`

Android stores pending work in both:

- an ordered request queue, which serializes calls to `WebSocket.send()`
- a concurrent dictionary keyed by `requestId`, whose value owns a `CompletableFuture<JSONObject>`

Responses can complete independently and are routed directly to the matching future. The UI continuation for that request is triggered without an event poll.

## Event delivery and reconnects

Dispatcher events are pushed live over the socket. Event publication is serialized with sequence assignment so live clients see deterministic ordering. On reconnect Android subscribes after its last accepted sequence and the desktop replays any retained missed events. Android deduplicates by sequence.

Requests that were accepted by a failed socket are not automatically resent because doing so could create a second LLM response. Their direct promises fail, while a completed desktop response can still be recovered through the sequenced event stream. Requests that had not reached `WebSocket.send()` remain queued for reconnect.

## Android efficiency cleanup

- Removed the 450 ms foreground event polling loop.
- Removed repeated foreground HTTP message/audio/dispatch/state traffic.
- OkHttp ping/reconnect owns liveness; a low-frequency guard only restarts a missing socket and does not send maintenance HTTP/JSON traffic.
- Tiny text, command, and initiative requests are sent directly through the non-blocking socket instead of first entering another executor queue.
- Android View values are captured on the UI thread before photo/audio encoding workers run.
- One shared OkHttp client owns the WebSocket, connection resources, and 20-second keepalive pings.
- Media encoding remains off the UI thread.
- Automatic background initiative remains a rare independent service request rather than holding a second permanent socket open.

## Latency diagnostics

Every successful direct response now adds:

- `clientQueueMs`
- `clientSocketRoundTripMs`
- `clientAfterSendMs`

The Android debug panel compares those values with:

- `ServerElapsedMs`
- `RuntimeElapsedMs`
- `GateWaitMs`

If `RuntimeElapsedMs` is still the dominant number, the remaining delay is model inference/prompt processing rather than Android transport. If `clientSocketRoundTripMs - ServerElapsedMs` is large, the remaining delay is on the phone/network/UI side.

## Compatibility

The previous HTTP routes remain in the desktop bridge for the background service, diagnostics, and older clients. The foreground Android Activity no longer uses them for event polling or normal conversation requests.
