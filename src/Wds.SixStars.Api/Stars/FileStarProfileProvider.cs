using System;
using System.Collections.Generic;
using System.Linq;
using Wds.SixStars.Api.Ai;

namespace Wds.SixStars.Api.Stars
{
    public sealed class FileStarProfileProvider : IStarProfileProvider
    {
        private readonly Dictionary<string, StarProfile> _profiles;

        public FileStarProfileProvider(InstructionLibrary instructionLibrary, IEnumerable<string>? starIds = null)
        {
            string[] ids = (starIds ?? new[] { "Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari" }).ToArray();
            List<StarProfile> profiles = new List<StarProfile>();
            foreach (string id in ids)
            {
                StarProfile profile = instructionLibrary.LoadStarProfile(id);
                if (profile.Emotions.Count == 0)
                {
                    profile.Emotions = StarEmotionMatrix.CreateStandardEmotionSet(profile.StarId);
                }
                StarRelationshipPersonalityMatrix.EnsureDeterministicRelationshipConfig(profile);
                profiles.Add(profile);
            }

            _profiles = profiles.Where(x => x != null && !string.IsNullOrWhiteSpace(x.StarId)).GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
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

        public static IStarProfileProvider TryCreateDefaultOrFallback()
        {
            try
            {
                return new FileStarProfileProvider(new InstructionLibrary(InstructionLibrary.FindDefaultContentRoot()));
            }
            catch
            {
                return new DefaultStarProfileProvider();
            }
        }
    }
}
