# Resonator AI notation — Six Stars executable profile

Return only a complete notation string. Whitespace separates tokens; explicit-note chords in square brackets sound simultaneously. Use one sequential stream.

Header: `tempo=N N/D key=KEY`. Use the requested tempo and meter. KEY is A–G, optional #/b, optional m for minor; do not append major/minor words. Notes are A–G, optional #/b, mandatory octave 0–7. Prefer melody octaves 3–4.

Notes are a quarter-note by default. Durations in quarter-note beats: bare event=1; comma=0.5; period=0.25. Suffixes replace the default duration. `_` is a rest; `-` extends the preceding note/chord without retriggering and cannot follow a rest. Both accept duration suffixes. A chord's duration counts once, not once per pitch.

`|` separates bars; put one before the first bar and after the last. No empty bars or doubled separators at line wraps. Exactly the requested number of bars. Each bar must sum to N*4/D quarter-note beats: 4/4 requires 4, 6/8 requires 3. Count durations, not notes. Three quarter notes total 3; three quarters plus a half total 5; two quarters plus a whole total 6. Silently sum and correct every bar before output. No pickups or meter changes.

Standalone `/` and `//` mark phrases/sections. They consume no time and do not reset the bar count. Dynamics `pp p mp mf f ff` consume no time.

Note/chord modifiers in order: duration; optional attack offset; optional gate; optional velocity; optional one emphasis suffix. Offset: `@` then a mandatory + or - and a fraction ending q, decimal percentage ending %, or integer ticks ending t. Fraction denominator must be positive. No negative first onset. Gate: `:N%`, 1–200. Velocity: `:vN`, integer 1–127 (not standalone vN). Emphasis: `>` accent, `^` strong accent, `!` staccato, `?` tenuto, `~` legato. Offsets and gates do not change nominal bar duration or shift later grid events. Rests/holds accept only duration suffixes.

Emit only the syntax above. Never emit numeric duration suffixes such as `:1.5` or request fields such as `UseAhd=true`. No named chords, optional octaves, extra header directives, track labels, art= switches, drum mode, pitch bends, microtones, braces, motifs/repetition macros, tuplets, crescendo brackets, standalone ?, payoff metadata, rhythm x, swing or humanization directives. Express AHD, motifs and emotional structure through explicit notes, harmony, dynamics and timing.
