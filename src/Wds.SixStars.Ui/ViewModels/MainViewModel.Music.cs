using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.LocalModels;
using Wds.SixStars.Api.Music;
using Wds.SixStars.Ui.Services;
using Wds.SixStars.Api.Ai;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private LocalModelServiceOptions CreateMusicOptions()
    {
        var options = new LocalChatModelOptions {
            EndpointUrl = Environment.GetEnvironmentVariable("SIX_STARS_LLM_ENDPOINT") ?? _appConfig.LocalModels.LlmEndpointUrl,
            Model = Environment.GetEnvironmentVariable("SIX_STARS_LLM_MODEL") ?? _appConfig.LLM.Model,
            TimeoutSeconds = _appConfig.LLM.TimeoutSeconds,
            Temperature = _appConfig.LLM.Temperature,
            DisableReasoning = true,
            UseJsonSchemaResponseFormat = false,
            LogExchangesToDisk = _appConfig.LLM.LogExchangesToDisk
        };
        return CreateLocalModelServiceOptions(options);
    }
    public MusicWindow CreateMusicWindow() => new MusicWindow(CreateMusicOptions(), _appConfig.ResonatorMidi);

    private readonly object _musicServiceGate = new();
    private MobileMusicStreamingService? _mobileMusicService;
    private MobileMusicStreamingService MusicService {
        get { lock (_musicServiceGate) return _mobileMusicService ??= new MobileMusicStreamingService(CreateMusicOptions, _appConfig.ResonatorMidi); }
    }
    private Task<byte[]> ProcessMobileMusicAsync(MusicCompositionRequest request, CancellationToken token)
        => MusicService.RenderWavAsync(request, token);
    private Task ProcessMobileMusicStreamAsync(MusicCompositionRequest request, System.IO.Stream response, CancellationToken token)
        => MusicService.StreamAsync(request, response, token);
}
