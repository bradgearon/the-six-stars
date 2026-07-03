# Voice Clone Setup Checklist

1. Open the Pinokio Voice Clone Studio **Voice Design** tab.
2. For each Star, paste the matching **Voice Design Instructions** from `docs/Six-Stars-Voice-Design-Prompts.md`.
3. Paste the matching **Reference Text**.
4. Generate the voice and listen.
5. Save/download the sample as the configured filename, for example `elusiaa_clone.wav`.
6. Copy each saved WAV into:

```text
src/Wds.SixStars.Api/Content/VoiceClones/
```

7. Verify each Star JSON has the matching fields:

```json
{
  "voiceId": "elusiaa-dev",
  "voiceCloneAudioFile": "elusiaa_clone.wav",
  "voiceCloneRefText": "exact text used to generate the saved sample"
}
```

8. Run the app with TTS pointed at your local Qwen/Pinokio-compatible speech endpoint.

Recommended `src/Wds.SixStars.Ui/appsettings.json` TTS section:

```json
"TTS": {
  "EndpointUrl": "http://localhost:8100/v1/audio/speech",
  "Model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
  "ApiKey": null,
  "PayloadShape": "OpenAiSpeech",
  "TaskType": "VoiceClone",
  "DefaultLanguage": "English"
}
```

PowerShell overrides:

```powershell
$env:SIX_STARS_TTS_ENDPOINT = "http://localhost:8100/v1/audio/speech"
$env:SIX_STARS_TTS_MODEL = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
$env:SIX_STARS_TTS_PAYLOAD_SHAPE = "OpenAiSpeech"
$env:SIX_STARS_TTS_TASK_TYPE = "VoiceClone"
$env:SIX_STARS_TTS_LANGUAGE = "English"
```

If Pinokio exposes a different URL, keep the same fields but replace `EndpointUrl` with the URL/route shown in the Pinokio app or its terminal logs. If it is a raw Gradio UI without `/v1/audio/speech`, run/adapt the `scripts/qwen2_audio_tts_server.py` wrapper or add a new payload adapter in `Qwen3TtsVoiceGenerator`.
