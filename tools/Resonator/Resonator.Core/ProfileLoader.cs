using System.Text.Json;

namespace Resonator.Core;

public static class ProfileLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ResonatorProfile Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new ResonatorProfile();

        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<ResonatorProfile>(json, Options) ?? new ResonatorProfile();
        Normalize(profile);
        return profile;
    }

    public static ResonatorProfile LoadJson(string json)
    {
        var profile = JsonSerializer.Deserialize<ResonatorProfile>(json, Options) ?? new ResonatorProfile();
        Normalize(profile);
        return profile;
    }

    private static void Normalize(ResonatorProfile profile)
    {
        profile.Articulations = new Dictionary<string, ArticulationDefinition>(profile.Articulations ?? new Dictionary<string, ArticulationDefinition>(), StringComparer.OrdinalIgnoreCase);
        profile.Percussion ??= new PercussionDefinition();
        profile.Percussion.NoteMap = new Dictionary<string, int>(profile.Percussion.NoteMap ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        profile.Percussion.Labels = new Dictionary<string, string>(profile.Percussion.Labels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        profile.ArticulationDefaults ??= new ArticulationDefaults();
    }
}
