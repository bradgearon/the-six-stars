using System;
using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Ai
{
    public static class StarResponseJsonSchema
    {
        public static object CreateResponseFormat(string schemaName = "six_stars_star_response")
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[]
                    {
                        "starId", "spokenText", "storyTitle", "storyKind", "canonSourcesUsed", "emotionalPurpose", "voiceDirective",
                        "detectedCombos", "suggestedStateDeltas", "conversationSummary", "changesSummary",
                        "importantPlayerQuotes", "memoryNotes", "rememberedMarkers", "referencedMemoryMarkerIds", "secretsUnlocked", "learnedFunctions", "relationshipEvents",
                        "girlfriendHistoryUpdates", "petNamesPlayerUsesForStar", "petNamesStarUsesForPlayer",
                        "girlfriendNeedsMetSummary", "girlfriendNeedsMetVector", "girlfriendWantsMetVector", "girlfriendCoupleActivitySummary", "girlfriendCoupleActivityVector",
                        "thingsLearnedOrSuggestedByPlayer", "loreUsed", "riskFlags", "wisdomScore"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "spokenText", SpokenTextArray(maxItems: 0, maxItemLength: 0) },
                        { "emotionalEffectVector", EmotionEffectVectorProperty() },
                        { "negativeEmotionVector", NegativeEmotionVectorProperty() },
                        { "storyTitle", NullableStringProperty() },
                        { "storyKind", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "None", "PersonalMemory", "MoralParable", "AncientStarFable", "PoeticAbstraction", "ComfortStory", "FlirtyStory", "HorrorWarning", "ComedyStory", "BattleMemory", "SecretUnlockStory" } } } },
                        { "storyId", StringProperty() },
                        { "storyMarkdown", StringProperty() },
                        { "storySummary", StringProperty(1200) },
                        { "storyIsFinished", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "storyPartIndex", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 0 } } },
                        { "canonSourcesUsed", StringArray(8) },
                        { "emotionalPurpose", StringProperty(220) },
                        { "voiceDirective", VoiceDirectiveObject() },
                        { "detectedCombos", StringArray(8) },
                        { "suggestedStateDeltas", DeltaObject() },
                        { "conversationSummary", StringProperty(1600) },
                        { "changesSummary", StringProperty(700) },
                        { "importantPlayerQuotes", EmptyStringArray() },
                        { "memoryNotes", StringArray(10) },
                        { "rememberedMarkers", RememberedMarkerArray(4) },
                        { "referencedMemoryMarkerIds", StringArray(6) },
                        { "secretsUnlocked", StringArray(6) },
                        { "learnedFunctions", LearnedFunctionArray(6) },
                        { "relationshipEvents", StringArray(8) },
                        { "girlfriendHistoryUpdates", StringArray(6) },
                        { "petNamesPlayerUsesForStar", StringArray(5) },
                        { "petNamesStarUsesForPlayer", StringArray(5) },
                        { "girlfriendNeedsMetSummary", StringProperty(420) },
                        { "girlfriendNeedsMetVector", GirlfriendVectorArray(12) },
                        { "girlfriendWantsMetVector", GirlfriendVectorArray(12) },
                        { "girlfriendCoupleActivitySummary", StringProperty(520) },
                        { "girlfriendCoupleActivityVector", StringProperty(1200) },
                        { "thingsLearnedOrSuggestedByPlayer", StringArray(8) },
                        { "loreUsed", StringArray(8) },
                        { "riskFlags", StringArray(5) },
                        { "wisdomScore", NumberProperty(0, 1) }
                    }
                }
            };

            return WrapSchema(schemaName, schema);
        }


        public static object CreateStarWritingWordPairResponseFormat()
        {
            Dictionary<string, object> twoWords = new Dictionary<string, object>
            {
                { "type", "array" }, { "minItems", 2 }, { "maxItems", 2 },
                { "items", StringProperty(80) }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "wordPairs" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "wordPairs", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 },
                                { "items", twoWords }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_writing_word_pairs", schema);
        }

        public static object CreateStarWritingObjectActionResponseFormat()
        {
            Dictionary<string, object> objectAction = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "object", "normalActions" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "object", StringProperty(80) },
                        { "normalActions", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 3 }, { "maxItems", 3 },
                                { "items", StringProperty(80) }
                            }
                        }
                    }
                }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "objects" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "objects", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 },
                                { "items", objectAction }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_writing_object_actions", schema);
        }

        public static object CreateStarWritingDisplacementCompositionResponseFormat()
        {
            Dictionary<string, object> item = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "noun", "normalVerb", "displacedVerb", "writing" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "noun", StringProperty(80) },
                        { "normalVerb", StringProperty(80) },
                        { "displacedVerb", StringProperty(80) },
                        { "writing", StringProperty(1400) }
                    }
                }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "items" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "items", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 },
                                { "items", item }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_writing_verb_noun_displacement", schema);
        }

        public static object CreateStarWritingCompositionResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "jokes" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "jokes", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 },
                                { "items", StringProperty(1400) }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_writing_composition", schema);
        }

        public static object CreateStarWritingGenerationResponseFormat()
        {
            Dictionary<string, object> twoWords = new Dictionary<string, object>
            {
                { "type", "array" }, { "minItems", 2 }, { "maxItems", 2 },
                { "items", StringProperty(80) }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "jokes", "usedWords" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "jokes", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 },
                                { "items", StringProperty(1400) }
                            }
                        },
                        { "usedWords", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 },
                                { "items", twoWords }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_writing_generation", schema);
        }

        public static object CreateStarWritingImprovementResponseFormat()
        {
            Dictionary<string, object> replacement = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "id", "oldScore", "newWordSet", "newWriting", "newScore" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "id", StringProperty(80) },
                        { "oldScore", NumberProperty(0, 100) },
                        { "newWordSet", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 2 }, { "maxItems", 2 },
                                { "items", StringProperty(80) }
                            }
                        },
                        { "newWriting", StringProperty(1400) },
                        { "newScore", NumberProperty(0, 100) }
                    }
                }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "array" },
                { "minItems", 0 },
                { "maxItems", 5 },
                { "items", replacement }
            };
            return WrapSchema("six_stars_writing_improvement_pass", schema);
        }

        public static object CreateStarWritingStoryTermContributionResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "subjects", "verbs" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "subjects", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 3 }, { "maxItems", 5 },
                                { "items", StringProperty(80) }
                            }
                        },
                        { "verbs", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 3 }, { "maxItems", 5 },
                                { "items", StringProperty(80) }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_story_term_contribution", schema);
        }

        public static object CreateStarWritingStoryOutlineResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "title", "premise", "beats" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "title", StringProperty(180) },
                        { "premise", StringProperty(1800) },
                        { "beats", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 6 }, { "maxItems", 14 },
                                { "items", StringProperty(1200) }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_story_outline", schema);
        }

        public static object CreateStarWritingStoryBuildResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "title", "revisedBeats", "story", "addedLayers", "completionDecision", "completionReason" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "title", StringProperty(180) },
                        { "revisedBeats", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 6 }, { "maxItems", 14 },
                                { "items", StringProperty(1200) }
                            }
                        },
                        { "story", StringProperty(24000) },
                        { "addedLayers", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 1 }, { "maxItems", 8 },
                                { "items", StringProperty(500) }
                            }
                        },
                        { "completionDecision", new Dictionary<string, object>
                            {
                                { "type", "string" },
                                { "enum", new[] { "continue", "request-finish", "approve-pending-finish", "deny-pending-finish" } }
                            }
                        },
                        { "completionReason", StringProperty(1000) }
                    }
                }
            };
            return WrapSchema("six_stars_story_build_phase", schema);
        }

        public static object CreateStarWritingStoryFinalResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "title", "story", "finalStep" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "title", StringProperty(180) },
                        { "story", StringProperty(24000) },
                        { "finalStep", new Dictionary<string, object> { { "type", "boolean" } } }
                    }
                }
            };
            return WrapSchema("six_stars_story_final", schema);
        }

        public static object CreateStarWritingStoryReviewResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "0" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "0", StringProperty(512) }
                    }
                }
            };
            return WrapSchema("six_stars_story_review", schema);
        }

        public static object CreateStarWritingReviewResponseFormat()
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            for (int i = 0; i < 5; i++)
            {
                // One compact score-only vector string per writing minimizes completion tokens.
                // Each value contains exactly 27 semicolon-delimited scores.
                properties[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = StringProperty(512);
            }

            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "0", "1", "2", "3", "4" } },
                { "properties", properties }
            };
            return WrapSchema("six_stars_writing_review", schema);
        }

        public static object CreateStarXPostSelectionResponseFormat()
        {
            Dictionary<string, object> post = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "writingId", "text" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "writingId", StringProperty(160) },
                        { "text", StringProperty(280) }
                    }
                }
            };
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "starId", "posts" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "posts", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 5 }, { "maxItems", 5 }, { "items", post }
                            }
                        }
                    }
                }
            };
            return WrapSchema("six_stars_x_post_selection", schema);
        }

        public static object CreateStarWritingPerformanceResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" }, { "additionalProperties", false },
                { "required", new[] { "starId", "selectedWritingIds", "spokenText" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "selectedWritingIds", new Dictionary<string, object>
                            {
                                { "type", "array" }, { "minItems", 1 }, { "maxItems", 5 }, { "items", StringProperty(120) }
                            }
                        },
                        { "spokenText", SpokenTextArray(5, 1800) }
                    }
                }
            };
            return WrapSchema("six_stars_writing_performance", schema);
        }

        public static object CreateDailyStarStateResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[]
                    {
                        "starId", "situationSummary", "feelingsSummary", "whatSheIsDoingToday", "currentDesires",
                        "relationshipPosture", "recentPeopleSeenSummary", "recentEventsSummary", "privateSecretsOrHooks",
                        "promptUseSummary", "moodTags", "recentPeopleSeen", "recentEvents", "todayIntentions", "thingsSheWantsToTalkAbout", "thingsSheWantsToSay", "conversationHooks"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "situationSummary", StringProperty(900) },
                        { "feelingsSummary", StringProperty(900) },
                        { "whatSheIsDoingToday", StringProperty(900) },
                        { "currentDesires", StringProperty(700) },
                        { "relationshipPosture", StringProperty(700) },
                        { "recentPeopleSeenSummary", StringProperty(700) },
                        { "recentEventsSummary", StringProperty(900) },
                        { "privateSecretsOrHooks", StringProperty(700) },
                        { "promptUseSummary", StringProperty(420) },
                        { "moodTags", StringArray(10) },
                        { "recentPeopleSeen", StringArray(12) },
                        { "recentEvents", StringArray(12) },
                        { "todayIntentions", StringArray(12) },
                        { "thingsSheWantsToTalkAbout", StringArray(12) },
                        { "thingsSheWantsToSay", SpeechIntentCandidateArray(12) },
                        { "conversationHooks", StringArray(12) }
                    }
                }
            };

            return WrapSchema("six_stars_daily_star_state", schema);
        }

        public static object CreateGirlfriendHourlyStateResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[]
                    {
                        "starId", "summary", "relationshipPosture", "needSummary", "wantSummary",
                        "needVector", "wantVector", "thingsSheWantsToSay", "cuteThingsSheWantsToSay",
                        "overallConfidence", "overallScore"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "summary", StringProperty(900) },
                        { "relationshipPosture", StringProperty(700) },
                        { "needSummary", StringProperty(700) },
                        { "wantSummary", StringProperty(700) },
                        { "needVector", GirlfriendVectorArray(30) },
                        { "wantVector", GirlfriendVectorArray(30) },
                        { "thingsSheWantsToSay", StringArray(16) },
                        { "cuteThingsSheWantsToSay", StringArray(16) },
                        { "overallConfidence", NumberProperty(0, 1) },
                        { "overallScore", NumberProperty(0, 100) }
                    }
                }
            };

            return WrapSchema("six_stars_girlfriend_hourly_state", schema);
        }

        public static object CreateInformationNeedSelectionResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "shouldRequestMoreInformation", "selectedDataType", "subject", "confidence", "score", "reason", "rankedNeeds" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "shouldRequestMoreInformation", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "selectedDataType", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "history", "quotes", "markers", "lore", "bestiary", "characters", "world", "screen", "story" } } } },
                        { "subject", StringProperty(160) },
                        { "confidence", NumberProperty(0, 1) },
                        { "score", NumberProperty(0, 100) },
                        { "reason", StringProperty(420) },
                        { "rankedNeeds", InformationNeedArray(9) }
                    }
                }
            };

            return WrapSchema("six_stars_information_need_selector", schema);
        }

        public static object CreateBackgroundReviewResponseFormat(string schemaName = "six_stars_background_memory_review_compact")
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[]
                    {
                        "starId", "emotionalEffectVector", "negativeEmotionVector", "storyTitle", "storyId", "storySummary", "storyIsFinished", "storyPartIndex",
                        "detectedCombos", "suggestedStateDeltas", "conversationSummary", "changesSummary",
                        "importantPlayerQuotes", "memoryNotes", "rememberedMarkers", "referencedMemoryMarkerIds", "secretsUnlocked", "learnedFunctions", "relationshipEvents",
                        "girlfriendHistoryUpdates", "petNamesPlayerUsesForStar", "petNamesStarUsesForPlayer",
                        "girlfriendNeedsMetSummary", "girlfriendNeedsMetVector", "girlfriendWantsMetVector", "girlfriendCoupleActivitySummary", "girlfriendCoupleActivityVector",
                        "requestToShowBoobs", "loreUsed", "riskFlags", "wisdomScore"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "spokenText", SpokenTextArray(maxItems: 0, maxItemLength: 0) },
                        { "emotionalEffectVector", EmotionEffectVectorProperty() },
                        { "negativeEmotionVector", NegativeEmotionVectorProperty() },
                        { "storyTitle", NullableStringProperty() },
                        { "storyId", StringProperty() },
                        { "storySummary", StringProperty(700) },
                        { "storyIsFinished", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "storyPartIndex", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 0 } } },
                        { "detectedCombos", StringArray(4) },
                        { "suggestedStateDeltas", DeltaObject() },
                        { "conversationSummary", StringProperty(900) },
                        { "changesSummary", StringProperty(450) },
                        { "importantPlayerQuotes", EmptyStringArray() },
                        { "memoryNotes", StringArray(6) },
                        { "rememberedMarkers", RememberedMarkerArray(4) },
                        { "referencedMemoryMarkerIds", StringArray(6) },
                        { "secretsUnlocked", StringArray(3) },
                        { "learnedFunctions", LearnedFunctionArray(3) },
                        { "relationshipEvents", StringArray(4) },
                        { "girlfriendHistoryUpdates", StringArray(3) },
                        { "petNamesPlayerUsesForStar", StringArray(3) },
                        { "petNamesStarUsesForPlayer", StringArray(3) },
                        { "girlfriendNeedsMetSummary", StringProperty(420) },
                        { "girlfriendNeedsMetVector", GirlfriendVectorArray(12) },
                        { "girlfriendWantsMetVector", GirlfriendVectorArray(12) },
                        { "girlfriendCoupleActivitySummary", StringProperty(520) },
                        { "girlfriendCoupleActivityVector", StringProperty(1200) },
                        { "requestToShowBoobs", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "loreUsed", StringArray(5) },
                        { "riskFlags", StringArray(3) },
                        { "wisdomScore", NumberProperty(0, 1) }
                    }
                }
            };

            return WrapSchema(schemaName, schema);
        }

        public static object CreateDecisionScoreResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "starId", "sceneKey", "scoreOrder", "scores", "confidence", "reasonCode", "shortExplanation" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "sceneKey", StringProperty(160) },
                        { "scoreOrder", StringProperty(320) },
                        { "scores", StringProperty(160) },
                        { "confidence", NumberProperty(0, 100) },
                        { "reasonCode", StringProperty(140) },
                        { "shortExplanation", StringProperty(360) }
                    }
                }
            };

            return WrapSchema("six_stars_decision_impulse_scores", schema);
        }

        public static object CreateSpeechResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "starId", "spokenText", "needsMoreInformation", "informationNeeds", "thingsLearnedOrSuggestedByPlayer" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "spokenText", SpokenTextArray(maxItems: 0, maxItemLength: 0) },
                        { "needsMoreInformation", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "informationNeeds", InformationNeedArray(9) },
                        { "thingsLearnedOrSuggestedByPlayer", StringArray(8) }
                    }
                }
            };

            return WrapSchema("six_stars_fast_spoken_text_only", schema);
        }

        public static object CreateStarInitiativeResponseFormat()
        {
            // This path runs from the phone's manual Listen button and the at-rest scheduler.
            // Keep the grammar focused, but allow a small complete spontaneous moment rather than
            // forcing every initiative response into one minimal line. The single decision vector
            // feeds the deterministic score/confidence gate; it is not a relationship update.
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "starId", "hasSomethingToSay", "priority", "speechDecisionVector", "selectedTopic", "reason", "spokenText" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty(80) },
                        { "hasSomethingToSay", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "priority", NumberProperty(0, 1) },
                        { "speechDecisionVector", GirlfriendVectorArray(1) },
                        { "selectedTopic", StringProperty(160) },
                        { "reason", StringProperty(260) },
                        { "spokenText", StarInitiativeSpokenTextArray(3) }
                    }
                }
            };

            return WrapSchema("six_stars_star_initiative_check", schema);
        }


        public static object CreateInterstarRelayResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "originStarId", "targetStarId", "shouldTell", "priority", "summary", "whatOriginWouldTellTarget", "reason", "cooldownKey" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "originStarId", StringProperty(80) },
                        { "targetStarId", StringProperty(80) },
                        { "shouldTell", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "priority", NumberProperty(0, 1) },
                        { "summary", StringProperty(360) },
                        { "whatOriginWouldTellTarget", StringProperty(900) },
                        { "reason", StringProperty(360) },
                        { "cooldownKey", StringProperty(120) }
                    }
                }
            };

            return WrapSchema("six_stars_interstar_relay", schema);
        }

        public static object CreateInterstarVectorResponseFormat(string schemaName = "six_stars_interstar_vector_update")
        {
            return CreateInterstarFlatVectorResponseFormat(schemaName, Array.Empty<string>(), includeSourceStarAliases: true);
        }

        public static object CreateInterstarReactionVectorResponseFormat(string sourceStarId, string targetStarId, string schemaName = "six_stars_interstar_reaction_vector")
        {
            List<string> starAliases = new List<string>();
            if (!string.IsNullOrWhiteSpace(sourceStarId)) starAliases.Add(sourceStarId);
            return CreateInterstarFlatVectorResponseFormat(schemaName, starAliases, includeSourceStarAliases: true);
        }

        public static object CreateInterstarFeedbackVectorResponseFormat(IEnumerable<string> respondingStarIds, string schemaName = "six_stars_interstar_origin_feedback_vector")
        {
            return CreateInterstarFlatVectorResponseFormat(schemaName, respondingStarIds ?? Array.Empty<string>(), includeSourceStarAliases: false);
        }

        private static object CreateInterstarFlatVectorResponseFormat(string schemaName, IEnumerable<string> interstarStarIds, bool includeSourceStarAliases)
        {
            List<string> required = new List<string>
            {
                "starId",
                "lastCatEmotionalEffectVector",
                "lastCatNegativeEmotionVector",
                "suggestedStateDeltas",
                "conversationSummary",
                "changesSummary",
                "memoryNotes",
                "relationshipEvents",
                "loreUsed",
                "riskFlags",
                "wisdomScore"
            };

            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "starId", StringProperty(80) },
                { "lastCatEmotionalEffectVector", EmotionEffectVectorProperty() },
                { "lastCatNegativeEmotionVector", NegativeEmotionVectorProperty() },
                { "emotionalEffectVector", EmotionEffectVectorProperty() },
                { "negativeEmotionVector", NegativeEmotionVectorProperty() },
                { "sourceStarEmotionalEffectVector", EmotionEffectVectorProperty() },
                { "sourceStarNegativeEmotionVector", NegativeEmotionVectorProperty() },
                { "sourceStarNegativeEmotionalEffectVector", NegativeEmotionVectorProperty() },
                { "sourceStarPositiveEmotionVector", EmotionEffectVectorProperty() },
                { "interstarEmotionVectors", InterstarEmotionVectorArray(0) },
                { "suggestedStateDeltas", DeltaObject() },
                { "conversationSummary", StringProperty(900) },
                { "changesSummary", StringProperty(500) },
                { "replyToSourceStar", StringProperty(500) },
                { "memoryNotes", StringArray(5) },
                { "relationshipEvents", StringArray(5) },
                { "loreUsed", StringArray(5) },
                { "riskFlags", StringArray(3) },
                { "wisdomScore", NumberProperty(0, 1) }
            };

            if (includeSourceStarAliases)
            {
                required.Add("sourceStarEmotionalEffectVector");
                required.Add("sourceStarNegativeEmotionVector");
            }

            foreach (string starId in (interstarStarIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            {
                string alias = InterstarVectorAlias(starId);
                if (string.IsNullOrWhiteSpace(alias)) continue;
                string positiveName = alias + "EmotionalEffectVector";
                string negativeName = alias + "NegativeEmotionalEffectVector";
                properties[positiveName] = EmotionEffectVectorProperty();
                properties[negativeName] = NegativeEmotionVectorProperty();
                required.Add(positiveName);
                required.Add(negativeName);
            }

            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", required.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
                { "properties", properties }
            };

            return WrapSchema(schemaName, schema);
        }

        public static object CreateStorySpeechResponseFormat(bool continuation)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "starId", StringProperty() },
                { "spokenText", SpokenTextArray(6) },
                { "storyTitle", StringProperty(160) },
                { "storyId", StringProperty(120) },
                { "storyMarkdown", StringProperty() },
                { "storySummary", StringProperty(900) },
                { "storyIsFinished", new Dictionary<string, object> { { "type", "boolean" } } },
                { "storyPartIndex", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 0 } } },
                { "thingsLearnedOrSuggestedByPlayer", StringArray(8) }
            };

            string[] required = continuation
                ? new[] { "starId", "spokenText", "storyIsFinished", "storyPartIndex", "thingsLearnedOrSuggestedByPlayer" }
                : new[] { "starId", "spokenText", "storyTitle", "storyId", "storyMarkdown", "storySummary", "storyIsFinished", "storyPartIndex", "thingsLearnedOrSuggestedByPlayer" };

            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", required },
                { "properties", properties }
            };

            return WrapSchema(continuation ? "six_stars_story_continuation_spoken_text" : "six_stars_story_plan_and_first_spoken_text", schema);
        }

        private static Dictionary<string, object> VoiceDirectiveObject()
        {
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[]
                    {
                        "voiceId", "voiceDesignPrompt", "deliveryInstructions", "primaryEmotion", "emotionIntensity",
                        "pace", "pitch", "volume", "prosody", "language", "audioTags", "avoid"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "voiceId", StringProperty(120) },
                        { "voiceDesignPrompt", StringProperty(900) },
                        { "deliveryInstructions", StringProperty(700) },
                        { "primaryEmotion", StringProperty(80) },
                        { "emotionIntensity", NumberProperty(0, 1) },
                        { "pace", StringProperty(80) },
                        { "pitch", StringProperty(80) },
                        { "volume", StringProperty(80) },
                        { "prosody", StringProperty(220) },
                        { "language", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "Auto", "English", "Chinese", "French", "German", "Spanish", "Italian", "Japanese", "Korean", "Portuguese", "Russian" } } } },
                        { "audioTags", StringArray(5) },
                        { "avoid", StringArray(5) }
                    }
                }
            };
        }



        private static Dictionary<string, object> InterstarEmotionVectorArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "additionalProperties", false },
                        { "required", new[] { "starId", "positiveEmotionVector", "negativeEmotionVector", "reason" } },
                        { "properties", new Dictionary<string, object>
                            {
                                { "starId", StringProperty(80) },
                                { "positiveEmotionVector", EmotionEffectVectorProperty() },
                                { "negativeEmotionVector", NegativeEmotionVectorProperty() },
                                { "reason", StringProperty(420) }
                            }
                        }
                    }
                }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> SpeechIntentCandidateArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "additionalProperties", false },
                        { "required", new[] { "intentId", "category", "topic", "speechGoal", "baseScore", "priority", "confidence", "sourceEventIds", "dedupeKey", "expiresAfterHours", "requiresAcknowledgement" } },
                        { "properties", new Dictionary<string, object>
                            {
                                { "intentId", StringProperty(160) },
                                { "category", StringProperty(80) },
                                { "topic", StringProperty(240) },
                                { "speechGoal", StringProperty(900) },
                                { "baseScore", NumberProperty(0, 100) },
                                { "priority", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "low", "medium", "high", "critical" } } } },
                                { "confidence", NumberProperty(0, 1) },
                                { "sourceEventIds", StringArray(16) },
                                { "dedupeKey", StringProperty(200) },
                                { "expiresAfterHours", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 1 }, { "maximum", 720 } } },
                                { "requiresAcknowledgement", new Dictionary<string, object> { { "type", "boolean" } } }
                            }
                        }
                    }
                }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> GirlfriendVectorArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "additionalProperties", false },
                        { "required", new[] { "itemId", "displayName", "confidence", "score", "reason" } },
                        { "properties", new Dictionary<string, object>
                            {
                                { "itemId", StringProperty(120) },
                                { "displayName", StringProperty(160) },
                                { "confidence", NumberProperty(0, 1) },
                                { "score", NumberProperty(0, 100) },
                                { "reason", StringProperty(360) }
                            }
                        }
                    }
                }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> InformationNeedArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "additionalProperties", false },
                        { "required", new[] { "subject", "confidence", "score" } },
                        { "properties", new Dictionary<string, object>
                            {
                                { "subject", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "history", "quotes", "markers", "lore", "bestiary", "characters", "world", "screen", "story" } } } },
                                { "confidence", NumberProperty(0, 1) },
                                { "score", NumberProperty(0, 100) }
                            }
                        }
                    }
                }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> RememberedMarkerArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "additionalProperties", false },
                        { "required", new[] { "id", "markerName", "description", "locationText", "coordinates", "area", "sourcePlayerQuote", "reminderText", "confidence", "isActive", "tags" } },
                        { "properties", new Dictionary<string, object>
                            {
                                { "id", StringProperty(140) },
                                { "markerName", StringProperty(160) },
                                { "description", StringProperty(500) },
                                { "locationText", StringProperty(300) },
                                { "coordinates", StringProperty(120) },
                                { "area", StringProperty(120) },
                                { "sourcePlayerQuote", StringProperty(500) },
                                { "reminderText", StringProperty(600) },
                                { "confidence", NumberProperty(0, 1) },
                                { "isActive", new Dictionary<string, object> { { "type", "boolean" } } },
                                { "tags", StringArray(6) }
                            }
                        }
                    }
                }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> LearnedFunctionArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "additionalProperties", false },
                        { "required", new[] { "id", "name", "description", "sourcePlayerQuote", "confidence", "isActive", "tags" } },
                        { "properties", new Dictionary<string, object>
                            {
                                { "id", StringProperty(140) },
                                { "name", StringProperty(120) },
                                { "description", StringProperty(360) },
                                { "sourcePlayerQuote", StringProperty(300) },
                                { "confidence", NumberProperty(0, 1) },
                                { "isActive", new Dictionary<string, object> { { "type", "boolean" } } },
                                { "tags", StringArray(5) }
                            }
                        }
                    }
                }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> SpokenTextArray(int maxItems = 0, int maxItemLength = 420)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "minItems", 1 },
                { "items", StringProperty(maxItemLength) }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> StarInitiativeSpokenTextArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", StringProperty(500) }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> EmptyStringArray()
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", StringProperty(1) },
                { "maxItems", 0 }
            };
        }

        private static Dictionary<string, object> StringArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "items", StringProperty(500) }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
        }

        private static Dictionary<string, object> EmotionEffectVectorProperty()
        {
            return StringProperty(700);
        }

        private static Dictionary<string, object> NegativeEmotionVectorProperty()
        {
            return StringProperty(220);
        }

        private static Dictionary<string, object> StringProperty(int maxLength = 0)
        {
            Dictionary<string, object> property = new Dictionary<string, object> { { "type", "string" } };
            if (maxLength > 0) property["maxLength"] = maxLength;
            return property;
        }

        private static Dictionary<string, object> NullableStringProperty(int maxLength = 0)
        {
            Dictionary<string, object> property = new Dictionary<string, object> { { "type", new[] { "string", "null" } } };
            if (maxLength > 0) property["maxLength"] = maxLength;
            return property;
        }

        private static Dictionary<string, object> NumberProperty(double min, double max)
        {
            return new Dictionary<string, object> { { "type", "number" }, { "minimum", min }, { "maximum", max } };
        }

        private static Dictionary<string, object> DeltaObject()
        {
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "heart", "trust", "romance", "emotionalConnectionPercent" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "heart", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -8 }, { "maximum", 8 } } },
                        { "trust", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -8 }, { "maximum", 8 } } },
                        { "romance", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -6 }, { "maximum", 6 } } },
                        { "emotionalConnectionPercent", NumberProperty(-5, 5) }
                    }
                }
            };
        }

        private static string InterstarVectorAlias(string starId)
        {
            if (string.IsNullOrWhiteSpace(starId)) return string.Empty;
            string compact = new string(starId.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(compact)) return string.Empty;
            return char.ToLowerInvariant(compact[0]) + (compact.Length > 1 ? compact.Substring(1) : string.Empty);
        }

        private static object WrapSchema(string schemaName, Dictionary<string, object> schema)
        {
            return new Dictionary<string, object>
            {
                { "type", "json_schema" },
                { "json_schema", new Dictionary<string, object>
                    {
                        { "name", schemaName },
                        { "strict", true },
                        { "schema", schema }
                    }
                }
            };
        }
    }
}
