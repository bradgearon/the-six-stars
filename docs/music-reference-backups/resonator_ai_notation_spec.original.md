# Resonator Note-String Language — AI Generation Spec

## Purpose

Generate compact Resonator notation that can be parsed deterministically into MIDI.

Follow the syntax exactly. Do not add explanatory text inside the notation output unless explicitly requested.

---

## Header

Optional global declarations:

- `tempo=N` — tempo in BPM.
- `N/D` — meter.
- `key=KEY` — musical key.
- `home=KEY` — primary tonal home.
- `ahd=KEY` — alternate AHD tonal center.
- `mode=melody` — interpret note tokens as pitched notes.
- `mode=drums` — interpret note tokens through the configured percussion note map.
- `art=NAME` — switch to a configured articulation.

---

## Notes

Valid pitch names:

- `A B C D E F G`
- Optional accidental: `#` or `b`.
- Optional octave number.

If octave is omitted, Resonator selects or preserves an appropriate register according to context/configuration.

---

## Default Duration

A note without a duration symbol is a quarter note.

Duration suffixes:

- `,` — eighth note.
- `.` — sixteenth note.
- `:` — half note.
- `::` — whole note.

The same duration suffixes apply to rests and chords.

---

## Holds / Ties

- `-` — continue the preceding sounding event without retriggering it.
- `-,` — eighth-note hold.
- `-.` — sixteenth-note hold.
- `-:` — half-note hold.
- `-::` — whole-note hold.

A hold extends the preceding note or chord.

---

## Rests

- `_` — quarter rest.
- `_,` — eighth rest.
- `_.` — sixteenth rest.
- `_:` — half rest.
- `_::` — whole rest.

---

## Chords

Use brackets for simultaneous pitches:

`[NOTE NOTE NOTE]`

Duration, timing, gate, velocity, articulation, and accent modifiers may be applied to the entire chord.

Named chord symbols may be supported if enabled by the parser/configuration.

---

## Bars and Structure

- `|` — bar boundary.
- `/` — phrase boundary.
- `//` — section boundary.

Phrase and section markers do not consume time.

---

## Attack Timing Offset

Use `@` with `+` or `-` to move the attack relative to its nominal grid position without moving later events.

Supported units:

- `%` — percentage of one quarter-note beat.
- `q` — fraction or multiple of a quarter note.
- `t` — MIDI ticks.

Timing offsets modify onset only.

---

## Gate / Sounding Duration

Use `:N%` after an event to set actual sounding duration as a percentage of its nominal duration.

Gate adjustment does not change the rhythmic position of later events.

---

## Velocity

Use:

`vN`

where `N` is MIDI velocity `1–127`.

---

## Articulation / Emphasis

Event suffixes:

- `>` — accent.
- `^` — strong accent / marcato.
- `!` — staccato.
- `?` — tenuto / emotional emphasis.
- `~` — legato connection to the next event.

Configured articulation changes use:

`art=NAME`

Configured articulations may emit keyswitch notes, controllers, program changes, or other MIDI events according to the user's profile.

---

## Microtonal Pitch

Use cents after a note:

- `+Nc` — raise pitch by N cents.
- `-Nc` — lower pitch by N cents.

Microtonal pitch must be emitted using pitch bend or equivalent MIDI behavior supported by the engine.

---

## Continuous Pitch Bend

Use:

`START~END`

to bend continuously from one pitch to another during the event.

A cents-based bend target may also be used:

`NOTE~+Nc`
`NOTE~-Nc`

---

## Motifs

Use braces for a grouped motif:

`{ ... }`

A motif may optionally be assigned a name:

`{NAME= ... }`

A previously named motif may be referenced by name if motif references are enabled.

---

## Repetition

Use:

`xN{ ... }`

to repeat the enclosed group N times.

---

## Tuplets

Use:

`N{ ... }`

to distribute N enclosed events evenly across the group's nominal rhythmic span.

---

## Dynamics

Supported dynamic state tokens:

- `pp`
- `p`
- `mp`
- `mf`
- `f`
- `ff`

Dynamics remain active until changed.

---

## Crescendo / Diminuendo

- `< ... >` — crescendo across the enclosed region.
- `> ... <` — diminuendo across the enclosed region.

---

## Percussion

Percussion uses the same note syntax as melody.

When `mode=drums` is active, written note names are translated through the user's configured percussion mapping.

No special drum-token syntax is used.

Chords may be used to trigger simultaneous percussion hits.

---

## Articulation Configuration

Articulation names are not hard-coded into the language.

The user's configuration maps articulation names to MIDI behavior, including:

- keyswitch notes
- controller values
- MIDI channels
- program changes
- optional timing offsets

The generated notation should only reference configured articulation names.

---

## Percussion Configuration

Percussion note meanings are not hard-coded.

The user's configuration maps written pitch tokens to the destination MIDI notes expected by the user's drum instrument or plugin.

---

## AHD Tonal Worlds

Use:

- `home=KEY` — primary tonal world.
- `ahd=KEY` — alternate tonal world.

These declarations provide compositional context. They do not themselves consume time or emit MIDI notes.

---

## Withheld Event

Use:

`?`

as a structural placeholder for an intentionally withheld expected event.

A withheld event consumes no sound unless the engine or generation instruction explicitly assigns it rhythmic duration.

---

## Payoff / Validator Metadata

When explicit payoff annotation is required, use:

`payoff(NOTE)`

The payoff marker identifies an event as a validator or expectation-resolution event.

It does not alter the pitch or duration unless other modifiers are present.

---

## Rhythm-Only Generation

If a pitchless rhythmic representation is required, use:

`x`

for an attack.

Duration, rest, timing, velocity, accent, and payoff modifiers may be applied to rhythm attacks.

---

## Swing and Humanization

Optional global declarations:

- `swing=N%`
- `humanize.time=±Nt`
- `humanize.velocity=±N`

Humanization is applied after deterministic parsing.

---

## Event Order

For a note, chord, rest, hold, or rhythm event, interpret modifiers in this conceptual order:

1. Base event.
2. Nominal duration.
3. Pitch/register or percussion mapping.
4. Microtonal pitch/bend.
5. Attack timing offset.
6. Gate duration.
7. Velocity/dynamics.
8. Articulation/emphasis.
9. Structural metadata such as phrase, motif, or payoff role.

---

## Parsing Rules

- Whitespace separates tokens.
- Brackets define simultaneous notes.
- Braces define grouped material.
- Bar, phrase, and section markers do not consume time.
- Timing offsets do not shift later grid events.
- Rests consume timeline duration.
- Holds consume timeline duration without retriggering the preceding sounding event.
- Chord notes begin simultaneously unless individual-note offsets are explicitly supported.
- In drum mode, pitch spelling is treated as a percussion-map key.
- Unknown articulation names or percussion mappings must produce a validation error.
- Invalid or ambiguous syntax must produce a validation error rather than being guessed.

---

## AI Output Rule

When asked to generate Resonator notation:

- Output only valid Resonator syntax unless explanation is explicitly requested.
- Preserve the requested bar count, meter, tempo, tonal home, AHD center, emotional structure, and instrumentation constraints.
- Use phrase and section boundaries where musically meaningful.
- Use explicit octave numbers only when register matters.
- Use timing offsets only when intentional rhythmic displacement is requested or musically necessary.
- Use microtonal notation only when requested or required by the intended effect.
- Use configured articulation names only.
- In percussion mode, use only note tokens valid in the user's percussion mapping.
