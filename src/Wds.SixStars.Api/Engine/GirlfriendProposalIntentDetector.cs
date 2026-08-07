using System;
using System.Collections.Generic;
using System.Text;

namespace Wds.SixStars.Api.Engine
{
    /// <summary>
    /// Deterministic relationship-intent detection shared by desktop, voice, HTTP, and WebSocket
    /// entry points. The transport may optionally send an explicit intent hint, but the runtime
    /// always verifies the actual player text so natural proposals work from every client.
    /// </summary>
    public static class GirlfriendProposalIntentDetector
    {
        private static readonly string[] DirectProposalPhrases =
        {
            "be my girlfriend",
            "be my girl",
            "be my gf",
            "become my girlfriend",
            "will you be my girlfriend",
            "would you be my girlfriend",
            "could you be my girlfriend",
            "can you be my girlfriend",
            "do you want to be my girlfriend",
            "do you wanna be my girlfriend",
            "would you like to be my girlfriend",
            "will you become my girlfriend",
            "would you become my girlfriend",
            "could you become my girlfriend",
            "can you become my girlfriend",
            "i want you to be my girlfriend",
            "i would like you to be my girlfriend",
            "i want us to be boyfriend and girlfriend",
            "can i be your boyfriend",
            "could i be your boyfriend",
            "would you let me be your boyfriend",
            "i want to be your boyfriend",
            "will you go out with me",
            "would you go out with me",
            "could you go out with me",
            "do you want to go out with me",
            "will you date me",
            "would you date me",
            "could you date me",
            "do you want to date me",
            "will you be mine",
            "would you be mine"
        };

        private static readonly HashSet<string> ProposalVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "be", "become", "date", "choose", "accept", "say", "want", "wanna"
        };

        public static bool IsProposal(string? text)
        {
            string normalized = Normalize(text);
            if (normalized.Length == 0) return false;

            foreach (string phrase in DirectProposalPhrases)
            {
                if (ContainsPhrase(normalized, phrase)) return true;
            }

            // Catch natural variants such as "could I ask you to become my girl friend?" while
            // keeping ordinary mentions of a girlfriend from changing relationship state.
            if (!ContainsPhrase(normalized, "girlfriend")) return false;
            if (!(ContainsPhrase(normalized, "my girlfriend")
                || ContainsPhrase(normalized, "your boyfriend")
                || ContainsPhrase(normalized, "boyfriend and girlfriend")))
            {
                return false;
            }

            string[] words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool hasProposalVerb = false;
            bool hasQuestionOrDesireMarker = false;
            foreach (string word in words)
            {
                if (ProposalVerbs.Contains(word)) hasProposalVerb = true;
                if (word == "will" || word == "would" || word == "could" || word == "can"
                    || word == "please" || word == "ask" || word == "asking" || word == "want" || word == "wanna")
                {
                    hasQuestionOrDesireMarker = true;
                }
            }

            return hasProposalVerb && hasQuestionOrDesireMarker;
        }

        private static bool ContainsPhrase(string normalized, string phrase)
        {
            string padded = " " + normalized + " ";
            string target = " " + phrase + " ";
            return padded.IndexOf(target, StringComparison.Ordinal) >= 0;
        }

        private static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            StringBuilder builder = new StringBuilder(text.Length);
            bool previousWasSpace = true;
            foreach (char raw in text.ToLowerInvariant())
            {
                char value = raw == '\u2019' || raw == '\u2018' ? '\'' : raw;
                if (char.IsLetterOrDigit(value))
                {
                    builder.Append(value);
                    previousWasSpace = false;
                }
                else if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }

            string normalized = builder.ToString().Trim();
            normalized = normalized.Replace("girl friend", "girlfriend");
            return normalized;
        }
    }
}
