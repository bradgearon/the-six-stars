using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Fast foreground response schema. Normal chat intentionally stays tiny so speech can start quickly.
    /// Story mode may also include the whole story markdown once, so the host can feed it back for
    /// local cached story autoplay while only speaking the first chunk from the LLM response.
    /// </summary>
    public sealed class StarSpeechModelResponse
    {
        public string StarId { get; set; } = string.Empty;

        [JsonPropertyName("spokenText")]
        [JsonConverter(typeof(SpokenTextPartsJsonConverter))]
        public List<string> SpokenParts { get; set; } = new List<string>();

        public string? StoryTitle { get; set; }
        public string StoryId { get; set; } = string.Empty;
        public string StoryMarkdown { get; set; } = string.Empty;
        public string StorySummary { get; set; } = string.Empty;
        public bool StoryIsFinished { get; set; }
        public int StoryPartIndex { get; set; }

        /// <summary>
        /// Compact foreground emotion-vector hint for speed tests. Format is one entry per canonical
        /// StarEmotionMatrix.StandardEmotionIds item in order: confidence:score;confidence:score;...
        /// Confidence is 0.00-1.00 and score is 0-100. It describes how the player's latest statement
        /// made this Star feel, without requesting the heavy emotionScores object in the foreground pass.
        /// </summary>
        [JsonPropertyName("emotionalEffectVector")]
        public string EmotionalEffectVector { get; set; } = string.Empty;

        /// <summary>Seven-entry compact negative vector: Irritated, Disrespected, Pressured, CreepedOut, Bored, Hurt, Unsafe.</summary>
        [JsonPropertyName("negativeEmotionVector")]
        public string NegativeEmotionVector { get; set; } = string.Empty;

        [JsonIgnore]
        public string SpokenText
        {
            get => string.Join(" ", SpokenParts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
            set
            {
                SpokenParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(value)) SpokenParts.Add(value.Trim());
            }
        }

        // Intentionally no emotion scores, no state hints, no memory fields, and no voiceDirective here.
        // The foreground pass is only for what the Star says aloud, plus optional long-story text.
        // Voice settings are built deterministically from the Star profile; heavy emotion/memory review runs later.
    }
}
