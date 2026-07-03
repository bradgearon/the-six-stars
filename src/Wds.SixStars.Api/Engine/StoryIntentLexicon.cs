using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Cheap text-only gate for deciding whether it is worth sending the tiny story-intent classifier request.
    /// This intentionally catches story aliases without promoting the message to Story mode by itself;
    /// the LLM classifier still makes the final decision after this gate passes.
    /// </summary>
    public static class StoryIntentLexicon
    {
        private static readonly string[] NegatedStoryPhrases =
        {
            "do not tell me a story",
            "don't tell me a story",
            "dont tell me a story",
            "do not tell a story",
            "don't tell a story",
            "dont tell a story",
            "no story",
            "not a story",
            "stop the story",
            "cancel the story",
            "end the story"
        };

        private static readonly string[] DirectPhrases =
        {
            "once upon a time",
            "story time",
            "storytime",
            "bedtime story",
            "bed time story",
            "fairy tale",
            "fairy tales",
            "campfire story",
            "cave story",
            "short story",
            "long story",
            "another story",
            "next story"
        };

        private static readonly string[] DirectStoryWords =
        {
            "story",
            "stories",
            "storytelling",
            "storyteller",
            "backstory",
            "tale",
            "tales",
            "fairytale",
            "fairytales",
            "fable",
            "fables",
            "legend",
            "legends",
            "myth",
            "myths",
            "parable",
            "parables",
            "saga",
            "sagas",
            "chronicle",
            "chronicles",
            "folktale",
            "folktales",
            "yarn",
            "yarns",
            "narrative",
            "narratives",
            "narration",
            "narrations"
        };

        private static readonly string[] StoryActionWords =
        {
            "tell",
            "tells",
            "telling",
            "narrate",
            "narrates",
            "narrating",
            "continue",
            "continues",
            "continuing",
            "finish",
            "finishes",
            "finishing",
            "resume",
            "resumes",
            "resuming",
            "start",
            "starts",
            "starting",
            "begin",
            "begins",
            "beginning",
            "make",
            "makes",
            "making",
            "create",
            "creates",
            "creating",
            "invent",
            "invents",
            "inventing",
            "spin",
            "spins",
            "spinning",
            "write",
            "writes",
            "writing",
            "improvise",
            "improvises",
            "improvising"
        };

        private static readonly string[] ContextualStoryWords =
        {
            "adventure",
            "adventures",
            "journey",
            "journeys",
            "chapter",
            "chapters",
            "scene",
            "scenes",
            "prologue",
            "prologues",
            "episode",
            "episodes",
            "bedtime"
        };

        public static bool ContainsStoryIntentTrigger(string? text)
        {
            return TryFindStoryIntentTrigger(text, out _);
        }

        public static bool TryFindStoryIntentTrigger(string? text, out string trigger)
        {
            trigger = string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string value = Normalize(text);
            if (NegatedStoryPhrases.Any(value.Contains))
            {
                trigger = "negated_story_phrase";
                return false;
            }

            foreach (string phrase in DirectPhrases)
            {
                if (ContainsPhrase(value, phrase))
                {
                    trigger = phrase;
                    return true;
                }
            }

            foreach (string word in DirectStoryWords)
            {
                if (ContainsWholeWord(value, word))
                {
                    trigger = word;
                    return true;
                }
            }

            string? action = StoryActionWords.FirstOrDefault(word => ContainsWholeWord(value, word));
            if (!string.IsNullOrEmpty(action))
            {
                string? context = ContextualStoryWords.FirstOrDefault(word => ContainsWholeWord(value, word));
                if (!string.IsNullOrEmpty(context))
                {
                    trigger = action + "+" + context;
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string text)
        {
            return text.Trim()
                .ToLowerInvariant()
                .Replace('’', '\'')
                .Replace('`', '\'')
                .Replace('“', '"')
                .Replace('”', '"');
        }

        private static bool ContainsPhrase(string value, string phrase)
        {
            return Regex.IsMatch(value, @"(?<![a-z0-9])" + Regex.Escape(phrase) + @"(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool ContainsWholeWord(string value, string word)
        {
            return Regex.IsMatch(value, @"(?<![a-z0-9])" + Regex.Escape(word) + @"(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
