using System.Collections.Generic;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Ui.Networking;

public sealed class MobileBridgeRequest
{
    public string SessionId { get; set; } = "android-phone-dev";
    public string StarId { get; set; } = "Elusiaa";
    public string RequestType { get; set; } = "Chat";
    public string PlayerText { get; set; } = string.Empty;
    public string Area { get; set; } = "VirethornCaves";
    public string Quest { get; set; } = "AndroidPhoneBridge";
    public bool RomanceMode { get; set; } = true;
    public bool SpeakOnDesktop { get; set; }
    public bool ReturnAudio { get; set; } = true;
    public string ClientName { get; set; } = "SixStarsMobileAndroid";
    public string AudioFormat { get; set; } = "wav";
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int BitsPerSample { get; set; } = 16;
    public string AudioBase64 { get; set; } = string.Empty;
    public string PhotoBase64 { get; set; } = string.Empty;
    public string PhotoMimeType { get; set; } = "image/jpeg";
    public string PhotoCaption { get; set; } = string.Empty;
}


public sealed class MobileBridgeResponse
{
    public bool Ok { get; set; } = true;
    public string Source { get; set; } = "mobile-bridge";
    public string StarId { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string Transcript { get; set; } = string.Empty;
    public string SpokenText { get; set; } = string.Empty;
    public string StarOutput { get; set; } = string.Empty;
    public string ConversationSummary { get; set; } = string.Empty;
    public string ChangesSummary { get; set; } = string.Empty;
    public string EmotionalPurpose { get; set; } = string.Empty;
    public int HeartDelta { get; set; }
    public int TrustDelta { get; set; }
    public int RomanceDelta { get; set; }
    public int BenaveilDelta { get; set; }
    public double EmotionalConnectionDelta { get; set; }
    public double EmotionalConnectionAfter { get; set; }
    public bool IsGirlfriendReadyAfter { get; set; }
    public bool VoiceRequested { get; set; }
    public bool VoiceSocketConnected { get; set; }
    public int VoiceChunksSent { get; set; }
    public int VoiceBytesSent { get; set; }
    public string VoiceMessage { get; set; } = string.Empty;
    public string VoiceError { get; set; } = string.Empty;
    public string StoryId { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public string StoryMarkdown { get; set; } = string.Empty;
    public string StorySummary { get; set; } = string.Empty;
    public int StoryPartIndex { get; set; }
    public bool StoryIsFinished { get; set; }
    public bool StoryMarkdownReady { get; set; }
    public bool StoryAutoplayPlanned { get; set; }
    public int StoryChunkCount { get; set; }
    public bool PhotoAttached { get; set; }
    public string PhotoSummary { get; set; } = string.Empty;
    public string PhotoError { get; set; } = string.Empty;
    public List<TopAffectedEmotion> TopAffectedEmotions { get; set; } = new List<TopAffectedEmotion>();
    public List<string> Notes { get; } = new List<string>();
}

