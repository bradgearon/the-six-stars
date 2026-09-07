# Desktop continuous TTS playback fix

The desktop qwentts.cpp path was receiving raw 24 kHz mono PCM in small WAV-wrapped transport windows, but each window was being sent through `PlayWavAsync` independently. That reopened/stopped NAudio output for every packet and the ordered streaming pipeline also inserted an explicit 35 ms delay after every packet. The result was audible ~50 ms gaps / gated, choppy speech even though TTS generation itself was streaming correctly.

This update adds an optional continuous playback session API and a NAudio implementation backed by one `BufferedWaveProvider` and one output device for the lifetime of a spoken response. qwentts.cpp PCM windows are decoded and appended to that rolling provider; the output remains open across packet boundaries and is drained once at the end. The old per-WAV path remains as a fallback for non-PCM/non-streaming audio and for hosts that do not implement the continuous interface.

Updated paths:
- `Wds.SixStars.Api/PublicApi/Interfaces/Voice/IWavePlaybackEndpoint.cs`
- `Wds.SixStars.Api/Voice/StreamingStarVoiceResponsePipeline.cs`
- `Wds.SixStars.Api/Voice/LiveStarVoiceResponsePipeline.cs`
- `Wds.SixStars.Ui/Audio/NaudioWavePlaybackEndpoint.cs`
- `Wds.SixStars.Ui/Audio/VoiceWavArchivePlaybackEndpoint.cs`
- `Wds.SixStars.Ui/ViewModels/MainViewModel.cs` (story-prefetch playback)

The explicit 35 ms inter-packet desktop delays were removed. Live replies, normal streaming replies, direct Speak-as-Star playback, and prefetched story PCM playback now all use one rolling desktop output stream when qwentts.cpp sends PCM windows.
