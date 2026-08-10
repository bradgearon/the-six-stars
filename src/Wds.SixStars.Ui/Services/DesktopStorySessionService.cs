using System.Text;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Ui.Services;

/// <summary>Mutable story session state extracted from MainViewModel; only orchestration remains in the UI layer.</summary>
internal sealed class DesktopStorySessionService
{
    public bool AutoContinue { get; set; }
    public string StarId { get; set; } = string.Empty;
    public string StoryId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public StoryKind Kind { get; set; } = StoryKind.None;
    public VoiceDirective VoiceDirective { get; set; } = VoiceDirective.Empty();
    public StringBuilder Progress { get; } = new();
    public string LastSpokenText { get; set; } = string.Empty;
    public int NextPartIndex { get; set; }
    public bool SuppressContinuationOnce { get; set; }

    public void Reset()
    {
        AutoContinue = false;
        StarId = string.Empty;
        StoryId = string.Empty;
        Title = string.Empty;
        Markdown = string.Empty;
        Summary = string.Empty;
        Kind = StoryKind.None;
        VoiceDirective = VoiceDirective.Empty();
        Progress.Clear();
        LastSpokenText = string.Empty;
        NextPartIndex = 0;
        SuppressContinuationOnce = false;
    }
}
