# Resonator Note-String Language

A compact text notation for writing, generating, editing, streaming, and transforming musical ideas in **Resonator**.

The language is designed to be fast to type, easy for a model to generate, easy for a deterministic parser to read, and expressive enough for melody, bass, rhythm, chords, motifs, microtonality, phrasing, articulation, and AHD payoff structures.

## 1. Notes

Write note names directly:

```text
A B C D E F G
```

Sharps and flats:

```text
C# F# Bb Eb
```

Octave numbers are optional:

```text
C# E F# B
C#4 E4 F#4 B4
```

Without octave numbers, Resonator may choose a sensible register. With octave numbers, pitches are exact.

## 2. Default duration

A note is a quarter note by default:

```text
A
```

## 3. Eighth notes

Add a comma:

```text
A,
```

## 4. Sixteenth notes

Add a period:

```text
A.
```

## 5. Longer notes

Optional shorthand:

```text
A:    half note
A::   whole note
```

The hold syntax below can also extend notes.

## 6. Holds / ties

Use `-` to continue the preceding note without a new attack.

```text
A -
A, -,
A. -.
```

The hold unit follows the same duration rules as notes and rests.

## 7. Rests

Use `_` for a rest.

```text
_     quarter rest
_,    eighth rest
_.    sixteenth rest
```

## 8. Chords

Put simultaneous notes inside brackets:

```text
[A C E]
[C#3 E3 G#3]
```

Duration modifiers apply to the whole chord:

```text
[A C E],
[A C E]:
[A C E] -
```

Optional named-chord shorthand may also be supported:

```text
[Am]
[C#m11]
[D/F#]
[Gmaj7]
```

The engine expands named chords into deterministic voicings.

## 9. Bars

Use `|` for bar lines:

```text
| A C E D | C B A _ |
```

## 10. Phrase and section boundaries

Use `/` for a phrase boundary:

```text
A C E D / C B A
```

Use `//` for a larger section boundary:

```text
A C E D / C B A // C# E F# G#
```

Phrase boundaries are meaningful to Resonator because interval emotion can depend on whether a note begins, continues, or ends a phrase.

## 11. Exact attack-time offset

Use `@` to move the note attack without moving the rhythmic grid.

```text
A@+1/8q
A@-1/16q
```

Percentage form:

```text
A@+12.5%
A@-25%
```

The percentage is relative to one quarter-note beat unless another unit is specified.

Exact MIDI tick offsets are also allowed:

```text
A@+60t
A@-12t
```

At 480 PPQ:

```text
quarter   = 480 ticks
eighth    = 240 ticks
sixteenth = 120 ticks
1/8 q     = 60 ticks
```

Example:

```text
A B@+60t C D
```

B is late, but C remains on its original grid point.

## 12. Sounding duration / gate length

Use `:` followed by a percentage to change how long the event actually sounds without changing the rhythmic grid.

```text
A:75%
A,:60%
A.:50%
```

This is useful for legato, staccato, phrasing, groove, and humanization.

## 13. Combined timing and duration

Timing offset and gate length can be combined:

```text
A,@+1/8q:85%
```

Meaning:

- nominal eighth note
- starts one-eighth of a quarter late
- sounds for 85% of its nominal duration

Example:

```text
| A C,@-1/16q:90% E D,@+1/8q:70% |
```

## 14. Timeline movement

An onset offset does not move later events. Use an actual rest to insert time into the timeline.

```text
A _, B
```

Optional explicit inserted time:

```text
_@1/8q
```

## 15. Accents and articulation

Suggested symbols:

```text
A>   accent
A^   strong accent / marcato
A!   staccato
A?   tenuto / emotional emphasis
A~   legato into next event
```

Chords may use the same modifiers:

```text
[A C E]>
```

## 16. Slurs / gesture groups

Use parentheses around a connected gesture:

```text
(A B C D)
```

This can mean a slur, legato phrase, emotional gesture, or one contour unit.

## 17. Motifs

Use braces for reusable motif groups:

```text
{A E B D A}
```

Optional named motif:

```text
{hero=A E B D A}
```

Replay it later:

```text
{hero}
```

Possible transformations:

```text
{hero:x2}
{hero:up8}
{hero:down8}
```

Exact transform names can be defined by Resonator.

## 18. Repetition

Repeat a group:

```text
x2{A C E D}
x4{A, E,}
```

## 19. Tuplets

Suggested syntax:

```text
3{A, B, C,}
```

Other tuplets:

```text
5{A. B. C. D. E.}
```

## 20. Dynamics

Optional dynamic marks:

```text
pp p mp mf f ff
```

Example:

```text
p A C E / mf D F A / ff [A C E]
```

## 21. Crescendo / diminuendo

Suggested region syntax:

```text
< A B C D >
```

= crescendo.

```text
> D C B A <
```

= diminuendo.

## 22. Velocity

Optional exact MIDI velocity:

```text
A:v96
A:v120
[A C E]:v110
```

## 23. Microtonal pitch

Cents can be added directly to a note:

```text
C+30c
C+50c
C+70c
C#-30c
```

Useful for yearning, leaning, expressive intonation, and movement between tonal worlds.

## 24. Pitch bends

Suggested gradual bend syntax:

```text
C~+70c
```

= begin at C and bend upward 70 cents during the note.

```text
C~C#
```

= continuously bend from C to C#.

```text
C#~C
```

= continuously bend downward from C# to C.

## 25. Key and tonal home

Starting key:

```text
key=Am
key=C#m
```

AHD can distinguish the ordinary home from an alternate emotional center:

```text
home=Am
ahd=C#
```

or:

```text
worldA=Am
worldB=C#
```

## 26. Tempo

```text
tempo=120
```

## 27. Meter

```text
4/4
3/4
6/8
7/8
```

Combined header:

```text
tempo=120 4/4 key=Am
```

## 28. Withheld events

Because Resonator supports emotional payoff, `?` may represent an intentionally missing expected event.

```text
A C E ?
```

This can represent a missing note, rhythmic attack, motif ending, validator, or harmonic resolution.

## 29. Validator / payoff event

A clear keyword form is recommended:

```text
payoff(A#)
payoff(D#)
payoff(C#)
```

This marks the event as a delayed validator or emotional payoff point.

Modifiers still work around the event:

```text
payoff(D#)>
```

## 30. AHD worlds

Optional explicit world markers:

```text
world=Am
A C E D / C B A

world=C#
C# E F# G# / E D# C#
```

These markers help Resonator reason about foreign notes, tonal memory, validators, reciprocal dissonance, and return behavior.

## 31. Payoff definitions

A payoff can be described explicitly:

```text
payoff {
  target=bar8
  validator=M6
  placement=strong
  motif=complete
  rhythm=restore_missing_offbeat
  bass=root_return
  strength=high
}
```

Possible validator types:

```text
m2 M2 m3 M3 P4 tritone P5 m6 M6 m7 M7 octave
```

Placement:

```text
placement=strong
placement=weak
placement=anticipated
placement=barline
```

Charge / delay length:

```text
charge=1beat
charge=1bar
charge=4bars
charge=section
```

## 32. Bassline

Bass uses the same notation as melody:

```text
bass:
| A2 E2 A2 E2 |
| F#2 C#3 F#2 C#3 |
```

Resonator can analyze root movement, fourth/fifth motion, chromatic motion, delayed root arrivals, bass payoff, and rhythmic expectation.

## 33. Rhythm-only patterns

Use `x` as an attack token when pitch does not matter:

```text
rhythm:
| x _ x, x, _ x |
```

Accents:

```text
| x> _ x, x^, _ x |
```

Withheld attack:

```text
| x x, ? x |
```

Later payoff:

```text
| x x, payoff(x) x |
```

## 34. Strong and weak beats

Strong/weak beat meaning should normally be inferred automatically from meter, bar position, subdivision, accent, and phrase position.

In 4/4, for example:

```text
beat 1 = strongest
beat 3 = secondary strong
beats 2 and 4 = weaker
offbeats = weak / anticipatory
```

No special strong/weak syntax is required unless Resonator later needs explicit overrides.

## 35. Humanization and swing

Optional macros:

```text
humanize.time=±12t
humanize.velocity=±5
swing=58%
```

These compile down to exact onset offsets and velocities.

## 36. Full example

```text
tempo=120 4/4 key=Am
home=Am
ahd=C#

p
| A C E, D, C B A | /
| C# E F# G# E D# C# ? | /

mf
| C# E F# G# E D# C# payoff(A#)> | //

ff
| [A2 E3 A3] A4 C5 E5 |
| [F#2 C#3 F#3] A4 F#5 payoff(D#) |
```

Possible interpretation:

1. A-minor identity is established.
2. C# alternate world appears.
3. Its validator is withheld.
4. The foreign phrase becomes familiar.
5. A# finally validates and reinterprets the preceding material.
6. Dynamics and harmony enlarge the payoff.
7. A later validator can create a second emotional color.

## 37. Precision example

```text
tempo=96 4/4 key=Am

| A4:95% C5,@-1/16q:85% E5 D5,@+1/8q:70% |
| C#5~+30c E5 F#5 G#5 |
| C5~+70c payoff(C#5)> - |
```

This can encode exact register, timing offsets, gate lengths, microtonal yearning, pitch bends, holds, and payoff events.

## 38. Recommended internal event model

Each parsed event can compile into fields such as:

```text
Pitch
NominalDuration
OnsetOffset
GateDuration
Velocity
Articulation
PhraseId
MotifId
Bar
Beat
TonalWorld
PitchBend
PayoffRole
```

This is enough for Resonator to produce precise MIDI while keeping the text notation compact.

# Core quick reference

```text
A            quarter note
A,           eighth note
A.           sixteenth note
A:           half note
A::          whole note

-            hold/tie
_            quarter rest
_,           eighth rest
_.           sixteenth rest

[A C E]      chord
|            bar line
/            phrase boundary
//           section boundary

A4           exact octave/register
A>           accent
A^           strong accent
A!           staccato
A?           tenuto/emphasis
A~           legato

A@+1/8q      late onset
A@-1/16q     early onset
A@+60t       exact tick offset
A:75%        gate-length percentage

C+30c        +30 cents
C+50c        +50 cents
C+70c        +70 cents
C~C#         bend from C to C#

{...}        motif
x2{...}      repeat
3{...}       tuplet

?            intentionally withheld event
payoff(A#)   validator/payoff event

key=Am       key
home=Am      primary tonal home
ahd=C#       alternate AHD center
tempo=120    tempo
4/4          meter

swing=58%
humanize.time=±12t
humanize.velocity=±5
```

# Design principle

Resonator notation should stay **musically readable first**.

Traditional notation concepts handle pitch, duration, meter, rests, simultaneity, phrasing, articulation, dynamics, and timing.

AHD extensions handle alternate tonal worlds, withheld expectations, validators, payoff timing, motif memory, emotional reinterpretation, microtonal yearning, rhythmic payoff, and bass payoff.

The goal is not to replace standard sheet music. The goal is to provide a compact language that can translate instructions such as:

> Make this more tender here, charge the tension for four bars, then make the final payoff feel huge and hopeful.

into precise, editable, streamable musical events.
