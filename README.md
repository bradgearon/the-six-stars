# Six Stars Desktop Dev Harness

This is a starter solution for the Six Stars AI companion runtime.

## Projects

- `Wds.SixStars.Api` — main runtime library. Keep Unity-safe contracts here.
- `Wds.SixStars.Ui` — WPF desktop dev harness. Builds to `six-stars.exe` because the project assembly name is set to `six-stars`.

## Build on Windows

Install .NET 8 SDK, then run:

```powershell
dotnet restore .\SixStars.sln
dotnet build .\SixStars.sln
```

Run the desktop harness:

```powershell
dotnet run --project .\src\Wds.SixStars.Ui\Wds.SixStars.Ui.csproj
```

Publish a single Windows exe:

```powershell
dotnet publish .\src\Wds.SixStars.Ui\Wds.SixStars.Ui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The publish folder will contain `six-stars.exe`.

## Current prototype behavior

The UI supports:

- Text input.
- Star selector for Elusiaa, Kaelia, Maera, Khavia, Auerlis, and Nyxari.
- Request type selector: Chat, Story, Letter, BattleUpdate, Debug.
- Quick test buttons for story, goblin/harpy discernment, cruel escalation, and kind company.
- Spoken output panel.
- Debug JSON / Unity payload panel.

The API supports:

- `SixStarsRuntime` as the main entry point.
- `IStarAgent` interface for swapping the mock agent with an LLM-backed agent.
- 32 named emotion scores per Star (27 general + 5 romance-layer emotions).
- 0-100 emotion scores with confidence.
- deterministic relationship delta calculation.
- Unity event payloads.
- story memory cards.
- wake alias configuration placeholders.

## Next implementation steps

1. Replace `MockStarAgent` with an LLM-backed implementation.
2. Persist `StarState` to SQLite or JSON instead of `InMemoryStarStateStore`.
3. Move hardcoded Star profiles into editable JSON files.
4. Add microphone capture, VAD, ASR, fuzzy wake alias detection, and TTS.
5. Create a Unity package that references or mirrors the `Wds.SixStars.Api.Models` contracts.

## Important design rule

The model interprets emotions, stories, and intent. The app code calculates relationship deltas.

That keeps Elusiaa and Kaelia expressive while keeping the actual game state deterministic and testable.

## Speech / hotword / Whisper pipeline

The reusable speech code lives in `Wds.SixStars.Api`, not in the WPF UI.

Current intended loop:

1. Unity/WPF/NAudio/WASAPI pushes ~100 ms PCM16 chunks into `WhisperSpeechInputPipeline`.
2. `RmsSpeechActivityDetector` watches for any sound above threshold.
3. After `MinSoundMs` (default 100 ms), the pipeline starts an utterance candidate.
4. Rolling probe windows are sent to `IAudioHotwordDetector` while the full utterance keeps buffering. The first probe can happen at `MinSoundMs`, then repeats every `HotwordProbeEveryMs` until `HotwordMaxSearchMs`.
5. The utterance keeps collecting 100 ms chunks until `SilenceEndMs` of silence.
6. The capture lock only copies the finished segment and resets active capture state. Hotword awaits, Whisper awaits, and transcript events run after the lock is released.
7. If any hotword probe accepted an equipped Star name, the whole captured utterance is sent to `IWhisperSpeechStreamer`.
8. The Whisper transcript becomes a `StarInteractionRequest` through `SpeechToStarCoordinator`.
9. After the Star responds, `StarVoiceResponsePipeline` calls the local TTS model and submits the returned WAV to an `IWavePlaybackEndpoint`. It notifies the speech pipeline when playback starts/completes so the mic can mute itself and avoid self-listening.
10. The speech pipeline enters conversation mode so follow-up speech is transcribed without repeating the hotword.
11. If speech stops for `ConversationIdleMs`, it returns to hotword detection mode.

New API pieces:

- `Audio/PcmAudioFormat.cs` — shared PCM format contract, defaulting to 16 kHz mono PCM16.
- `Audio/PcmAudioChunk.cs` — small audio frame that can come from Unity, WASAPI, NAudio, or tests.
- `Audio/RmsSpeechActivityDetector.cs` — dependency-free starter VAD.
- `Audio/PcmAudioUtilities.cs` — Unity float-sample-to-PCM16 helper and test signal helpers.
- `Speech/IAudioHotwordDetector.cs` — abstraction for a lightweight local hotword model.
- `Speech/MockAudioHotwordDetector.cs` — dev/test hotword detector.
- `Speech/IWhisperSpeechStreamer.cs` — abstraction for local Whisper / whisper.cpp / Whisper.net.
- `Speech/WhisperSpeechInputPipeline.cs` — continuous listener pipeline with hotword mode and conversation mode.
- `Speech/UnityMicrophoneSpeechAdapter.cs` — Unity-friendly float sample adapter.
- `Speech/SpeechToStarCoordinator.cs` — bridge from Whisper transcript -> `SixStarsRuntime.ProcessAsync`.
- `Speech/MockWhisperSpeechStreamer.cs` — dev/test streamer used by the WPF harness.
- `Speech/LocalWhisperHttpSpeechStreamer.cs` — starter reference HTTP adapter for a local Whisper sidecar.
- `Voice/IStarVoiceGenerator.cs` — abstraction for the local voice generation model.
- `Voice/IWavePlaybackEndpoint.cs` — abstraction for a WAV playback endpoint in desktop or Unity.
- `Voice/IStarSpeechPlaybackObserver.cs` — small observer contract used to notify the speech pipe that generated Star audio has started/completed.
- `Voice/StarVoiceResponsePipeline.cs` — Star text -> TTS WAV -> playback endpoint, with self-listening notifications.

The WPF harness has a Speech / Whisper Pipe section. Because no real microphone adapter is attached yet, use **Simulate Speech Through Whisper Pipe** to push fake PCM through the same pipeline Unity will use. The mock streamer returns the current Player Text as the transcript, then routes it into the selected Star and runs the mock voice pipeline.

Unity integration shape:

```csharp
var settings = new VoiceCaptureSettings
{
    RequireHotwordWhenIdle = true,
    MinSoundMs = 100,
    HotwordProbeMs = 700,
    HotwordProbeEveryMs = 300,
    HotwordMaxSearchMs = 1800,
    SilenceEndMs = 900,
    ConversationIdleMs = 15000
};

var pipeline = new WhisperSpeechInputPipeline(
    whisperStreamer,
    audioHotwordDetector,
    new RmsSpeechActivityDetector(),
    settings,
    new WhisperTranscriptionOptions());

// Pass `pipeline` into StarVoiceResponsePipeline as an IStarSpeechPlaybackObserver
// so input is muted while generated Star audio is playing.
var voicePipeline = new StarVoiceResponsePipeline(
    voiceGenerator,
    wavePlaybackEndpoint,
    pipeline);

pipeline.WakeAliases = wakeAliases;
pipeline.PreferredStarId = "Elusiaa";
await pipeline.StartListeningAsync(cancellationToken);

var unityAdapter = new UnityMicrophoneSpeechAdapter(pipeline);

// Each Unity microphone frame, ideally around 100 ms:
await unityAdapter.PushUnityFloatSamplesAsync(
    floatSamples,
    sampleCount,
    sampleRateHz,
    channels,
    "unity-microphone",
    cancellationToken);
```

The key separation is: Unity or desktop owns microphone capture; `Wds.SixStars.Api` owns speech segmentation, hotword gating, Whisper streaming contracts, transcript events, Star routing, voice-generation contracts, and WAV playback contracts.

## Structured local LLM response layer

The API now has a structured-output agent path for local Gemma, DeepSeek, LM Studio, Ollama-compatible sidecars, or any OpenAI-compatible chat endpoint.

New API pieces:

- `Ai/MainInstructionSet.cs` — main behavior and task instruction contract loaded from JSON.
- `Ai/InstructionLibrary.cs` — loads `Content/Instructions/main-instructions.json` and Star JSON files.
- `Ai/OpenAiCompatibleChatModelClient.cs` — posts chat requests to a local OpenAI-compatible endpoint.
- `Ai/StructuredStarAgent.cs` — asks the local model for strict JSON and maps it to `StarInteractionResponse`.
- `Ai/FallbackStarAgent.cs` — falls back to `MockStarAgent` if the local model fails.
- `Ai/StarResponseJsonSchema.cs` — structured-output schema requiring spoken text, state suggestions, memory updates, and all emotion scores.
- `Stars/FileStarProfileProvider.cs` — loads each Star from `Content/Stars/*.json` instead of hardcoding them.
- `Screen/ScreenCaptureSummary.cs` and `Screen/InMemoryScreenContextStore.cs` — retain the last five screen summaries per session so a Star can comment on the user's work context.

Content files:

```text
src/Wds.SixStars.Api/Content/Instructions/main-instructions.json
src/Wds.SixStars.Api/Content/Stars/elusiaa.json
src/Wds.SixStars.Api/Content/Stars/kaelia.json
src/Wds.SixStars.Api/Content/Stars/maera.json
src/Wds.SixStars.Api/Content/Stars/khavia.json
src/Wds.SixStars.Api/Content/Stars/auerlis.json
src/Wds.SixStars.Api/Content/Stars/nyxari.json
```

The required emotions are now the named Six Stars list:

```text
Happy, Amused, Playful, Comforted, Safe, Protected, Trusting, Respected,
Admiring, Affectionate, Vulnerable, Hopeful, Wondrous, Intrigued, Impassioned,
Determined, Concerned, Confused, Unsure, Frustrated, Angry, Hurt, Sad,
Melancholy, Lonely, Guilty, Suspicious, Flattered, Attracted, Tender, Yearning,
Jealous
```

The last five are the optional romance-layer emotions. The model returns scores and confidence for all emotions. The model also returns `suggestedStateDeltas`; the runtime still calculates final `StateDeltas` using deterministic rules so the game remains testable.

### Enabling a local model

By default, the WPF harness still uses `MockStarAgent`. To enable the structured local LLM path, set environment variables before launching:

```powershell
$env:SIX_STARS_LLM_ENDPOINT = "http://localhost:1234/v1/chat/completions"
$env:SIX_STARS_LLM_MODEL = "gemma-local-or-deepseek-local"
# Optional, only if your sidecar requires it:
$env:SIX_STARS_LLM_API_KEY = "local-key"
# Set to false if your local server does not support json_schema response_format:
$env:SIX_STARS_LLM_JSON_SCHEMA = "false"

dotnet run --project .\src\Wds.SixStars.Ui\Wds.SixStars.Ui.csproj
```

If the endpoint fails or the response cannot be parsed, `FallbackStarAgent` uses the deterministic mock agent and adds a debug note.

### Structured response shape

The model is asked to return JSON containing:

- `spokenText` — exact text to speak through the Star voice.
- `storyTitle`, `storyKind`, `canonSourcesUsed` — story metadata when applicable.
- `emotionalPurpose` — why she responded this way.
- `emotionScores` — all 32 named emotions, each with score, confidence, and reason.
- `detectedCombos` — e.g. `affection_plus_wisdom`, `cruel_escalation`, `screen_awareness_company`.
- `suggestedStateDeltas` — model recommendation only.
- `conversationSummary` — durable summary used in future prompts.
- `changesSummary` — what changed because of this turn.
- `importantPlayerQuotes` — exact player quotes she should remember.
- `memoryNotes` — short durable facts for future story and relationship continuity.
- `secretsUnlocked`, `loreUsed`, `riskFlags`, `wisdomScore`.

### Screen context retention

Use `SixStarsRuntime.AddScreenCaptureAsync(sessionId, capture, token)` to retain a screen summary. The runtime automatically injects the latest five summaries into future `StarInteractionRequest`s for that session.

The recommended design remains: screenshot images are summarized by a local vision model first; the Star model receives safe summaries and optional image references, not raw private screen contents unless you intentionally wire that in.

For UI/Unity code that wants a smaller surface, use `SixStarsConversationApi`:

```csharp
await conversationApi.AddScreenCaptureSummaryAsync(sessionId, screenSummary, token);
StarInteractionResponse response = await conversationApi.SendPlayerTextAsync(
    sessionId,
    "Elusiaa",
    playerText,
    RequestType.Chat,
    gameContext,
    storyOptions,
    token);

StarInteractionResponse voiceResponse = await conversationApi.SendSpeechTranscriptAsync(
    sessionId,
    "Elusiaa",
    whisperTranscript,
    RequestType.Chat,
    gameContext,
    storyOptions,
    token);
```

## Qwen3-TTS voice output layer

The structured Star response now includes a `voiceDirective` object. This is the bridge between the Star LLM and Qwen3-TTS.

The Star model produces:

- `spokenText` — the exact line to speak.
- `voiceDirective.voiceDesignPrompt` — stable voice identity/persona description.
- `voiceDirective.deliveryInstructions` — how this specific line should be performed.
- `voiceDirective.primaryEmotion` and `voiceDirective.emotionIntensity` — the dominant performance emotion.
- `voiceDirective.pace`, `pitch`, `volume`, `prosody`, `language`, `audioTags`, and `avoid`.

New API pieces:

- `Models/VoiceDirective.cs` — structured performance contract returned by the Star model.
- `Voice/Qwen3TtsOptions.cs` — configurable local Qwen3-TTS endpoint settings.
- `Voice/Qwen3TtsInstructionBuilder.cs` — merges Star voice identity + per-line delivery into one TTS instruction string.
- `Voice/Qwen3TtsVoiceGenerator.cs` — HTTP adapter for a local Qwen3-TTS server.
- `Voice/StarVoiceResponsePipeline.cs` — now uses `response.VoiceDirective` instead of only guessing from emotion scores.

The default adapter sends an OpenAI-compatible speech request like:

```json
{
    "model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
  "input": "Little one... I will stay near while you work.",
  "voice": "elusiaa-dev",
  "instructions": "Voice identity: ... Performance: gentle, warm, protective...",
  "response_format": "wav",
  "speed": 1.0,
    "task_type": "VoiceClone",
    "voice_clone_audio": "elusiaa_clone.wav",
    "voice_clone_ref_text": "Little one, stay close to the light. Mercy is how we remember we are not gods."
}
```

If your local sidecar uses a simpler shape, set:

```powershell
$env:SIX_STARS_TTS_PAYLOAD_SHAPE = "QwenSimple"
```

which sends `inputs`, `voice`, `instructions`, and `language`.

### Enabling Qwen3-TTS in the WPF harness

By default, the desktop harness still uses `MockStarVoiceGenerator`. To call Qwen3-TTS:

```powershell
$env:SIX_STARS_TTS_ENDPOINT = "http://localhost:8091/v1/audio/speech"
$env:SIX_STARS_TTS_MODEL = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
$env:SIX_STARS_TTS_TASK_TYPE = "VoiceClone"
$env:SIX_STARS_TTS_LANGUAGE = "Auto"
# Optional, if your server needs it:
$env:SIX_STARS_TTS_API_KEY = "local-key"
```

The pipeline is now:

```text
Star structured model response
→ spokenText + voiceDirective
→ StarVoiceResponsePipeline
→ Qwen3TtsInstructionBuilder
→ Qwen3TtsVoiceGenerator
→ VoiceSynthesisResult WAV
→ IWavePlaybackEndpoint
```

The current WPF playback endpoint is still `NullWavePlaybackEndpoint`, so it receives the WAV but does not play through speakers yet. Replace it with a WPF/NAudio playback endpoint or a Unity playback endpoint when you are ready.
- `changesSummary` — what changed this turn.
- `importantPlayerQuotes` — exact quotes that affected the Star, unlocked secrets, changed romance, or taught her something.
- `memoryNotes` — durable notes for future story/companion behavior.
- `secretsUnlocked` — newly unlocked lore/secrets.
- `learnedFunctions` — durable lessons, functions, rituals, meanings, or preferences the player taught the Star.
- `relationshipEvents` — milestone or combo history entries.
- `girlfriendHistoryUpdates` — durable girlfriend/romance route history.
- `petNamesPlayerUsesForStar` and `petNamesStarUsesForPlayer` — pet names learned during interaction.
- `voiceDirective` — Qwen3-TTS performance instructions for the generated speech.

## Star memory / relationship state API

A new state API owns durable Star memory separately from the chat model. The runtime now follows this loop:

```text
UI / Unity / Speech transcript
→ SixStarsRuntime asks StarMemoryApi for latest StarState + recent history
→ prompt is built from Star JSON + main instructions + durable state + recent screen summaries
→ local LLM returns structured response
→ deterministic delta calculator computes actual game deltas
→ StarMemoryApi updates state, combos, history, secrets, learned functions, quotes, girlfriend history
→ response returns Unity events and debug data
```

New pieces:

- `State/IStarMemoryApi.cs` — reusable state/memory API contract.
- `State/StarMemoryApi.cs` — applies interactions to a Star's durable state.
- `State/StarStateUpdateRequest.cs` — internal update object created after each Star response.
- `State/StarInteractionHistoryPage.cs` — paged history for UI/debug/Unity.
- `Models/SixStarsSaveData.cs` — Unity-save-friendly chunk containing all Star states.
- `Models/StarLearnedFunction.cs` — anything the player taught the Star.
- `Models/StarSecretState.cs` — structured secret/lore unlock state.
- `Models/RelationshipComboState.cs` — repeated social/emotional combo tracking.
- `Engine/JsonStarStateStore.cs` — desktop JSON persistence store.

Runtime/facade methods:

```csharp
Task<StarState> GetStarStateAsync(string sessionId, string starId, CancellationToken ct);
Task<StarInteractionHistoryPage> GetStarInteractionHistoryAsync(string sessionId, string starId, int skip, int take, CancellationToken ct);
Task<SixStarsSaveData> ExportSaveDataAsync(string sessionId, CancellationToken ct);
Task ImportSaveDataAsync(SixStarsSaveData saveData, CancellationToken ct);
```

For the desktop harness, state is saved as JSON under the user's local app data folder:

```text
%LOCALAPPDATA%/Wds/SixStars/six-stars-local-dev-state.json
```

Unity can either embed `SixStarsSaveData` directly into its save file or replace `JsonStarStateStore` with a Unity-backed `IStarStateStore` that reads/writes the same model objects.

Important state behavior:

- The LLM may suggest relationship deltas, but deterministic code still applies the actual deltas by default.
- `StarMemoryOptions.AcceptModelSuggestedDeltas` exists for testing if you want the memory API to accept model deltas instead.
- The memory API always logs the full player text and Star response for each interaction.
- It automatically preserves the player's exact statement when the emotional impact is high, a secret unlocks, or a strong relationship delta happens.
- The prompt builder sends back the running summary, recent history, exact remembered quotes, secrets, learned functions, combos, pet names, and girlfriend history before each model request.

## Public API folder organization

The reusable call surface has been moved into `src/Wds.SixStars.Api/PublicApi` so Unity, the desktop harness, and future hosts have one obvious place to look for integration points.

Namespaces were **not** changed. This was intentional so existing code can keep using namespaces such as `Wds.SixStars.Api.Engine`, `Wds.SixStars.Api.Speech`, `Wds.SixStars.Api.Voice`, and `Wds.SixStars.Api.State` while the files are physically easier to find.

Public API layout:

```text
PublicApi/
  README.md
  Facades/
    SixStarsConversationApi.cs
  Adapters/
    Speech/
      UnityMicrophoneSpeechAdapter.cs
  Interfaces/
    Ai/ILocalChatModelClient.cs
    Audio/ISpeechActivityDetector.cs
    Audio/IWakeWordDetector.cs
    Engine/IStarAgent.cs
    Engine/IRelationshipDeltaCalculator.cs
    Engine/IStarStateStore.cs
    Screen/IScreenContextStore.cs
    Speech/IAudioHotwordDetector.cs
    Speech/IWhisperSpeechStreamer.cs
    Stars/IStarProfileProvider.cs
    State/IStarMemoryApi.cs
    Voice/IStarSpeechPlaybackObserver.cs
    Voice/IStarVoiceGenerator.cs
    Voice/IWavePlaybackEndpoint.cs
```

Default implementations still live in their feature folders, such as `Speech/WhisperSpeechInputPipeline.cs`, `Voice/Qwen3TtsVoiceGenerator.cs`, `State/StarMemoryApi.cs`, and `Ai/StructuredStarAgent.cs`.

## Desktop wiring that is now attached

The WPF harness now instantiates the real desktop pieces around the reusable API layer:

```text
MainViewModel
  -> SixStarsRuntime
      -> FileStarProfileProvider
      -> StructuredStarAgent or MockStarAgent
      -> RelationshipDeltaCalculator
      -> JsonStarStateStore
      -> InMemoryScreenContextStore

  -> WhisperSpeechInputPipeline
      -> LocalWhisperHttpSpeechStreamer or MockWhisperSpeechStreamer
      -> MockAudioHotwordDetector for now
      -> RmsSpeechActivityDetector
      -> VoiceCaptureSettings

  -> NaudioMicrophoneSpeechSource
      -> records 16 kHz mono PCM16 in 100 ms buffers
      -> pushes chunks into WhisperSpeechInputPipeline

  -> StarVoiceResponsePipeline
      -> Qwen3TtsVoiceGenerator or MockStarVoiceGenerator
      -> NaudioWavePlaybackEndpoint
      -> notifies WhisperSpeechInputPipeline when playback starts/completes

  -> ScreenAwarenessLoop
      -> DesktopScreenCaptureService
      -> OpenAiCompatibleVisionScreenSummarizer or MockScreenSummarizer
      -> InMemoryScreenContextStore
```

### Microphone listening

Click **Start Listen** in the desktop harness. The UI starts both:

1. `WhisperSpeechInputPipeline.StartListeningAsync(...)`
2. `NaudioMicrophoneSpeechSource.StartAsync(...)`

NAudio captures roughly 100 ms chunks and feeds the API:

```csharp
await _speechPipeline.PushPcm16Async(
    buffer,
    buffer.Length,
    PcmAudioFormat.Mono16Khz(),
    "wpf-naudio-microphone",
    cancellationToken);
```

The speech pipe handles VAD, hotword probing, full-utterance capture, Whisper transcription, conversation mode, and self-listening prevention.

### Screen awareness

Click **Start Screen** to begin periodic screen awareness. It captures immediately, then every 5 minutes. Each capture is summarized and stored in the latest-five screen context store. `SixStarsRuntime` automatically injects recent screen summaries into the next Star request.

Click **Capture Now** to force an immediate desktop capture and summary.

The screen summarizer is mock by default. To use a local vision model that accepts OpenAI-compatible image chat requests:

```powershell
$env:SIX_STARS_SCREEN_LLM_ENDPOINT = "http://localhost:1234/v1/chat/completions"
$env:SIX_STARS_SCREEN_LLM_MODEL = "qwen-vl-local-or-other-vision-model"
# Set to false if the endpoint does not support response_format json_object.
$env:SIX_STARS_SCREEN_LLM_JSON_SCHEMA = "false"
```

### Whisper endpoint

The WPF app uses `MockWhisperSpeechStreamer` unless `SIX_STARS_WHISPER_ENDPOINT` is configured:

```powershell
$env:SIX_STARS_WHISPER_ENDPOINT = "http://localhost:8080/inference"
$env:SIX_STARS_WHISPER_PROVIDER = "local-http"
```

`LocalWhisperHttpSpeechStreamer` posts a WAV as multipart form data to the endpoint and treats the response body as the transcript text. Adjust that adapter if your whisper.cpp/Whisper.net sidecar returns JSON.

### Qwen3-TTS playback

The WPF app now uses `NaudioWavePlaybackEndpoint`, so a returned WAV is actually played through the default Windows audio device. To enable Qwen3-TTS:

```powershell
$env:SIX_STARS_TTS_ENDPOINT = "http://localhost:8091/v1/audio/speech"
$env:SIX_STARS_TTS_MODEL = "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice"
$env:SIX_STARS_TTS_TASK_TYPE = "VoiceDesign"
$env:SIX_STARS_TTS_LANGUAGE = "Auto"
```

The voice pipeline calls `NotifyStarSpeechStarted()` and `NotifyStarSpeechCompleted()` on the speech pipeline so the app does not transcribe its own generated Star voice.

### Relationship/memory loop

`SixStarsRuntime.ProcessAsync(...)` already performs the state loop:

```text
get latest Star state + recent history + screen summaries
-> send structured Star request to the model
-> calculate deterministic StateDeltas from emotion scores and combos
-> StarMemoryApi.UpdateStarWithInteractionAsync(...)
-> save JSON state to %LOCALAPPDATA%/Wds/SixStars/six-stars-local-dev-state.json
```

That means text input, speech transcription, and Unity calls all flow through the same relationship/memory update path.

### Audio hotword endpoint

The desktop harness uses `MockAudioHotwordDetector` unless `SIX_STARS_HOTWORD_ENDPOINT` is configured. The local hotword adapter sends short rolling WAV probes plus the wake-alias JSON to your detector:

```powershell
$env:SIX_STARS_HOTWORD_ENDPOINT = "http://localhost:8090/detect-hotword"
$env:SIX_STARS_HOTWORD_PROVIDER = "local-http"
```

Expected response can be JSON:

```json
{ "detected": true, "starId": "Elusiaa", "matchedAlias": "elusiaa", "confidence": 0.92 }
```

or simple plain text such as `Elusiaa`, `Kaelia`, or `none`.


## Services launcher note

`run-ai-companion.cmd` is the normal full companion launcher again: it uses the integrated/prebuilt engine launcher (`redist\six-stars-launcher.exe` when built, or the launcher project fallback) to start the local engine services and open the desktop app. Use `run-ai-services.cmd` when you want services-only with the prebuilt engine EXEs, then open the UI separately with `launch-ui-only.cmd`. Use `run-ai-source-services.cmd` or `python scripts\start_services.py` for the repo `.venv` Python debug flow.


### Launcher split: prebuilt app vs venv services

- `run-ai-companion.cmd` launches the normal integrated/prebuilt-engine companion app path. It calls `launch.cmd`, which prefers `redist\six-stars-launcher.exe` and falls back to the .NET launcher project for development builds.
- `run-ai-services.cmd` is services-only and keeps the desktop app closed. It uses the prebuilt engine EXEs from `config/runtime-manifest.json` through `launch.cmd --skip-install --no-ui`.
- `launch-ui-only.cmd` opens only the desktop UI after services are already running.
- `run-ai-companion-with-ui.cmd` is the dev/source flow that starts `run-ai-source-services.cmd` in a second window and then opens the UI.


## Launcher modes updated: prebuilt app vs source services

- `run-ai-companion.cmd` is the normal full prebuilt companion launcher. It starts the integrated engine services and opens the desktop app.
- `run-ai-services.cmd` is services-only for the prebuilt engine EXEs. It calls `launch.cmd --skip-install --no-ui` and does not launch the UI.
- `launch-ui-only.cmd` opens the desktop UI after services are already running.
- `run-ai-source-services.cmd` is the Python source/venv debug helper for ASR/TTS. It calls `start.cmd`, which activates `.venv` and then runs `python scripts\start_services.py`.
- Manual Python debug flow is still supported: activate `.venv` yourself and run `python scripts\start_services.py`.

## CUDA-only local AI policy

This build is configured to use GPU/CUDA for local LLM, ASR, and TTS. CPU fallback is disabled in `config/runtime-manifest.json` under `gpuPolicy`. If CUDA is not available, services fail fast and tell you to configure NVIDIA CUDA engines or use a hosted service option.

The Qwen TTS server has been restored to the previous parallel streaming behavior. Android and desktop clients subscribe for WAV chunks; they do not receive GPU/model objects.

## Memory archive and information need vector

The companion now keeps prompt context bounded while preserving source memory in the Unity-compatible Star state. Recent turns remain in `MemoryLog`; turns older than two hours move into `OldConversationArchives` with summaries and source entries. The prompt-facing `ImportantPlayerQuotes` list is capped at 128, while `AllImportantPlayerQuotes` keeps the durable source records.

Fast chat responses include `needsMoreInformation` plus an `informationNeeds` vector. If a Star needs older conversation history, exact quotes, markers, lore, character data, bestiary/world data, or story history, the runtime builds a capped local archive slice and performs a second internal LLM pass before returning the final answer.

Use **Clear All Star Data** in the desktop UI or Android app to reset all local Star states.


## LLM exchange logs

The desktop runtime now appends local LLM request/response/debug entries to one plain daily log file: `logs/llm-exchanges/YYYY-MM-DD.log`. Use **Open LLM Exchange Log Folder** in Quick Actions to inspect the day's `.log` file and trim unused prompt/context sections.
