using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Wds.SixStars.Api.Models;

namespace Wds.SixStars.Api.Stories;

/// <summary>
/// API-layer story speech adapter shared by desktop, Android, and future Unity clients.
/// It turns one full storyMarkdown response into small local speech responses so every
/// front end can prefetch the next voice chunk while the current chunk is being played.
/// </summary>
public sealed class StarStorySpeechAdapter
{
    public bool ShouldUseStoryAutoplay(StarInteractionResponse? response)
    {
        if (response == null) return false;
        if (string.IsNullOrWhiteSpace(response.StoryMarkdown)) return false;
        if (string.IsNullOrWhiteSpace(response.SpokenText) && (response.SpokenParts == null || response.SpokenParts.Count == 0)) return false;
        return TryGetNextNonEmptyStoryChunkFromMarkdown(
            response.StoryMarkdown,
            Math.Max(0, response.StoryPartIndex + 1),
            out _,
            out _);
    }

    public int CountStoryChunks(string? markdown)
    {
        return ExtractAllMarkdownChunks(markdown).Count;
    }

    public bool TryCreateNextStoryChunkResponse(StarInteractionResponse currentResponse, out StarInteractionResponse? nextResponse)
    {
        nextResponse = null;
        if (currentResponse == null || string.IsNullOrWhiteSpace(currentResponse.StoryMarkdown)) return false;
        int startIndex = Math.Max(0, currentResponse.StoryPartIndex + 1);
        if (!TryGetNextNonEmptyStoryChunkFromMarkdown(currentResponse.StoryMarkdown, startIndex, out int actualIndex, out string nextChunk))
        {
            return false;
        }

        nextResponse = CreateLocalStoryChunkResponse(currentResponse, nextChunk, actualIndex);
        return true;
    }

    public StarInteractionResponse CreateLocalStoryChunkResponse(StarInteractionResponse storySource, string markdownChunk, int chunkIndex)
    {
        if (storySource == null) throw new ArgumentNullException(nameof(storySource));
        string cleanChunk = CleanStoryChunkForSpeech(markdownChunk);
        bool finishedAfterThisChunk = !TryGetNextNonEmptyStoryChunkFromMarkdown(storySource.StoryMarkdown, chunkIndex + 1, out _, out _);
        VoiceDirective voice = CloneVoiceDirective(storySource.VoiceDirective);
        if (string.IsNullOrWhiteSpace(voice.DeliveryInstructions))
        {
            voice.DeliveryInstructions = "Continue the same story narration naturally. Keep the breath, tone, and emotional continuity from the previous chunk.";
        }
        else if (!voice.DeliveryInstructions.Contains("Continue the same story narration", StringComparison.OrdinalIgnoreCase))
        {
            voice.DeliveryInstructions = voice.DeliveryInstructions.Trim() + " Continue the same story narration naturally, without a recap or setup phrase.";
        }

        StarInteractionResponse response = new StarInteractionResponse
        {
            StarId = storySource.StarId ?? string.Empty,
            StoryId = storySource.StoryId ?? string.Empty,
            StoryTitle = storySource.StoryTitle ?? string.Empty,
            StoryKind = storySource.StoryKind,
            StoryMarkdown = storySource.StoryMarkdown ?? string.Empty,
            StorySummary = storySource.StorySummary ?? string.Empty,
            StoryPartIndex = Math.Max(0, chunkIndex),
            StoryIsFinished = finishedAfterThisChunk,
            EmotionalPurpose = "API story adapter cached playback; no LLM continuation request.",
            EmotionalEffectVector = string.Empty,
            VoiceDirective = voice,
            SuggestedStateDeltas = RelationshipDelta.Zero(),
            StateDeltas = RelationshipDelta.Zero(),
            ConversationSummary = "Continuing cached story narration from markdown chunk " + Math.Max(0, chunkIndex) + ".",
            ChangesSummary = "Story autoplay advanced through the API story adapter without a new LLM call."
        };
        response.SetSpokenParts(SplitStoryChunkForSpeech(cleanChunk));
        response.Debug.DetectedIntent = "StoryAutoplayApiChunk";
        response.Debug.Notes.Add("API story adapter used cached storyMarkdown chunk " + Math.Max(0, chunkIndex) + "; no LLM request was sent.");
        response.UnityEvents.Add(new UnityEventEnvelope
        {
            Type = "StoryAutoplayChunk",
            Target = response.StarId,
            Data =
            {
                { "storyId", response.StoryId ?? string.Empty },
                { "storyTitle", response.StoryTitle ?? string.Empty },
                { "storyPartIndex", response.StoryPartIndex.ToString() },
                { "storyIsFinished", response.StoryIsFinished.ToString() },
                { "storyChunkCount", CountStoryChunks(response.StoryMarkdown).ToString() },
                { "source", "api-story-adapter-cached-markdown" }
            }
        });
        return response;
    }

    public bool TryGetNextNonEmptyStoryChunkFromMarkdown(string? markdown, int startChunkIndex, out int chunkIndex, out string chunk)
    {
        chunkIndex = Math.Max(0, startChunkIndex);
        chunk = string.Empty;
        if (string.IsNullOrWhiteSpace(markdown)) return false;

        int wanted = Math.Max(0, startChunkIndex);
        string text = markdown.Replace("\r\n", "\n");
        MatchCollection matches = Regex.Matches(
            text,
            @"(?ims)^\s*#{1,6}\s*Chunk\s+(?<index>\d+)\b[^\n]*\n(?<body>.*?)(?=^\s*---+\s*$|^\s*#{1,6}\s*Chunk\s+\d+\b|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (matches.Count > 0)
        {
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups["index"].Value, out int index) || index < wanted) continue;
                string cleaned = CleanStoryChunkForSpeech(match.Groups["body"].Value);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;
                chunkIndex = index;
                chunk = cleaned;
                return true;
            }
            return false;
        }

        if (!Regex.IsMatch(text, @"(?m)^\s*---+\s*$")) return false;
        string[] pieces = Regex.Split(text, @"(?m)^\s*---+\s*$")
            .Select(CleanStoryChunkForSpeech)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (wanted >= pieces.Length) return false;
        chunkIndex = wanted;
        chunk = pieces[wanted];
        return true;
    }

    public static string[] SplitStoryChunkForSpeech(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk)) return Array.Empty<string>();
        string normalized = Regex.Replace(chunk.Replace("\r\n", "\n").Trim(), @"\n{3,}", "\n\n");
        string[] paragraphParts = normalized
            .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (paragraphParts.Length > 1) return paragraphParts;

        string[] sentences = Regex.Split(normalized, @"(?<=[.!?…])\s+")
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (sentences.Length <= 2) return new[] { normalized };

        return sentences
            .Select((sentence, index) => new { sentence, index })
            .GroupBy(x => x.index / 2)
            .Select(g => string.Join(" ", g.Select(x => x.sentence)).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    public static string CleanStoryChunkForSpeech(string? markdownChunk)
    {
        if (string.IsNullOrWhiteSpace(markdownChunk)) return string.Empty;
        string text = markdownChunk.Replace("\r\n", "\n").Trim();
        text = Regex.Replace(text, @"(?im)^\s*#{1,6}\s*Chunk\s+\d+\b[^\n]*$", string.Empty);
        text = Regex.Replace(text, @"(?im)^\s*#\s+.+$", string.Empty);
        text = Regex.Replace(text, @"(?m)^\s*---+\s*$", string.Empty);
        text = Regex.Replace(text, @"(?m)\s+---+\s*$", string.Empty);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public List<string> ExtractAllMarkdownChunks(string? markdown)
    {
        List<string> chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(markdown)) return chunks;
        string text = markdown.Replace("\r\n", "\n");
        MatchCollection matches = Regex.Matches(
            text,
            @"(?ims)^\s*#{1,6}\s*Chunk\s+\d+\b[^\n]*\n(?<body>.*?)(?=^\s*---+\s*$|^\s*#{1,6}\s*Chunk\s+\d+\b|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            string cleaned = CleanStoryChunkForSpeech(match.Groups["body"].Value);
            if (!string.IsNullOrWhiteSpace(cleaned)) chunks.Add(cleaned);
        }

        if (chunks.Count == 0 && Regex.IsMatch(text, @"(?m)^\s*---+\s*$"))
        {
            chunks.AddRange(Regex.Split(text, @"(?m)^\s*---+\s*$")
                .Select(CleanStoryChunkForSpeech)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        return chunks;
    }

    private static VoiceDirective CloneVoiceDirective(VoiceDirective? source)
    {
        if (source == null) return VoiceDirective.Empty();
        return new VoiceDirective
        {
            VoiceId = source.VoiceId ?? string.Empty,
            VoiceDesignPrompt = source.VoiceDesignPrompt ?? string.Empty,
            VoiceCloneAudioFile = source.VoiceCloneAudioFile ?? string.Empty,
            VoiceCloneRefText = source.VoiceCloneRefText ?? string.Empty,
            XVectorOnly = source.XVectorOnly,
            DeliveryInstructions = source.DeliveryInstructions ?? string.Empty,
            PrimaryEmotion = source.PrimaryEmotion ?? "neutral",
            EmotionIntensity = source.EmotionIntensity,
            Pace = source.Pace ?? "normal",
            Pitch = source.Pitch ?? "natural",
            Volume = source.Volume ?? "normal",
            Prosody = source.Prosody ?? string.Empty,
            Language = source.Language ?? "Auto",
            AudioTags = source.AudioTags == null ? new List<string>() : source.AudioTags.ToList(),
            Avoid = source.Avoid == null ? new List<string>() : source.Avoid.ToList()
        };
    }
}
