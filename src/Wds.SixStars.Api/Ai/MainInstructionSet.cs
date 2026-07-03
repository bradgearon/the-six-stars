using System.Collections.Generic;

namespace Wds.SixStars.Api.Ai
{
    public sealed class MainInstructionSet
    {
        public string Version { get; set; } = "dev";
        public string SystemPrompt { get; set; } = string.Empty;
        public Dictionary<string, string> TaskInstructions { get; set; } = new Dictionary<string, string>();
        public List<string> RequiredEmotionNames { get; set; } = new List<string>();
        public string ResponseContractNotes { get; set; } = string.Empty;
    }
}
