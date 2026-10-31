# Resonator Language — v0.1 Parser Surface

This document describes the syntax implemented by the first MIDI generator. 

## Notes

Notes are a quarter note long by default.

```text
A B C# Eb
A3 C#4 E4
```

Octaves are optional. `defaultOctave` and `midiNoteForC0` come from the profile.

## Durations

Used to shorten notes.

```text
A       quarter
A,      eighth
A.      sixteenth
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