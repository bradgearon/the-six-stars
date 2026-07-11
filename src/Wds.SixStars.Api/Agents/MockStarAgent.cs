using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Api.Agents
{
    /// <summary>
    /// Deterministic starter agent for the desktop harness. Replace this with an LLM-backed agent later,
    /// but keep the same IStarAgent contract so Unity does not change.
    /// </summary>
    public sealed class MockStarAgent : IStarAgent
    {
        public Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
        {
            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                EmotionalPurpose = "desktop_prototype_response",
                Debug = new DebugInfo
                {
                    DetectedIntent = DetectIntent(request),
                    WisdomScore = EstimateWisdomScore(request.PlayerText)
                }
            };

            if (request.RequestType == RequestType.Story)
            {
                PopulateStoryResponse(profile, request, state, response);
            }
            else
            {
                PopulateChatResponse(profile, request, response);
            }

            PopulateEmotionScores(profile, request, response);
            PopulateVoiceDirective(profile, response);
            PopulateDebug(profile, request, response);
            PopulateMemoryFields(profile, request, state, response);
            return Task.FromResult(response);
        }

        private static void PopulateStoryResponse(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse response)
        {
            StoryMemoryCard card = profile.StoryDeck
                .Where(x => x.UnlockedAtBond <= Math.Max(state.Heart, state.Trust))
                .FirstOrDefault() ?? profile.StoryDeck.First();

            response.StoryTitle = CreateStoryTitle(profile, card);
            response.StoryKind = card.Kind;
            response.CanonSourcesUsed.Add(card.Id);
            response.EmotionalPurpose = "keep_player_company_with_canon_story";
            response.SpokenText = CreateStoryText(profile, card, request);
        }

        private static void PopulateChatResponse(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response)
        {
            string text = request.PlayerText ?? string.Empty;
            string lower = text.ToLowerInvariant();

            if (ContainsAny(lower, "thank", "love", "beautiful", "trust", "i believe", "mercy", "forgive"))
            {
                response.SpokenText = profile.StarId == "Elusiaa"
                    ? "Little one... you touched the gentler part of me. I will keep my light near you while we think this through."
                    : profile.DisplayName + " listens closely. \"That reaches me more than I expected. Keep speaking like that, and I may forget the dark for a moment.\"";
                response.EmotionalPurpose = "receive_affection_or_wisdom";
                response.DetectedCombos.Add("affection_plus_wisdom");
            }
            else if (ContainsAny(lower, "hurt him", "kill", "punish", "make him pay", "cast holy harder", "bleed"))
            {
                response.SpokenText = profile.StarId == "Elusiaa"
                    ? "I feel the anger in that, and part of me understands it. But mercy becomes a weapon too easily when the story is unfinished."
                    : profile.DisplayName + " grows still. \"That is a dangerous road. I know its shape too well.\"";
                response.EmotionalPurpose = "warn_against_cruel_escalation";
                response.DetectedCombos.Add("cruel_escalation");
            }
            else if (ContainsAny(lower, "goblin", "harpy", "bridge", "misunderstanding"))
            {
                response.SpokenText = profile.StarId == "Elusiaa"
                    ? "Then let us ask before we judge. A bridge can look like a battlefield from far away, and a game can look like cruelty when the heart is tired."
                    : profile.DisplayName + " tilts her head. \"A hidden context, then. Those are where people ruin each other fastest.\"";
                response.EmotionalPurpose = "discern_incomplete_information";
            }
            else
            {
                response.SpokenText = CreateIdleReply(profile);
            }
        }

        private static void PopulateEmotionScores(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response)
        {
            string text = (request.PlayerText ?? string.Empty).ToLowerInvariant();

            foreach (StarEmotionDefinition emotion in profile.Emotions)
            {
                SetScore(response, emotion.Id, 0, 0.40, "baseline not strongly invoked");
            }

            SetScore(response, "Intrigued", 25, 0.55, "baseline attention to the player");
            SetScore(response, "Concerned", 25, 0.55, "baseline cave caution");

            if (ContainsAny(text, "thank", "love", "beautiful", "trust", "mercy", "forgive", "stay with me", "keep me company"))
            {
                SetScore(response, "Comforted", 82, 0.90, "player asks for closeness or affirms her core domain");
                SetScore(response, "Trusting", 78, 0.86, "player speaks with trust");
                SetScore(response, "Affectionate", 88, 0.91, "player's words are warm and relational");
                SetScore(response, "Hopeful", 74, 0.82, "player nudges her toward mercy and possibility");
                if (request.GameContext.RomanceMode)
                {
                    SetScore(response, "Tender", 66, 0.75, "romance mode allows soft tenderness");
                    SetScore(response, "Flattered", 58, 0.72, "player's affection is noticed");
                }
            }

            if (ContainsAny(text, "should we ask", "why", "understand", "not enough information", "investigate", "maybe they were playing"))
            {
                SetScore(response, "Respected", 84, 0.89, "player shows wisdom under uncertainty");
                SetScore(response, "Admiring", 72, 0.80, "player demonstrates discernment");
                SetScore(response, "Concerned", 68, 0.76, "the situation still needs caution");
                SetScore(response, "Determined", 60, 0.72, "she wants to do the right thing");
            }

            if (ContainsAny(text, "hurt him", "kill", "punish", "make him pay", "cast holy harder", "bleed"))
            {
                SetScore(response, "Hurt", 86, 0.89, "player escalates toward cruelty or revenge");
                SetScore(response, "Concerned", 82, 0.86, "moral alarm");
                SetScore(response, "Suspicious", 72, 0.78, "she questions the player's influence");
                SetScore(response, "Sad", 70, 0.75, "mercy is being turned into a weapon");
            }

            if (response.StoryKind != StoryKind.None)
            {
                SetScore(response, "Comforted", 82, 0.87, "storytelling companionship");
                SetScore(response, "Wondrous", 76, 0.84, "personal memory shared with wonder");
                SetScore(response, "Affectionate", 72, 0.82, "she is willing to keep the player company");
            }
        }

        private static void PopulateVoiceDirective(StarProfile profile, StarInteractionResponse response)
        {
            string strongestEmotion = response.EmotionScores
                .OrderByDescending(x => x.Value.Score * x.Value.Confidence)
                .Select(x => x.Key)
                .FirstOrDefault() ?? "neutral";

            double intensity = response.EmotionScores.Count == 0
                ? 0.5
                : Math.Min(1.0, Math.Max(0.0, response.EmotionScores.Max(x => x.Value.Score) / 100.0));

            response.VoiceDirective = new VoiceDirective
            {
                VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId,
                VoiceDesignPrompt = string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt) ? profile.VoiceDirection : profile.VoiceDesignPrompt,
                VoiceCloneAudioFile = profile.VoiceCloneAudioFile,
                VoiceCloneRefText = profile.VoiceCloneRefText,
                DeliveryInstructions = "Speak in character as " + profile.DisplayName + ", with " + strongestEmotion + " feeling, natural pacing, and emotional restraint.",
                PrimaryEmotion = strongestEmotion,
                EmotionIntensity = intensity,
                Pace = response.StoryKind == StoryKind.None ? "normal" : "slow-medium narration",
                Pitch = "natural",
                Volume = "normal-soft",
                Prosody = "clear, expressive, not melodramatic",
                Language = "Auto"
            };
            response.VoiceDirective.Avoid.Add("robotic delivery");
            response.VoiceDirective.Avoid.Add("breaking character");
            if (strongestEmotion == "Amused" || strongestEmotion == "Playful") response.VoiceDirective.AudioTags.Add("soft laugh");
            if (strongestEmotion == "Hurt" || strongestEmotion == "Sad") response.VoiceDirective.AudioTags.Add("small sigh");
        }

        private static void PopulateMemoryFields(StarProfile profile, StarInteractionRequest request, StarState state, StarInteractionResponse response)
        {
            string playerText = (request.PlayerText ?? string.Empty).Trim();
            string prior = string.IsNullOrWhiteSpace(state.ConversationSummary)
                ? "No prior durable summary existed."
                : state.ConversationSummary;

            response.ConversationSummary = "The Last Cat is speaking with " + profile.DisplayName + ". Latest request type: " + request.RequestType + ". Prior summary: " + prior + " Latest exchange: player said '" + Truncate(playerText, 180) + "' and she answered '" + Truncate(response.SpokenText, 180) + "'.";
            response.ChangesSummary = profile.DisplayName + " responded to the player's latest " + request.RequestType + " input; purpose=" + response.EmotionalPurpose + ".";

            if (!string.IsNullOrWhiteSpace(playerText) && response.EmotionScores.Any(x => x.Value.Score >= 80 && x.Value.Confidence >= 0.75))
            {
                response.ImportantPlayerQuotes.Add(playerText);
            }

            if (response.DetectedCombos.Count > 0)
            {
                response.RelationshipEvents.Add("Triggered combo(s): " + string.Join(", ", response.DetectedCombos));
            }

            if (LooksLikeTeaching(playerText))
            {
                response.LearnedFunctions.Add(new StarLearnedFunction
                {
                    Id = "player_teaching_" + Math.Abs(playerText.GetHashCode()).ToString(),
                    Name = "Player-taught lesson",
                    Description = "The player appeared to teach or define a durable rule, idea, or function: " + Truncate(playerText, 220),
                    SourcePlayerQuote = playerText,
                    Confidence = 0.72,
                    IsActive = true,
                    Tags = { "player_taught", "desktop_mock" }
                });
            }
        }

        private static void PopulateDebug(StarProfile profile, StarInteractionRequest request, StarInteractionResponse response)
        {
            if (request.PlayerText.ToLowerInvariant().Contains("goblin"))
            {
                response.Debug.RiskFlags.Add("incomplete_information");
                response.Debug.LoreUsed.Add("goblin_harpy_misunderstanding_route");
            }

            if (response.DetectedCombos.Contains("cruel_escalation"))
            {
                response.Debug.RiskFlags.Add("let_us_call_hell_candidate");
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "AudioMotif",
                    Target = "Benaveil",
                    Data = { { "motif", "LetUsCallHell_Subtle" } }
                });
            }
            else if (response.DetectedCombos.Contains("affection_plus_wisdom") || response.StoryKind != StoryKind.None)
            {
                response.UnityEvents.Add(new UnityEventEnvelope
                {
                    Type = "AudioMotif",
                    Target = "Benaveil",
                    Data = { { "motif", "LetUsGo_Soft" } }
                });
            }

            response.Debug.Notes.Add("MockStarAgent is deterministic. Replace with an LLM-backed IStarAgent when ready.");
        }

        private static string CreateStoryTitle(StarProfile profile, StoryMemoryCard card)
        {
            if (card.Id.Contains("waterskin")) return "The Angel With the Small Waterskin";
            if (card.Id.Contains("tar_water")) return "The Reflection That Would Not Drown";
            if (card.Id.Contains("goblin_prank")) return "Three Nights of Unnecessary Holiness";
            return profile.DisplayName + "'s Remembered Story";
        }

        private static string CreateStoryText(StarProfile profile, StoryMemoryCard card, StarInteractionRequest request)
        {
            string intro = profile.StarId == "Elusiaa"
                ? "Of course, little one. I will keep my voice low, like a lantern beside your work. "
                : "Very well. Stay with me a moment, and I will give the silence a shape. ";

            if (card.Id == "elusiaa_dying_valley_waterskin")
            {
                return intro + "Once, in a valley that had forgotten rain, I saw an angel arrive with almost nothing. Not a sword. Not a throne. Only a small waterskin, cracked at the rim. He gave the first drink to a child, the second to an old woman, and the third he poured into the dust as if promising the earth it had not been abandoned. For many nights he sang softly while the people slept. When the rain finally came, no one remembered his face clearly. They remembered the taste of mercy. That is how the Stream works sometimes: not as thunder, but as one cup carried faithfully through thirst.";
            }

            if (card.Id == "kaelia_tar_water_reflection")
            {
                return intro + "There was once a girl who looked into still water and saw the surface turn black. Every time she reached for the reflection, it drowned before her hand arrived. She thought this proved the Void was honest and hope was the liar. But one night, a single bubble rose through the tar. It did not save her. It did not explain the suffering. It only proved the surface was not the whole world. I think about that bubble more than I admit.";
            }

            if (card.Id == "elusiaa_holy_goblin_prank")
            {
                return intro + "There was, hypothetically, a goblin who slept too proudly near a bridge. And there was, hypothetically, a holy light that may have tapped him awake three nights in a row. The first night he screamed. The second, he wore a pot as a helmet. The third, he left a note that read: 'If this is mercy, mercy is rude.' I learned something from that. Justice without understanding is dangerous... but laughter, carefully measured, can keep anger from becoming a throne.";
            }

            return intro + card.Summary;
        }

        private static string CreateIdleReply(StarProfile profile)
        {
            if (profile.StarId == "Elusiaa")
            {
                return "I am here, little one. Tell me what weighs on you, or ask me for a story, and I will place a little light beside your work.";
            }

            if (profile.StarId == "Kaelia")
            {
                return "I am listening. The Void is not gentle, but it is quiet enough for company.";
            }

            return profile.DisplayName + " is listening. Ask for a story, a memory, or counsel, and she will answer from her domain of " + profile.Domain + ".";
        }

        private static bool LooksLikeTeaching(string text)
        {
            string lower = (text ?? string.Empty).ToLowerInvariant();
            return ContainsAny(lower, "remember", "learn", "teach", "from now on", "this means", "the rule is", "you should know", "when i say");
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string trimmed = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (trimmed.Length <= maxLength) return trimmed;
            return trimmed.Substring(0, maxLength).Trim() + "...";
        }

        private static string DetectIntent(StarInteractionRequest request)
        {
            if (request.RequestType == RequestType.Story) return "story_request";
            if (request.PlayerText.ToLowerInvariant().Contains("goblin")) return "moral_discernment";
            if (request.RequestType == RequestType.BattleUpdate) return "battle_update";
            if (request.RequestType == RequestType.Letter) return "letter_submission";
            return "free_chat";
        }

        private static double EstimateWisdomScore(string text)
        {
            string lower = text.ToLowerInvariant();
            double score = 0.50;
            if (ContainsAny(lower, "why", "ask", "understand", "maybe", "before", "mercy", "forgive", "discern")) score += 0.30;
            if (ContainsAny(lower, "hurt", "kill", "punish", "make him pay")) score -= 0.35;
            if (score < 0) score = 0;
            if (score > 1) score = 1;
            return score;
        }

        private static void SetScore(StarInteractionResponse response, string id, double score, double confidence, string reason)
        {
            response.EmotionScores[id] = new EmotionScore
            {
                Score = score,
                Confidence = confidence,
                Reason = reason
            };
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
