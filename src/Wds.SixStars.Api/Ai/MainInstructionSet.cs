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

        /// <summary>
        /// Editable fast foreground speech wording. Keeping this in main-instructions.json lets the
        /// prompt contract be tuned without changing C# and lets compact stream controls be turned
        /// back on without restoring a second hard-coded prompt path.
        /// </summary>
        public FastSpeechInstructionSet FastSpeech { get; set; } = new FastSpeechInstructionSet();
    }

    public sealed class FastSpeechInstructionSet
    {
        public string ResponseInstructions { get; set; } = "Return only the spoken reply and answer the Last Cat directly. Act any actions out in first person.";
        public bool IncludeControlItems { get; set; }
        public string IncludeControlItemsInstruction { get; set; } = "Also include any applicable control items defined below.";
        public List<string> ControlInstructions { get; set; } = new List<string>();
    }
}
