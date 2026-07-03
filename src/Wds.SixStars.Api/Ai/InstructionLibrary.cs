using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Ai
{
    public sealed class InstructionLibrary
    {
        private readonly JsonSerializerOptions _jsonOptions;

        public InstructionLibrary(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            _jsonOptions.Converters.Add(new JsonStringEnumConverter());
        }

        public string ContentRootPath { get; }

        public MainInstructionSet LoadMainInstructions()
        {
            string path = Path.Combine(ContentRootPath, "Instructions", "main-instructions.json");
            if (!File.Exists(path))
            {
                return CreateFallbackMainInstructions();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MainInstructionSet>(json, _jsonOptions) ?? CreateFallbackMainInstructions();
        }

        public StarProfile LoadStarProfile(string starId)
        {
            string fileName = starId.ToLowerInvariant() + ".json";
            string path = Path.Combine(ContentRootPath, "Stars", fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Star JSON profile not found.", path);
            }

            string json = File.ReadAllText(path);
            StarProfile? profile = JsonSerializer.Deserialize<StarProfile>(json, _jsonOptions);
            if (profile == null)
            {
                throw new InvalidOperationException("Could not deserialize Star profile: " + path);
            }

            if (profile.Emotions.Count == 0)
            {
                profile.Emotions = StarEmotionMatrix.CreateStandardEmotionSet(profile.StarId);
            }

            return profile;
        }

        public static string FindDefaultContentRoot()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string direct = Path.Combine(baseDirectory, "Content");
            if (Directory.Exists(direct)) return direct;

            string current = baseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string candidate = Path.Combine(current, "src", "Wds.SixStars.Api", "Content");
                if (Directory.Exists(candidate)) return candidate;
                string? parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }

            return direct;
        }

        private static MainInstructionSet CreateFallbackMainInstructions()
        {
            MainInstructionSet set = new MainInstructionSet
            {
                Version = "fallback",
                SystemPrompt = "You are the current selected Six Stars character from The Last Cat. Stay in character, use the provided Star profile and game context, and return strict JSON only.",
                ResponseContractNotes = "Return only JSON matching the requested response contract. Include every required emotion score from 0 to 100 and confidence from 0 to 1."
            };
            foreach (string emotion in StarEmotionMatrix.StandardEmotionIds)
            {
                set.RequiredEmotionNames.Add(emotion);
            }
            set.TaskInstructions["Chat"] = "Respond naturally as the Star, evaluate emotions, and summarize memory changes.";
            set.TaskInstructions["Story"] = "Tell a canon-respecting story using the Star's past, people, unlocked secrets, or poetic abstractions.";
            set.TaskInstructions["Letter"] = "Evaluate the letter's lore and emotional effect, then respond as the Star.";
            set.TaskInstructions["BattleUpdate"] = "React to the battle update as an equipped companion.";
            set.TaskInstructions["Debug"] = "Return concise debug-oriented character output while preserving role.";
            return set;
        }
    }
}
