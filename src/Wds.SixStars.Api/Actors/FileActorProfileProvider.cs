using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Actors
{
    /// <summary>
    /// Discovers active generic actors from Content/Actors/*.json and optionally merges active
    /// Stars into the same catalog. Actor JSON filenames must match actorId (case-insensitive).
    /// Adding a correctly named active JSON file is therefore enough to register a new actor.
    /// </summary>
    public sealed class FileActorProfileProvider : IActorProfileProvider
    {
        private readonly Dictionary<string, ActorProfile> _actors;

        public FileActorProfileProvider(InstructionLibrary instructionLibrary, IStarProfileProvider? stars = null)
        {
            if (instructionLibrary == null) throw new ArgumentNullException(nameof(instructionLibrary));
            List<ActorProfile> actors = new List<ActorProfile>();

            if (stars != null)
            {
                actors.AddRange(stars.GetAllProfiles()
                    .Where(x => x != null && x.Active)
                    .Select(ActorProfile.FromStarProfile));
            }

            string actorRoot = Path.Combine(instructionLibrary.ContentRootPath, "Actors");
            if (Directory.Exists(actorRoot))
            {
                JsonSerializerOptions options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                options.Converters.Add(new JsonStringEnumConverter());

                foreach (string path in Directory.EnumerateFiles(actorRoot, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string json = File.ReadAllText(path);
                    ActorProfile? actor = JsonSerializer.Deserialize<ActorProfile>(json, options);
                    if (actor == null || !actor.Active) continue;
                    string fileId = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(actor.ActorId)) actor.ActorId = fileId;
                    if (!string.Equals(fileId, actor.ActorId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Active actor profile filename must match actorId. File='" + fileId + "', actorId='" + actor.ActorId + "'.");
                    }
                    actor.DisplayName = string.IsNullOrWhiteSpace(actor.DisplayName) ? actor.ActorId : actor.DisplayName.Trim();
                    actor.ActorType = string.IsNullOrWhiteSpace(actor.ActorType) ? "actor" : actor.ActorType.Trim();
                    actor.Soul ??= new ActorSoulConfig();
                    if (actors.Any(x => x != null && string.Equals(x.ActorId, actor.ActorId, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("ActorId '" + actor.ActorId + "' is already registered. Star and generic actor IDs must be unique.");
                    }
                    actors.Add(actor);
                }
            }

            _actors = actors
                .Where(x => x != null && x.Active && !string.IsNullOrWhiteSpace(x.ActorId))
                .GroupBy(x => x.ActorId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        }

        public ActorProfile GetActor(string actorId)
        {
            if (_actors.TryGetValue(actorId ?? string.Empty, out ActorProfile? actor)) return actor;
            throw new ArgumentException("Unknown or inactive ActorId: " + actorId, nameof(actorId));
        }

        public IReadOnlyList<ActorProfile> GetAllActors()
        {
            return _actors.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
