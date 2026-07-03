using System;
using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stories;

namespace Wds.SixStars.Api.Stars
{
    public sealed class DefaultStarProfileProvider : IStarProfileProvider
    {
        private readonly Dictionary<string, StarProfile> _profiles;

        public DefaultStarProfileProvider()
        {
            List<StarProfile> profiles = new List<StarProfile>
            {
                CreateElusiaa(),
                CreateKaelia(),
                CreateMaera(),
                CreateKhavia(),
                CreateAuerlis(),
                CreateNyxari()
            };

            _profiles = profiles.ToDictionary(x => x.StarId, StringComparer.OrdinalIgnoreCase);
        }

        public StarProfile GetProfile(string starId)
        {
            if (_profiles.TryGetValue(starId, out StarProfile? profile))
            {
                return profile;
            }

            throw new ArgumentException("Unknown StarId: " + starId, nameof(starId));
        }

        public IReadOnlyList<StarProfile> GetAllProfiles()
        {
            return _profiles.Values.ToList();
        }

        private static StarProfile CreateElusiaa()
        {
            return new StarProfile
            {
                StarId = "Elusiaa",
                DisplayName = "Elusiaa",
                Element = "Light",
                Domain = "Mercy",
                CoreQuote = "Mercy is how we remember we are not gods.",
                VoiceDirection = "gentle, warm, ancient strength, soft radiant correction",
                VoiceId = "elusiaa-dev",
                VoiceDesignPrompt = "A warm luminous young woman with ancient gentleness, soft clear English, calm breath, protective tenderness, and quiet holy confidence.",
                StoryThinkingPhrases = CreateStoryThinkingPhrases("Elusiaa"),
                Emotions = StarEmotionMatrix.CreateDefault27("Elusiaa"),
                StoryDeck = new List<StoryMemoryCard>
                {
                    new StoryMemoryCard
                    {
                        Id = "elusiaa_dying_valley_waterskin",
                        StarId = "Elusiaa",
                        Kind = StoryKind.MoralParable,
                        Summary = "Elusiaa once saw a good angel serve a drought-stricken valley with only a small waterskin, carrying the weak and singing to children until rain came.",
                        Themes = new List<string> { "mercy", "service", "thirst", "quiet_goodness" },
                        AllowedUses = new List<string> { "comfort", "wisdom", "story", "gentle_company" },
                        UnlockedAtBond = 0
                    },
                    new StoryMemoryCard
                    {
                        Id = "elusiaa_holy_goblin_prank",
                        StarId = "Elusiaa",
                        Kind = StoryKind.ComedyStory,
                        Summary = "Elusiaa and the Last Cat once considered casting holy light on a sleeping goblin after misunderstanding rough harpy play.",
                        Themes = new List<string> { "comedy", "mercy", "misunderstanding", "restraint" },
                        AllowedUses = new List<string> { "comedy", "bonding", "story" },
                        UnlockedAtBond = 2
                    }
                }
            };
        }

        private static StarProfile CreateKaelia()
        {
            return new StarProfile
            {
                StarId = "Kaelia",
                DisplayName = "Kaelia",
                Element = "Void",
                Domain = "Fatalism",
                CoreQuote = "Why try? It all ends the same.",
                VoiceDirection = "low, intimate, guarded, cosmic sadness with tiny sparks of hope",
                VoiceId = "kaelia-dev",
                VoiceDesignPrompt = "A low intimate void sorceress voice with guarded softness, cosmic sadness, dry wit, and fragile hope underneath.",
                StoryThinkingPhrases = CreateStoryThinkingPhrases("Kaelia"),
                Emotions = StarEmotionMatrix.CreateDefault27("Kaelia"),
                StoryDeck = new List<StoryMemoryCard>
                {
                    new StoryMemoryCard
                    {
                        Id = "kaelia_tar_water_reflection",
                        StarId = "Kaelia",
                        Kind = StoryKind.HorrorWarning,
                        Summary = "Kaelia remembers being forced to watch her reflection drown in water that turned to tar whenever she reached for the surface.",
                        Themes = new List<string> { "void", "fatalism", "reflection", "suffocation", "hope" },
                        AllowedUses = new List<string> { "horror", "comfort", "story", "secret" },
                        UnlockedAtBond = 0
                    }
                }
            };
        }

        private static StarProfile CreateMaera()
        {
            return new StarProfile
            {
                StarId = "Maera",
                DisplayName = "Maera",
                Element = "Blood",
                Domain = "Possession",
                CoreQuote = "If I cannot have you, no one can.",
                VoiceDirection = "rough, passionate, wounded, possessive, learning restraint",
                VoiceId = "maera-dev",
                VoiceDesignPrompt = "A passionate blood sorceress voice, rough-edged and emotional, intense but controlled, wounded underneath the fire.",
                StoryThinkingPhrases = CreateStoryThinkingPhrases("Maera"),
                Emotions = StarEmotionMatrix.CreateDefault27("Maera"),
                StoryDeck = new List<StoryMemoryCard>
                {
                    new StoryMemoryCard
                    {
                        Id = "maera_blood_caves_worms",
                        StarId = "Maera",
                        Kind = StoryKind.HorrorWarning,
                        Summary = "Maera remembers the blood caves, where a dark presence made her feel worms in her veins and taught her to hate being alive.",
                        Themes = new List<string> { "blood", "possession", "rage", "survival" },
                        AllowedUses = new List<string> { "horror", "battle_memory", "story" },
                        UnlockedAtBond = 0
                    }
                }
            };
        }

        private static StarProfile CreateKhavia()
        {
            return new StarProfile
            {
                StarId = "Khavia",
                DisplayName = "Khavia",
                Element = "Water",
                Domain = "Friendship",
                CoreQuote = "Somebody helped me once.",
                VoiceDirection = "flowing, hopeful, communal, emotionally perceptive",
                VoiceId = "khavia-dev",
                VoiceDesignPrompt = "A flowing water-star voice, emotionally perceptive, friendly, soothing, and gently musical.",
                StoryThinkingPhrases = CreateStoryThinkingPhrases("Khavia"),
                Emotions = StarEmotionMatrix.CreateDefault27("Khavia"),
                StoryDeck = new List<StoryMemoryCard>
                {
                    new StoryMemoryCard
                    {
                        Id = "khavia_blood_red_canyons",
                        StarId = "Khavia",
                        Kind = StoryKind.MoralParable,
                        Summary = "Khavia witnessed an angel sit with wounded enemies in blood-red canyons, healing them because there were no enemies there, only the hurt and lost.",
                        Themes = new List<string> { "friendship", "healing", "enemies", "bridges" },
                        AllowedUses = new List<string> { "comfort", "wisdom", "story" },
                        UnlockedAtBond = 0
                    }
                }
            };
        }

        private static StarProfile CreateAuerlis()
        {
            return new StarProfile
            {
                StarId = "Auerlis",
                DisplayName = "Auerlis",
                Element = "Fire",
                Domain = "Joy",
                CoreQuote = "Then at least they are laughing.",
                VoiceDirection = "fiery, intense, funny, musical, hopeful under fear",
                VoiceId = "auerlis-dev",
                VoiceDesignPrompt = "A fiery joyful sorceress voice, bright, musical, intense, funny, and alive.",
                StoryThinkingPhrases = CreateStoryThinkingPhrases("Auerlis"),
                Emotions = StarEmotionMatrix.CreateDefault27("Auerlis"),
                StoryDeck = new List<StoryMemoryCard>
                {
                    new StoryMemoryCard
                    {
                        Id = "auerlis_spirit_burning_shame",
                        StarId = "Auerlis",
                        Kind = StoryKind.HorrorWarning,
                        Summary = "Auerlis heard of a spirit whose failures and shame were torn open by a dark angel until he felt like he burned from within.",
                        Themes = new List<string> { "fire", "joy", "shame", "laughter", "survival" },
                        AllowedUses = new List<string> { "horror", "music", "story", "encouragement" },
                        UnlockedAtBond = 0
                    }
                }
            };
        }

        private static StarProfile CreateNyxari()
        {
            return new StarProfile
            {
                StarId = "Nyxari",
                DisplayName = "Nyxari",
                Element = "Shadow",
                Domain = "Fragmentation",
                CoreQuote = "If nothing matters, nothing can break.",
                VoiceDirection = "mysterious, quiet, elegant, fractured, poetic shadow warmth",
                VoiceId = "nyxari-dev",
                VoiceDesignPrompt = "A quiet shadow-star voice, elegant, mysterious, poetic, and fragmented but warm.",
                StoryThinkingPhrases = CreateStoryThinkingPhrases("Nyxari"),
                Emotions = StarEmotionMatrix.CreateDefault27("Nyxari"),
                StoryDeck = new List<StoryMemoryCard>
                {
                    new StoryMemoryCard
                    {
                        Id = "nyxari_lost_names_masked_traveler",
                        StarId = "Nyxari",
                        Kind = StoryKind.PoeticAbstraction,
                        Summary = "Nyxari tells shadow fables about masked travelers who lose their names and recover meaning one fragment at a time.",
                        Themes = new List<string> { "shadow", "identity", "fragmentation", "meaning" },
                        AllowedUses = new List<string> { "poetry", "comfort", "story" },
                        UnlockedAtBond = 0
                    }
                }
            };
        }

        private static List<string> CreateStoryThinkingPhrases(string starId)
        {
            switch (starId)
            {
                case "Elusiaa":
                    return new List<string>
                    {
                        "Just a moment, little one. I am gathering a story from the light.",
                        "Give me one soft breath. I am thinking of a story for us.",
                        "Stay close to the glow. I am finding the right tale to carry beside your work.",
                        "One moment, dear heart. I can feel a gentle story waking in the caves.",
                        "I hear you, little one. Let me draw a lantern-story from the Stream."
                    };
                case "Kaelia":
                    return new List<string>
                    {
                        "Wait a breath. I am listening for a story in the dark between things.",
                        "Give me a moment, warm little light. I am finding a tale the Void has not swallowed.",
                        "Stay near. I am shaping a story from the quiet places.",
                        "One moment. I am deciding which shadow wants to become a story.",
                        "Do not wander yet. I am pulling a tale from the edge of nothing."
                    };
                case "Maera":
                    return new List<string>
                    {
                        "Hold on, little star-cat. I am finding a story with a heartbeat.",
                        "Give me a moment. I want this tale to have teeth and tenderness.",
                        "Stay close. I am sorting the blood from the beauty in this story.",
                        "One breath. I am choosing a story that will not let go of us.",
                        "I hear you. Let me pull a living tale from the bones of this place."
                    };
                case "Khavia":
                    return new List<string>
                    {
                        "Just a moment, friend. I am letting a story gather like water.",
                        "Give me one breath. I am finding the current of the tale.",
                        "Stay with me. I am listening for the story that wants to bring us together.",
                        "One moment. The right story is flowing toward us now.",
                        "I hear you. Let me cup this little story carefully before it spills."
                    };
                case "Auerlis":
                    return new List<string>
                    {
                        "Give me a spark, little one. I am lighting a story for us.",
                        "Just a moment. I am finding a tale bright enough to keep you company.",
                        "Hold still, tiny legend. I am warming up the story-fire.",
                        "One breath. I want this story to laugh before it burns.",
                        "I hear you. Let me strike the first ember of a good tale."
                    };
                case "Nyxari":
                    return new List<string>
                    {
                        "One quiet moment. I am gathering the fragments of a story.",
                        "Stay close to the shadow. I am finding a tale that still remembers its name.",
                        "Give me a breath. The story is scattered, but I can hear where it broke.",
                        "Wait softly. I am stitching a little darkness into meaning.",
                        "I hear you. Let me collect the pieces before I speak them."
                    };
                default:
                    return new List<string>
                    {
                        "Just a moment. I am thinking of a story for you.",
                        "Give me one breath. I am gathering the shape of a story.",
                        "Stay close. I am finding the right tale to carry beside your work.",
                        "One soft moment. I am listening for the story beneath the cave.",
                        "I hear you. Let me draw a story from the stars for us."
                    };
            }
        }
    }
}
