using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.Actors;

namespace Wds.SixStars.Api.Stars
{
    public sealed class FileStarProfileProvider : IStarProfileProvider
    {
        private readonly Dictionary<string, StarProfile> _profiles;

        public FileStarProfileProvider(InstructionLibrary instructionLibrary, IEnumerable<string>? starIds = null)
        {
            if (instructionLibrary == null) throw new ArgumentNullException(nameof(instructionLibrary));

            string[] ids = starIds != null
                ? starIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : DiscoverStarIds(instructionLibrary.ContentRootPath);

            List<StarProfile> profiles = new List<StarProfile>();
            foreach (string id in ids)
            {
                StarProfile profile = instructionLibrary.LoadStarProfile(id);
                string fileId = id.Trim();
                if (string.IsNullOrWhiteSpace(profile.StarId))
                {
                    throw new InvalidOperationException("Star profile '" + fileId + "' is missing starId.");
                }
                if (!string.Equals(fileId, profile.StarId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Star JSON filename must match starId. File='" + fileId + "', starId='" + profile.StarId + "'.");
                }
                if (!profile.Active) continue;
                if (!string.Equals(profile.ActorType ?? string.Empty, "star", StringComparison.OrdinalIgnoreCase)) continue;

                profile.ActorId = string.IsNullOrWhiteSpace(profile.ActorId) ? profile.StarId : profile.ActorId.Trim();
                profile.Soul ??= ActorSoulConfig.FullStarSoul();
                if (profile.Emotions.Count == 0)
                {
                    profile.Emotions = StarEmotionMatrix.CreateStandardEmotionSet(profile.StarId);
                }
                StarRelationshipPersonalityMatrix.EnsureDeterministicRelationshipConfig(profile);
                profiles.Add(profile);
            }

            _profiles = profiles
                .Where(x => x != null && x.Active && !string.IsNullOrWhiteSpace(x.StarId))
                .GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToDictionary(x => x.StarId, x => x, StringComparer.OrdinalIgnoreCase);
        }

        public StarProfile GetProfile(string starId)
        {
            if (_profiles.TryGetValue(starId ?? string.Empty, out StarProfile? profile))
            {
                return profile;
            }

            throw new ArgumentException("Unknown or inactive StarId: " + starId, nameof(starId));
        }

        public IReadOnlyList<StarProfile> GetAllProfiles()
        {
            return _profiles.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string[] DiscoverStarIds(string contentRootPath)
        {
            string root = Path.Combine(contentRootPath, "Stars");
            if (!Directory.Exists(root)) return Array.Empty<string>();
            return Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }

        public static IStarProfileProvider TryCreateDefaultOrFallback()
        {
            string contentRoot = InstructionLibrary.FindDefaultContentRoot();
            string starRoot = Path.Combine(contentRoot, "Stars");
            bool hasConfiguredStars = Directory.Exists(starRoot)
                && Directory.EnumerateFiles(starRoot, "*.json", SearchOption.TopDirectoryOnly).Any();

            // Once a JSON roster exists it is authoritative. Configuration errors must surface
            // instead of silently resurrecting the built-in six-Star fallback roster.
            if (hasConfiguredStars)
            {
                return new FileStarProfileProvider(new InstructionLibrary(contentRoot));
            }

            return new DefaultStarProfileProvider();
        }
    }
}
