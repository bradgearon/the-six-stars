using System.Collections.Generic;
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
                        "importantPlayerQuotes", "memoryNotes", "secretsUnlocked", "learnedFunctions", "relationshipEvents",
                        "girlfriendHistoryUpdates", "petNamesPlayerUsesForStar", "petNamesStarUsesForPlayer",
                        "loreUsed", "riskFlags", "wisdomScore"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "spokenText", SpokenTextArray(6) },
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
                        { "importantPlayerQuotes", StringArray(8) },
                        { "memoryNotes", StringArray(10) },
                        { "secretsUnlocked", StringArray(6) },
                        { "learnedFunctions", LearnedFunctionArray(6) },
                        { "relationshipEvents", StringArray(8) },
                        { "girlfriendHistoryUpdates", StringArray(6) },
                        { "petNamesPlayerUsesForStar", StringArray(5) },
                        { "petNamesStarUsesForPlayer", StringArray(5) },
                        { "loreUsed", StringArray(8) },
                        { "riskFlags", StringArray(5) },
                        { "wisdomScore", NumberProperty(0, 1) }
                    }
                }
            };

            return WrapSchema(schemaName, schema);
        }


        public static object CreateStoryIntentResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "isStoryRequest", "confidence", "storySeed", "reason" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "isStoryRequest", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "confidence", NumberProperty(0, 1) },
                        { "storySeed", StringProperty(500) },
                        { "reason", StringProperty(180) }
                    }
                }
            };

            return WrapSchema("six_stars_story_intent_classifier", schema);
        }

        public static object CreateBackgroundReviewResponseFormat(string schemaName = "six_stars_background_memory_review_compact")
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[]
                    {
                        "starId", "spokenText", "storyTitle", "storyId", "storySummary", "storyIsFinished", "storyPartIndex",
                        "detectedCombos", "suggestedStateDeltas", "conversationSummary", "changesSummary",
                        "importantPlayerQuotes", "memoryNotes", "secretsUnlocked", "learnedFunctions", "relationshipEvents",
                        "girlfriendHistoryUpdates", "petNamesPlayerUsesForStar", "petNamesStarUsesForPlayer",
                        "loreUsed", "riskFlags", "wisdomScore"
                    }
                },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "spokenText", SpokenTextArray(6) },
                        { "storyTitle", NullableStringProperty() },
                        { "storyId", StringProperty() },
                        { "storySummary", StringProperty(700) },
                        { "storyIsFinished", new Dictionary<string, object> { { "type", "boolean" } } },
                        { "storyPartIndex", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 0 } } },
                        { "detectedCombos", StringArray(4) },
                        { "suggestedStateDeltas", DeltaObject() },
                        { "conversationSummary", StringProperty(900) },
                        { "changesSummary", StringProperty(450) },
                        { "importantPlayerQuotes", StringArray(4) },
                        { "memoryNotes", StringArray(6) },
                        { "secretsUnlocked", StringArray(3) },
                        { "learnedFunctions", LearnedFunctionArray(3) },
                        { "relationshipEvents", StringArray(4) },
                        { "girlfriendHistoryUpdates", StringArray(3) },
                        { "petNamesPlayerUsesForStar", StringArray(3) },
                        { "petNamesStarUsesForPlayer", StringArray(3) },
                        { "loreUsed", StringArray(5) },
                        { "riskFlags", StringArray(3) },
                        { "wisdomScore", NumberProperty(0, 1) }
                    }
                }
            };

            return WrapSchema(schemaName, schema);
        }

        public static object CreateSpeechResponseFormat()
        {
            Dictionary<string, object> schema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "additionalProperties", false },
                { "required", new[] { "starId", "spokenText", "emotionalEffectVector", "negativeEmotionVector" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "starId", StringProperty() },
                        { "spokenText", SpokenTextArray(5) },
                        { "emotionalEffectVector", EmotionEffectVectorProperty() },
                        { "negativeEmotionVector", NegativeEmotionVectorProperty() }
                    }
                }
            };

            return WrapSchema("six_stars_fast_spoken_text_only", schema);
        }

        public static object CreateStorySpeechResponseFormat(bool continuation)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>
            {
                { "starId", StringProperty() },
                { "spokenText", SpokenTextArray(6) },
                { "emotionalEffectVector", EmotionEffectVectorProperty() },
                        { "negativeEmotionVector", NegativeEmotionVectorProperty() },
                { "storyTitle", StringProperty(160) },
                { "storyId", StringProperty(120) },
                { "storyMarkdown", StringProperty() },
                { "storySummary", StringProperty(900) },
                { "storyIsFinished", new Dictionary<string, object> { { "type", "boolean" } } },
                { "storyPartIndex", new Dictionary<string, object> { { "type", "integer" }, { "minimum", 0 } } }
            };

            string[] required = continuation
                ? new[] { "starId", "spokenText", "emotionalEffectVector", "negativeEmotionVector", "storyIsFinished", "storyPartIndex" }
                : new[] { "starId", "spokenText", "emotionalEffectVector", "negativeEmotionVector", "storyTitle", "storyId", "storyMarkdown", "storySummary", "storyIsFinished", "storyPartIndex" };

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

        private static Dictionary<string, object> SpokenTextArray(int maxItems = 0)
        {
            Dictionary<string, object> array = new Dictionary<string, object>
            {
                { "type", "array" },
                { "minItems", 1 },
                { "items", StringProperty(420) }
            };
            if (maxItems > 0) array["maxItems"] = maxItems;
            return array;
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
                { "required", new[] { "heart", "trust", "romance", "benaveilInfluence", "emotionalConnectionPercent" } },
                { "properties", new Dictionary<string, object>
                    {
                        { "heart", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -8 }, { "maximum", 8 } } },
                        { "trust", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -8 }, { "maximum", 8 } } },
                        { "romance", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -6 }, { "maximum", 6 } } },
                        { "benaveilInfluence", new Dictionary<string, object> { { "type", "integer" }, { "minimum", -6 }, { "maximum", 6 } } },
                        { "emotionalConnectionPercent", NumberProperty(-5, 5) }
                    }
                }
            };
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
