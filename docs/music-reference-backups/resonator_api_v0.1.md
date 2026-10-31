# Resonator Language — v0.1 Parser Surface

This document describes the syntax implemented by the first MIDI generator.

## Notes

```text
A B C# Eb
A3 C#4 E4
```

Octaves are optional. `defaultOctave` and `midiNoteForC0` come from the profile.

## Durations

```text
A       quarter
A,      eighth
A.      sixteenth
A:      half
A::     whole
```

## Rests

```text
_       quarter rest
_,      eighth rest
_.      sixteenth rest
```

## Holds

A hold extends the previous note/chord without retriggering it.

```text
A -
A, -,
A. -.
```

## Bars, phrases, sections

These are structural markers and do not themselves consume time.

```text
| A C E D | / C B A _ // C# E F# G# |
```

- `|` bar marker
- `/` phrase marker
- `//` section marker

## Chords

Exact notes:

```text
[A C E]
[C#3 E3 G#3 B3]
```

Common chord symbols are also expanded:

```text
[Am]
[C#m11]
[Gmaj7]
[D/F#]
```

Supported qualities in the first version:

```text
major (default)
m
7
maj7
m7
dim
aug
sus2
sus4
add9
m11
```

## Tempo and meter

```text
tempo=120
120
4/4
6/8
```

The first writer emits one initial MIDI tempo and time signature.

## Dynamics

```text
pp p mp mf f ff
```

These set the default velocity for following notes.

## Exact velocity

```text
A:v96
[A C E]:v110
```

## Articulation marks

```text
A>    accent
A^    strong accent
A!    staccato
A?    tenuto/emphasis
A~    legato overlap
```

## Configured articulation switching

```text
art=short
art=long
art=legato
art=pizzicato
```

Names and keyswitches/CCs are defined in `resonator.config.json`.

Articulation commands do not advance musical time.

## Gate / sounding length

```text
A:75%
A,:60%
```

The rhythmic grid remains unchanged; only the actual note length changes.

## Onset offsets

```text
A@+1/8q
A@-1/16q
A@+12.5%
A@+60t
```

Offsets move the attack while leaving subsequent grid events in place.

Combined:

```text
A,@+1/8q:85%
```

## Microtonal cents

```text
C+30c
C+50c
C+70c
C#-30c
```

## Continuous pitch bends

```text
C~C#
C#~C
```

The writer emits a stepped MIDI pitch-bend curve over the duration of the note.

## Percussion

Percussion uses ordinary note names. Enter percussion mode with:

```text
mode=drums
```

Then the same notation works for drum hits:

```text
| C E, E, D |
```

`resonator.config.json` maps input notes to the MIDI notes expected by the user's drum plugin. In the supplied example profile:

```text
C  = kick
D  = snare
E  = closed hat
F# = open hat
A# = crash
B  = ride
```

The mapping is fully user configurable. Chords mean simultaneous hits:

```text
[C A#]
```

Switch back with:

```text
mode=melody
```

All normal duration, offset, velocity, accent, gate, and repeat syntax works in percussion mode.

## Repetition

```text
x2{A C E D}
mode=drums x4{E, E,}
```

Simple nested-free repeat bodies are expanded before parsing.

## Payoff marker

The parser accepts:

```text
payoff(A#)
```

or a trailing marker:

```text
A#@
```

In v0.1 the marker is primarily forward-compatible metadata; `payoff(...)` is also given a strong accent so the MIDI demonstrates the event clearly.

## AHD metadata accepted

These are accepted without generating MIDI events yet:

```text
key=Am
home=Am
ahd=C#
worldA=Am
worldB=C#
<Am>
<C#>
```

This leaves the syntax ready for the later AHD analysis/generation layer.

## Profile-level MIDI precision

Typical profile settings:

```json
{
  "ppq": 480,
  "defaultTempo": 120,
  "defaultChannel": 0,
  "defaultVelocity": 96,
  "defaultOctave": 3,
  "midiNoteForC0": 24,
  "pitchBendRangeSemitones": 2
}
```

## Example melody

```text
tempo=96 4/4 key=Am home=Am ahd=C#
| C#3 E3 F#3 B3 |
| A3 C4 E4 D4 |
| C4+30c C4+50c C4+70c payoff(C#4) |
```

## Example percussion chunk

```text
tempo=120 4/4
mode=drums
| [C A#] E, E, D |
| C E, F#, D> |
```

## Intentionally deferred higher-level syntax

The original Resonator design also includes concepts such as named motifs, motif transformation, explicit withheld-event objects, tuplets, crescendo regions, swing/humanization expansion, and structured payoff declarations.

Those belong naturally in the next layer. The first parser accepts some metadata forms for forward compatibility, but this version focuses on reliable MIDI generation from the core notation needed for melody, bass, chords, percussion, timing, articulations, and bends.
