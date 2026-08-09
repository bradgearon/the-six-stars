using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Mobile;

namespace Wds.SixStars.Ui.Networking;

public sealed class MobileBridgeRequest
{
    public string SessionId { get; set; } = "android-phone-dev";
    public string StarId { get; set; } = "Elusiaa";
    public string RequestType { get; set; } = "Chat";
    public string PlayerText { get; set; } = string.Empty;
    public bool GirlfriendProposal { get; set; }
    public string Area { get; set; } = "VirethornCaves";
    public string Quest { get; set; } = "AndroidPhoneBridge";
    public bool RomanceMode { get; set; } = true;
    public bool SpeakOnDesktop { get; set; }
    public bool ReturnAudio { get; set; } = true;
    public bool ReturnAudioInline { get; set; }
    public bool PrefetchForListen { get; set; }
    public string SpeechIntentId { get; set; } = string.Empty;
    public string PreparedSpeechPrefixIntentId { get; set; } = string.Empty;
    public string SpeechIntentGoal { get; set; } = string.Empty;
    public string SpeechIntentTopic { get; set; } = string.Empty;
    public string SpeechIntentPriority { get; set; } = string.Empty;
    public double SpeechIntentScore { get; set; }
    public string ClientName { get; set; } = "SixStarsMobileAndroid";
    public string Reason { get; set; } = string.Empty;
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
    public bool InitiativeShouldSpeak { get; set; } = true;
    public bool InitiativeSkippedBeforeLlm { get; set; }
    public double InitiativeStateScore { get; set; }
    public double InitiativePriority { get; set; }
    public string InitiativeTopic { get; set; } = string.Empty;
    public string InitiativeReason { get; set; } = string.Empty;
    public int HeartDelta { get; set; }
    public int TrustDelta { get; set; }
    public int RomanceDelta { get; set; }
    public int BenaveilDelta { get; set; }
    public double EmotionalConnectionDelta { get; set; }
    public double EmotionalConnectionAfter { get; set; }
    public bool IsGirlfriendReadyAfter { get; set; }
    public bool IsGirlfriendAfter { get; set; }
    public bool VoiceRequested { get; set; }
    public bool VoiceSocketConnected { get; set; }
    public int VoiceChunksSent { get; set; }
    public int VoiceBytesSent { get; set; }
    public int VoiceChunksCached { get; set; }
    public int VoiceBytesCached { get; set; }
    public List<MobileInlineVoiceChunk> InlineVoiceChunks { get; set; } = new List<MobileInlineVoiceChunk>();
    public bool PrefetchForListen { get; set; }
    public DateTimeOffset? PrefetchedAtUtc { get; set; }
    public string VoiceMessage { get; set; } = string.Empty;
    public string VoiceError { get; set; } = string.Empty;
    public long ServerElapsedMs { get; set; }
    public long RuntimeElapsedMs { get; set; }
    public long GateWaitMs { get; set; }
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
    public List<MobileConversationHistoryItem> RecentConversationHistory { get; set; } = new List<MobileConversationHistoryItem>();
    public string EmotionalEffectVector { get; set; } = string.Empty;
    public string NegativeEmotionVector { get; set; } = string.Empty;
    public string LastEmotionVectorsText { get; set; } = string.Empty;
    public string InterstarStatusText { get; set; } = string.Empty;
    public string DailyStateSlotId { get; set; } = string.Empty;
    public string DailyStateLocalDate { get; set; } = string.Empty;
    public DateTimeOffset? DailyStateGeneratedAt { get; set; }
    public string DailyStateSummary { get; set; } = string.Empty;
    public string DailyStateJson { get; set; } = string.Empty;
    public string DailyStateXml { get; set; } = string.Empty;
    public List<string> Notes { get; } = new List<string>();
    [JsonIgnore]
    public Func<Task>? AfterTransportResponseAsync { get; set; }

    [JsonIgnore]
    public Func<Task>? OnTransportFailureAsync { get; set; }

    public void StartAfterTransportResponse()
    {
        Func<Task>? work = AfterTransportResponseAsync;
        AfterTransportResponseAsync = null;
        OnTransportFailureAsync = null;
        if (work == null) return;
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); } catch { }
        });
    }

    public void StartTransportFailureCleanup()
    {
        Func<Task>? cleanup = OnTransportFailureAsync;
        AfterTransportResponseAsync = null;
        OnTransportFailureAsync = null;
        if (cleanup == null) return;
        _ = Task.Run(async () =>
        {
            try { await cleanup().ConfigureAwait(false); } catch { }
        });
    }
}

