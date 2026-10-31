using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Ai;

namespace Wds.SixStars.Api.Music
{
    public sealed class MusicCompositionRequest
    {
        public string Description { get; set; } = "";
        public string ExistingNotation { get; set; } = "";
        public string OriginalBrief { get; set; } = "";
        public int Bars { get; set; } = 8;
        public int Tempo { get; set; } = 96;
        public string Meter { get; set; } = "4/4";
        public bool UseAhd { get; set; } = true;
    }

    public sealed class MusicCompositionResult
    {
        public string InstructionType { get; set; } = "MusicComposition";
        public string Notation { get; set; } = "";
        public string ValidationProfile { get; set; } = "resonator-core-v1";
        public int ActualBars { get; set; }
        public int Attempts { get; set; }
        public string InstructionVersion { get; set; } = "";
    }

    public sealed class MusicCompositionInstructions
    {
        public string InstructionType { get; set; } = "MusicComposition";
        public string Version { get; set; } = "1";
        public string SystemPrompt { get; set; } = "";
        public string ExecutableProfile { get; set; } = "";
        public string References { get; private set; } = "";
        public static MusicCompositionInstructions Load(string contentRoot)
        {
            string folder = Path.Combine(contentRoot, "Instructions", "Music");
            var value = JsonSerializer.Deserialize<MusicCompositionInstructions>(
                File.ReadAllText(Path.Combine(folder, "music-composition.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Music composition instructions are empty.");
            if (string.IsNullOrWhiteSpace(value.SystemPrompt) || string.IsNullOrWhiteSpace(value.ExecutableProfile))
                throw new InvalidDataException("Music composition instructions require a system prompt and executable profile.");
            var references = new StringBuilder();
            foreach (string name in new[] { "interval_emotion_field_guide.md", "anchored_harmonic_divergence.md", "resonator_note_string_language.md", "resonator_api_v0.1.md" })
            {
                string text = File.ReadAllText(Path.Combine(folder, name));
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Music reference is empty: " + name);
                references.AppendLine("\nREFERENCE: " + name).AppendLine(text);
            }
            value.References = references.ToString();
            return value;
        }
    }

    public sealed class MusicGenerationException : Exception
    {
        public IReadOnlyList<string> Errors { get; }
        public MusicGenerationException(IReadOnlyList<string> errors)
            : base("The model did not produce valid Resonator notation after one repair attempt.\n" + string.Join("\n", errors)) { Errors = errors; }
    }

    /// <summary>Reusable from desktop, Unity, or HTTP; does not enter the speech/TTS queue.</summary>
    public sealed class MusicCompositionService
    {
        private readonly ILocalChatModelClient _model;
        private readonly InstructionLibrary _library;
        public MusicCompositionService(ILocalChatModelClient model, InstructionLibrary library)
        { _model = model; _library = library; }

        public async Task<MusicCompositionResult> ComposeAsync(MusicCompositionRequest request, CancellationToken token)
        {
            ValidateRequest(request);
            var instructions = _library.LoadMusicCompositionInstructions();
            var messages = new List<ChatMessage> {
                new ChatMessage("system", instructions.SystemPrompt + "\n" + instructions.References
                    + "\nEXECUTABLE OUTPUT CONTRACT (overrides optional reference syntax):\n" + instructions.ExecutableProfile + "\nTIMING CONTRACT: | separators are optional visual markers. Ignore any earlier requirement to emit or fill explicit | delimited bars. Derive the timeline from event durations. Bars is a target length, not an exact output constraint. Prefer complete musical phrases. A shorter or longer valid melody is acceptable; playback preserves every note and fills an incomplete final measure with rests. Events may cross implied bar boundaries; / and // add no time."),
                new ChatMessage("user", JsonSerializer.Serialize(request) + "\n" + BuildDurationInstruction(request))
            };
            IReadOnlyList<string> errors = Array.Empty<string>();
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                string notation = (await _model.CompleteTextStreamingAsync(messages, 8192,
                    "MusicComposition" + (attempt == 1 ? "" : ".Repair"), null, token).ConfigureAwait(false)).Trim();
                token.ThrowIfCancellationRequested();
                errors = ResonatorNotationValidator.Validate(notation, request);
                if (errors.Count == 0) {
                    notation = ResonatorNotationValidator.CompleteFinalMeasure(notation, request, out int actualBars);
                    return new MusicCompositionResult {
                        Notation = notation, ActualBars = actualBars, Attempts = attempt, InstructionVersion = instructions.Version
                    };
                }
                if (attempt == 1) {
                    messages.Add(new ChatMessage("assistant", notation.Length <= 65536 ? notation : notation.Substring(0, 65536)));
                    messages.Add(new ChatMessage("user", "Replace the entire score, preserving the requested musical journey. Fix ALL errors below together. Use the requested tempo and meter. Preserve the generated melody and its length; do not trim or rewrite it merely to match the requested bar count. Bar separators | are optional and do not affect timing. Return only the complete notation:\n" + string.Join("\n", errors)));
                }
            }
            throw new MusicGenerationException(errors);
        }

        public static string BuildDurationInstruction(MusicCompositionRequest request)
        {
            string[] meter = request.Meter.Split('/');
            decimal beats = request.Bars * decimal.Parse(meter[0], System.Globalization.CultureInfo.InvariantCulture) * 4 / decimal.Parse(meter[1], System.Globalization.CultureInfo.InvariantCulture);
            return "Aim for about " + beats.ToString(System.Globalization.CultureInfo.InvariantCulture) + " quarter-note beats (" + request.Bars + " bars of " + request.Meter + "). This is a length target, not a required note count or exact duration. Return the complete musical statement even if shorter or longer. The app will keep every note and fill an incomplete final measure with rests.";
        }

        public static void ValidateRequest(MusicCompositionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 12000)
                throw new ArgumentException("Description must contain 1–12000 characters.");
            if ((request.ExistingNotation?.Length ?? 0) > 65536 || (request.OriginalBrief?.Length ?? 0) > 12000) throw new ArgumentException("Existing song context is too large.");
            if (request.Bars < 1 || request.Bars > 64) throw new ArgumentException("Bars must be 1–64.");
            if (request.Tempo < 30 || request.Tempo > 240) throw new ArgumentException("Tempo must be 30–240.");
            if (!ResonatorNotationValidator.IsMeter(request.Meter)) throw new ArgumentException("Meter numerator must be 1–12 and denominator 2, 4, 8 or 16.");
        }
    }
}
