# Resonator MIDI Generator (.NET 8)

Resonator converts the compact Resonator note-string language into a Standard MIDI File.

This repository contains:

- `Resonator.Core` — parser + MIDI writer
- `Resonator.Cli` — command-line converter
- `Resonator.Api` — ASP.NET Core Minimal API
- `resonator.config.json` — user instrument profile for articulations and percussion
- `LANGUAGE.md` — notation supported by this first implementation

There are **no runtime NuGet dependencies** in Core, CLI, or API. MIDI is written directly.

## Quick start

Requires .NET 8 SDK.

```bash
dotnet build Resonator.sln
```

### CLI

```bash
dotnet run --project Resonator.Cli -- \
  --notation "tempo=120 4/4 | A C E D | C B A _ |" \
  --output example.mid
```

From a file:

```bash
dotnet run --project Resonator.Cli -- \
  --input examples/melody.res \
  --output melody.mid
```

Pipe text:

```bash
echo "tempo=120 4/4 mode=drums | C E, E, D |" \
  | dotnet run --project Resonator.Cli -- -o drums.mid
```

Use another profile:

```bash
dotnet run --project Resonator.Cli -- \
  --profile my-studio.json \
  --notation "art=legato | A B C# D |" \
  --output phrase.mid
```

## API

Run:

```bash
dotnet run --project Resonator.Api
```

JSON endpoint:

```bash
curl -X POST http://localhost:5000/midi \
  -H "Content-Type: application/json" \
  -d '{"notation":"tempo=120 4/4 | A C E D |","fileName":"phrase.mid"}' \
  --output phrase.mid
```

Raw-text endpoint:

```bash
curl -X POST http://localhost:5000/midi/text \
  -H "Content-Type: text/plain" \
  --data 'tempo=120 4/4 | A C E D |' \
  --output phrase.mid
```

Parser inspection endpoint:

```bash
curl -X POST http://localhost:5000/parse \
  -H "Content-Type: application/json" \
  -d '{"notation":"tempo=120 4/4 | A C E D |"}'
```

## Instrument profile

`resonator.config.json` controls user-specific MIDI behavior.

### Octave naming

DAWs disagree about octave labels. The sample profile is configured for the user's VSL convention:

```json
"midiNoteForC0": 24,
"defaultOctave": 3
```

That makes `C0` MIDI 24 and `A3` MIDI 69.

If your system uses scientific MIDI naming (`C4 = 60`), use:

```json
"midiNoteForC0": 12,
"defaultOctave": 4
```

### Articulations

Articulation changes do not consume rhythmic time:

```text
art=legato A B C# D
art=pizzicato A, C#, E,
```

The profile maps articulation names to keyswitch notes and/or CC messages.

Example:

```json
"articulations": {
  "legato": {
    "keySwitchMidiNote": 26,
    "channel": 0
  }
}
```

The supplied profile includes the VSL mappings discussed for Resonator:

- Short = 24
- Long = 25
- Legato = 26
- Legato Agile = 27
- Portamento = 28
- Dynamics = 29
- Tremolo = 30
- Trills = 31
- Pizzicato = 32
- Harmonics = 33
- Ponticello = 34
- Rest = 35

### Percussion

Percussion uses the **same note syntax as melody**. Switch the parser into drum mode:

```text
mode=drums
| C E, E, D |
```

The profile maps those input note names to whatever MIDI notes the user's plugin expects:

```json
"percussion": {
  "channel": 9,
  "defaultDurationTicks": 60,
  "noteMap": {
    "C": 36,
    "D": 38,
    "E": 42,
    "A#": 49
  },
  "labels": {
    "C": "Kick",
    "D": "Snare",
    "E": "Closed Hat",
    "A#": "Crash"
  }
}
```

So a plugin that expects a kick on MIDI 36 can use `C` as the Resonator input note. Another user's plugin can map `C` somewhere else without changing the musical chunk. Exact-octave keys such as `C1` are also allowed and take precedence over pitch-class mappings.

Simultaneous drum hits use the normal chord syntax:

```text
[C A#]
```

Switch back at any time:

```text
mode=melody
| A3 C4 E4 D4 |
mode=drums
| C E, E, D |
```

This makes percussion chunks compatible with the same rhythm, offset, gate, velocity, accent, repeat, and payoff syntax as pitched notes.

## Microtonality

Static cents offsets are supported:

```text
C+30c
C+50c
C+70c
C#-30c
```

Pitch bends are supported:

```text
C~C#
C#~C
```

Resonator uses spare MIDI channels for independently bent notes and sends pitch-bend-range RPN setup automatically. The range is configured with:

```json
"pitchBendRangeSemitones": 2
```

## Exact timing

The rhythmic grid and actual attack/gate are separate.

```text
A,@+1/8q:85%
```

means:

- nominal eighth note
- start 1/8 of a quarter late
- sound for 85% of the nominal eighth duration

Supported onset offsets:

```text
A@+1/8q
A@-1/16q
A@+12.5%
A@+60t
```

An onset offset does **not** push later grid events.

## Why the config is separate

Resonator notation should describe **music**, not one user's plugins.

The same phrase:

```text
art=legato | A C# E F# |
```

can trigger completely different keyswitches on VSL, Kontakt, SWAM, a hardware synth, or another user's template simply by changing the profile.

Likewise, `mode=drums | C D |` can target General MIDI, Superior Drummer, orchestral percussion, or a custom sampler by changing only the profile note map.

## Current scope

This first version intentionally focuses on the path needed now:

**Resonator string -> parsed timed events -> `.mid` bytes**

The parser already supports the important building blocks for melody, harmony, bass, percussion, articulation switching, groove offsets, and microtonal AHD experiments.

Higher-level AHD generation — story-to-emotion planning, interval selection, motif memory, validator/payoff planning, rhythm payoff, bass payoff, transformations, and streaming model patches — should sit above `Resonator.Core` and emit this language.

## Note on validation

The project was created in an environment that did not have the .NET SDK installed, so I could not execute `dotnet build` here. The source is structured for .NET 8 and contains no external runtime package dependency; run `dotnet build Resonator.sln` locally as the first verification step.
