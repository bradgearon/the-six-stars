# Standalone Android production UI + local TTS + reasoning/review reliability update

This update intentionally converges the standalone Android companion back onto the already-shown/recognized thin-client production experience instead of maintaining a second visual language.

## Production UI parity contract

`android/six-stars-standalone/app/src/main/res/layout/activity_main.xml` is a direct copy of `android/six-stars-mobile/app/src/main/res/layout/activity_prod_companion.xml`, with only the custom `PixelStarAnimationView` package name changed to the standalone package.

The standalone app also carries the same production background, title/talk/character frames, Star idle sprites, action icons and Star animation assets. Main visible controls remain declarative XML so Android Studio Design/Split can edit the layout visually.

Behavior wired to the production controls:

- Cat avatar/frame -> standalone Last Cat Console.
- Star title/name/subtitle -> Star selector.
- Star stage/sprite -> Star selector.
- Talk -> start local recording.
- While recording -> Talk becomes Send Voice and the production Cancel/Send Voice row is visible.
- Cancel -> discard local recording; no ASR/LLM submission.
- Send Voice -> stop recording, Groq ASR, then foreground conversation.
- Empty ASR transcript -> do not submit a foreground turn.
- Text button -> production text composer.

The Last Cat Console is declared in `dialog_companion_console.xml`; its runtime trace is inside a dedicated scroll view and can show a large tail of `standalone-runtime.log` for diagnosis. The Star selector is also an XML-declared popup.

## Google/Gemma thought output

The standalone Google client does not need model thought/reasoning text as application output. The standalone Google client now omits `thinkingConfig` entirely for every Star conversation/review request, and the copied templates never inject Gemma 4's `<|think|>` trigger token. The client also independently filters any returned candidate part marked `thought=true` before speech, JSON parsing, memory review or UI display. `ThinkingLevel` remains in settings only as a backwards-compatible/diagnostic field and is not currently sent by the standalone client.

This matters for correctness as well as presentation: thought text must never be concatenated in front of a structured background-review JSON object.

No `maxOutputTokens` cap was reintroduced.

## Protected background review recovery

The primary memory review is still non-cancelable and the next foreground still honors the canonical ordering invariant:

1. foreground response reaches EOS;
2. pending primary review is persisted;
3. protected review completes and memory/state commits;
4. next foreground is allowed to build its prompt.

A failed review no longer leaves a permanently poisoned completed-exceptionally barrier. The pending review remains persisted and is retried before the next foreground. The Cat console also exposes a manual `Retry Memory Review` action for debugging.

This is a recovery mechanism, not a memory bypass.

## Preferred local TTS boundary

Standalone Android should not send a copied/relative `voiceCloneAudioFile` to `scripts/websocket_tts_server.py`. The Python host intentionally requires an absolute host-local path.

The desktop mobile bridge now exposes:

`POST /api/mobile/local-tts`

Body:

```json
{
  "starId": "Elusiaa",
  "text": "A sentence to speak."
}
```

The .NET desktop handler:

1. loads the canonical desktop Star profile;
2. creates the normal live voice directive;
3. resolves the clone file through `ResolveVoiceCloneAudioPath`/`PrepareVoiceCloneReferenceForTts`;
4. builds the normal `VoiceSynthesisRequest`;
5. forces the normal local Qwen WebSocket generator even if desktop hosted TTS is enabled;
6. returns the generated WAV to Android.

Android prefers `TTS.LocalServiceUrl` over the old `TTS.LocalWebSocketUrl`. Therefore the preferred LAN config is:

```json
"TTS": {
  "Mode": "local",
  "LocalServiceUrl": "http://192.168.1.179:8066/api/mobile/local-tts"
}
```

The old direct Python WebSocket route is retained only as a compatibility/developer path.

## State/import invariant

None of these UI/service reliability changes alter canonical Star relationship, deterministic emotion/memory, profile, short-term-memory, story, or import semantics. The standalone/game state compatibility contract remains authoritative.

## Latest-source merge safety

This update was re-applied on top of the user's latest installable standalone Android source rather than replacing that tree with an older checkpoint. In particular:

- the Android Studio Image Asset-generated launcher resources and manifest icon references are preserved;
- the user's `AndroidPromptBuilder` checked-`JSONException` fix is preserved;
- the user's `JsonUtil.writeObject` checked-`JSONException` signature fix is preserved;
- existing `standalone-services*.json` files are not overwritten by this merge.

This matters because the current development repository intentionally contains working service credentials. Those files should remain untouched until the separate secure-settings/proxy migration is done.
