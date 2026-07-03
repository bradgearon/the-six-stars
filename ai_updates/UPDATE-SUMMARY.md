# Six Stars UI + Prompt + Voice Update

This update focuses on the normal companion loop:

- Default UI mode is now plain chat with the selected Star.
- Added a clear **Send Message** command/button that forces `RequestType.Chat`.
- Added an optional **Story Suggestion** text box.
- **Tell Me A Story** now sets `RequestType.Story` and sends the story suggestion through `StoryOptions.PlayerSuggestion`.
- The LLM prompt now has a clearer formatted acting card and voice/performance card for the selected Star.
- Personality/voice notes such as `personalityShort`, `voiceArchetype`, `speakingStyle`, `voiceStyleRules`, and `voiceGenerationRules` are explicitly described as applying both to the LLM acting response and to Qwen3-TTS performance.
- TTS voice identity injection now builds a stable voice identity from the Star profile before sending the voice generation request.
- Default WPF voice playback now uses normal HTTP `/v1/audio/speech` + `NaudioWavePlaybackEndpoint`.
- WebSocket streaming TTS is opt-in only with `WebSocketTTS.Enabled=true` or `SIX_STARS_USE_STREAMING_TTS=true`.
- Default TTS language is now `English` instead of `Auto`.

Run:

```powershell
dotnet restore .\SixStars.sln
dotnet build .\SixStars.sln
dotnet run --project .\src\Wds.SixStars.Ui\Wds.SixStars.Ui.csproj
```
