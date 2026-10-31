namespace Resonator.Core;

public sealed class ResonatorProfile
{
    public int Ppq { get; set; } = 480;
    public int DefaultTempo { get; set; } = 120;
    public int DefaultChannel { get; set; } = 0;
    public int DefaultVelocity { get; set; } = 96;
    public int DefaultOctave { get; set; } = 4;
    public int MidiNoteForC0 { get; set; } = 12;
    public int PitchBendRangeSemitones { get; set; } = 2;
    public ArticulationDefaults ArticulationDefaults { get; set; } = new();
    public Dictionary<string, ArticulationDefinition> Articulations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PercussionDefinition Percussion { get; set; } = new();
}

public sealed class ArticulationDefaults
{
    public int LeadTicks { get; set; } = 30;
    public int DurationTicks { get; set; } = 30;
    public int Velocity { get; set; } = 100;
}

public sealed class ArticulationDefinition
{
    public string? KeySwitch { get; set; }
    public int? KeySwitchMidiNote { get; set; }
    public int? Channel { get; set; }
    public int? Velocity { get; set; }
    public int? LeadTicks { get; set; }
    public int? DurationTicks { get; set; }
    public List<ControlChangeDefinition> ControlChanges { get; set; } = [];
}

public sealed class ControlChangeDefinition
{
    public int Controller { get; set; }
    public int Value { get; set; }
    public int? Channel { get; set; }
}

public sealed class PercussionDefinition
{
    public int Channel { get; set; } = 9;
    public int DefaultDurationTicks { get; set; } = 60;

    // Maps Resonator input note names to whatever MIDI notes the user's drum
    // plugin expects. Keys may be pitch classes ("C", "C#", "Db") or
    // exact tokens with octaves ("C1"). Exact mappings win over pitch-class
    // mappings.
    public Dictionary<string, int> NoteMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Optional human-readable labels for UI/documentation only.
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ResonatorComposition
{
    public int Ppq { get; init; } = 480;
    public int Tempo { get; set; } = 120;
    public int Numerator { get; set; } = 4;
    public int Denominator { get; set; } = 4;
    public long LengthTicks { get; set; }
    public List<ResonatorEvent> Events { get; } = [];
}

public abstract record ResonatorEvent(long Tick);

public sealed record NoteEvent(
    long Tick,
    long DurationTicks,
    int MidiNote,
    int Velocity,
    int Channel,
    double Cents = 0,
    double? BendTargetCents = null,
    bool IsPercussion = false) : ResonatorEvent(Tick);

public sealed record ControlChangeEvent(long Tick, int Channel, int Controller, int Value) : ResonatorEvent(Tick);

public sealed record ParseResult(ResonatorComposition Composition, IReadOnlyList<string> Warnings);
