using System;

namespace Wds.SixStars.Ui.Services;

/// <summary>
/// Composition boundary for the desktop UI. MainViewModel coordinates these services but no longer
/// owns their worker loops, sockets, audio devices, or mutable story session state directly.
/// </summary>
internal sealed class DesktopUiServiceRegistry : IDisposable
{
    public required DesktopConversationService Conversations { get; init; }
    public required DesktopSpeechInputService SpeechInput { get; init; }
    public required DesktopVoiceOutputService VoiceOutput { get; init; }
    public required DesktopScreenAwarenessService ScreenAwareness { get; init; }
    public required DesktopDiagnosticsService Diagnostics { get; init; }
    public required DesktopStarStateService StarState { get; init; }
    public required DesktopStarInitiativeService StarInitiative { get; init; }
    public required DesktopStorySessionService StorySession { get; init; }
    public required MobileBridgeHostService MobileBridgeHost { get; init; }
    public required MobileConversationService MobileConversation { get; init; }
    public required MobileVoiceDeliveryService MobileVoice { get; init; }

    public void Dispose()
    {
        DisposeBestEffort(StarInitiative);
        DisposeBestEffort(SpeechInput);
        DisposeBestEffort(VoiceOutput);
        DisposeBestEffort(MobileBridgeHost);
        DisposeBestEffort(MobileConversation);
        DisposeBestEffort(ScreenAwareness);
    }

    private static void DisposeBestEffort(IDisposable disposable)
    {
        try { disposable.Dispose(); }
        catch { }
    }
}
