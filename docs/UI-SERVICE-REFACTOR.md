# Android and Desktop UI Service Refactor

This refactor moves device, transport, persistence, background scheduling, and mutable session work out of the two UI controllers.

## Android

`MainActivity` remains responsible for Android view construction, permission launchers, dialogs, and rendering. `MainViewModel` owns the lifecycle-safe service graph and exposes `LiveData` for connection, recording, waiting, voice playback, bridge events, and protocol diagnostics. The phone playback service uses the application context and a detachable Activity listener so rotation/recreation does not retain a dead Activity.

| Class | Responsibility | Main entry points |
|---|---|---|
| `MainViewModel` | Lifecycle-safe UI coordinator and service container | `initialize`, `selectStar`, `setWaitingForReply`, `setRecording`, `setVoicePlaying` |
| `MobileSettingsService` | All durable companion preferences | `bridgeUrl`, `selectedStarId`, `setRomanceMode`, `setPlayOnPhone`, `setArea`, `setQuest` |
| `BridgeConnectionService` | Persistent multiplexed control WebSocket | `start`, `ensureConnected`, `reconnect`, `request`, `subscribeAfter` |
| `ConversationService` | Typed control-channel operations | `sendText`, `sendAudio`, `sendPhoto`, `sendCommand`, `fetchState`, `requestInitiative`, `testAudio` |
| `VoiceCaptureService` | `AudioRecord`, recording thread, PCM accumulation, WAV packaging | `start`, `stop`, `cancel`, `wavFromPcm16` |
| `PhoneVoicePlaybackService` | TCP audio connection, stream ordering, checksum validation, saved WAV, `AudioTrack` queue | `connect`, `enqueue`, `pause`, `resume`, `playSavedVoice`, `clearForNewStream` |
| `PhotoAttachmentService` | URI loading, MIME detection, size validation | `load` |
| `PreparedSpeechCoordinator` | Prepared speech synchronization, gating, dwell and score policy | `sync`, `bestFor`, `blockFor`, `allowAfter`, `shouldAutoSpeak` |
| `StarInitiativeCoordinator` | Background initiative service start/stop and prefetched cache access | `setEnabled`, `start`, `stop`, `loadPrefetched`, `clearPrefetched` |
| `MobileBridgeEventService` | Wire event classification into typed event kinds | `parse` |
| `StarAnimationCoordinator` | Listening/thinking/talking/special animation state sequencing | `listening`, `thinking`, `beginVoice`, `endVoice`, `queueShirtOff` |

### Android thread ownership

- WebSocket callbacks: `BridgeConnectionService` / `MobileBridgeWebSocketClient`
- Microphone blocking read: `VoiceCaptureService`
- TCP phone-audio socket: `PhoneVoicePlaybackService`
- `AudioTrack` playback queue: `PhoneVoicePlaybackService`
- Prepared-speech I/O: `PreparedSpeechCoordinator` executor
- UI rendering and dialogs: `MainActivity`

## Desktop WPF

`MainViewModel` remains the WPF binding façade and orchestrates view state. The service registry owns runtime lanes, audio devices, screen awareness, diagnostics, mobile transports, initiative scheduling, and story session state.

| Class | Responsibility | Main entry points |
|---|---|---|
| `DesktopUiServiceRegistry` | Single composition/disposal boundary | `Dispose` |
| `DesktopConversationService` | Foreground runtime requests and daily refresh | `BeginOperation`, `ProcessAsync`, `TriggerDailyStateRefreshAsync` |
| `DesktopSpeechInputService` | Microphone + Whisper pipeline ordering | `StartAsync`, `StopAsync`, `CancelAsync`, `StartConversationCaptureAsync`, `RecoverAfterResumeAsync` |
| `DesktopVoiceOutputService` | Streaming or standard desktop TTS/playback | `SpeakAsync`, `PlayAsync`, `Pause`, `Resume`, `Cancel` |
| `DesktopScreenAwarenessService` | Screen capture loop lifecycle | `StartAsync`, `StopAsync`, `CaptureNowAsync` |
| `DesktopDiagnosticsService` | Conversation logs and LLM exchange folder actions | `LoadLatestConversationLog`, `CopyLatestConversationLog`, `OpenLatestConversationLog`, `OpenLlmExchangeLogFolder` |
| `DesktopStarStateService` | Met/ally/girlfriend/cute-mode and reset operations | `LoadAsync`, `SetMetAsync`, `SetAllyAsync`, `SetExclusiveGirlfriendAsync`, `SetCuteModeEnabledAsync`, `ResetAsync` |
| `DesktopStarInitiativeService` | Randomized at-rest scheduling | `Start`, `RequestNowAsync`, `Dispose` |
| `DesktopStorySessionService` | Mutable active story state | properties plus `Reset` |
| `MobileBridgeHostService` | HTTP/WebSocket bridge and TCP audio host lifetime | `Start`, `Stop` |
| `MobileConversationService` | Priority-aware Android foreground runtime lane | `TryAcquireAsync`, `AcquireAsync`, `ProcessAsync`, `ProcessWhenForegroundIdleAsync` |
| `MobileVoiceDeliveryService` | Android audio stream session and frame delivery | `CreateStreamSession`, `SendChunkAsync`, `SendControlAsync`, `SendStatusAsync`, `SendErrorAsync` |

### Desktop thread ownership

- WPF binding updates and dispatcher calls: `MainViewModel`
- Desktop microphone and Whisper pipeline: `DesktopSpeechInputService`
- Desktop TTS and playback: `DesktopVoiceOutputService`
- Screen capture loop: `DesktopScreenAwarenessService`
- At-rest timer: `DesktopStarInitiativeService`
- Android foreground runtime semaphore: `MobileConversationService`
- Android HTTP/WebSocket/TCP listeners: `MobileBridgeHostService`
- Android voice frames: `MobileVoiceDeliveryService`

## Behavioral compatibility

The wire protocol and existing request/response payloads remain unchanged. Android still sends complete WAV recordings to desktop Whisper, the desktop still sends completed structured LLM text before beginning phone TTS, and generated phone WAV chunks still travel over the dedicated ordered TCP audio channel. This refactor changes ownership and lifecycle boundaries rather than the protocol.
