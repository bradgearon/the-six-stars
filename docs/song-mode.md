# Desktop song mode

The desktop **Ask for Music** button opens a separate song workspace. Type a musical
brief or a desired feeling, choose bars/tempo/meter and AHD, then submit. No speech
input wiring or separate HTTP host is added in this patch.

Examples:

- “Say hello, welcome. Become tender, gather heroic confidence, then open outward
  with an ascending major sixth. Bring the opening motif back at the end.”
- “I'm feeling down and want something reassuring. Start quietly, acknowledge that
  weight, gradually find courage, and end warmly rather than with a huge fanfare.”
- Revision: “Keep the opening and its rhythm, but make the middle more tender and
  hold the sixth at the climax longer.”

The numeric controls define the exact score length, tempo and meter. Change them
alongside the brief when extending the song or changing tempo.

## Working song

A validated first response saves the whole notation plus the original brief,
latest request, tempo, meter, bar count and AHD choice. Each subsequent request
includes the current notation and original brief, asks for a revision, and replaces
the song only after successful notation validation. Restarting/reopening the app
restores the song. Generation failures leave the previous saved score intact.

Storage: `%LOCALAPPDATA%\SixStars\Music\current-song.json`.

**Clear Song Work** cancels generation/rendering, stops MIDI, clears saved song
work and the preview, and resets the workspace. An operation generation counter
and cancellation checks discard late results. **Stop / Cancel** retains song work.
Closing the workspace stops playback and requests but keeps the saved song.

## Instructions

New dedicated instruction type: `MusicComposition`.

`Content/Instructions/Music/music-composition.json` is editable and contains the
composition instructions and executable output profile. The three supplied Markdown
guides are included verbatim beside it and loaded in full for each request. The
regular Star dialogue prompts and TTS pipeline are not involved.

The template uses structural interval destinations, direction, beat placement,
duration, chord context and register; AHD supplies resolution priming, preserved
identity during divergence, local resolution, remembered home and motivic return.
These are artistic tendencies, not a promise that every listener feels the same.

The local model uses the desktop's local endpoint/model configuration. There is
one repair attempt with deterministic notation errors. Output is a complete note
string, not explanatory prose. Debug label: `MusicComposition` / `.Repair`.

The `resonator-core-v1` profile supports explicit octave notes, explicit
chords, rests, holds, quarter/eighth/sixteenth/half/whole durations, bars, phrase
markers, dynamics, onset offsets, gates, velocities and articulation. It checks bar
lengths and header settings. Proposed constructs in the reference (named chords,
motif macros, tuplets, bends, multi-track labels and payoff metadata) are expanded
into supported musical events where possible instead of assuming parser support.
This explicit-event subset has been checked with the supplied Resonator v0.1
parser and MIDI API. Its actual LANGUAGE.md is also supplied to the composer. AHD is expressed in
the notes, motifs and timing, so it does not depend on special AHD parser keywords.

## MIDI connection and preview

Run `start-resonator.cmd` at the solution root to launch the included API at
`http://127.0.0.1:5000`. Merge `music-appsettings.fragment.json` into desktop
`appsettings.json` if needed; the same working defaults are also in the config class.
Rendering is enabled by default. Turn Enabled off to compose without the API.

- `Enabled`: true to render and preview after composition.
- `EndpointUrl`: defaults to `http://127.0.0.1:5000/midi`.
- `BodyMode`: `Text` for raw `text/plain`, `JsonString` for a JSON string value, or
  `JsonProperty` for a JSON object containing one configured notation field.
- `NotationProperty`: `notation` for the supplied API; BodyMode defaults to `JsonProperty`.
- `TimeoutSeconds`: 1–600 seconds.

The adapter expects binary Standard MIDI bytes, checks the MThd signature, limits
responses to 16 MiB, and reports HTTP/format errors. JSON URLs/base64 envelopes and
other response contracts would require an adapter change. Parser errors returned
by the API are displayed in the song window.

When enabled, a successful composition automatically renders and plays. **Render
MIDI** retries rendering the retained notation without asking the model again.
**Play MIDI** replays the current preview. If rendering fails, the notation remains
saved. Reopening the workspace restores notation; render again for a fresh preview.

Preview uses Windows' built-in MCI MIDI sequencer and default MIDI mapper, not the
speech audio queue. A normal MIDI synth gives a basic General MIDI preview; piano
is the usual default when the file does not select another program. Program changes
in the returned file are respected. The available output device determines sounds;
a missing sequencer/synth yields a visible error without losing song work. No DAW,
SoundFont or extra MIDI playback NuGet package is required by this code.

Microsoft references: [Playing a MIDI File](https://learn.microsoft.com/en-us/windows/win32/multimedia/playing-a-midi-file)
and [The MIDI Mapper](https://learn.microsoft.com/en-us/windows/win32/multimedia/the-midi-mapper).

## Install and verify

Extract the patch at the solution root and rebuild `Wds.SixStars.Ui`. Merge the
small settings fragment instead of replacing your existing appsettings file.
The included `tools/Resonator` folder is the supplied API/Core/CLI source, with two
integration fixes: numeric slashes in meters/onset fractions no longer split into
phrase markers, and relative API profile paths resolve against the executable folder.
The supplied VSL profile remains unchanged (C0 = MIDI 24; C3 = middle C). No Android
changes are needed. The service requires .NET 8; the desktop continues to use .NET 9.

Validation completed: full API and Windows-targeted WPF desktop builds; automated
checks for full reference loading, revision context, valid/invalid notation,
bar lengths, timing modifiers, real parser interval/register checks, real HTTP
notation-to-MIDI conversion, repair, cancellation including late model output,
atomic song replacement, restore/clear, all three request body modes, disabled
rendering, and rejection of non-MIDI responses. Existing unrelated compiler warnings
remain. Windows audible playback and actual local-model musical quality could not be
tested on this Linux workspace. The API was built and run, and the real MIDI
response was verified.

Run checks from the solution root:

```sh
dotnet run --project tests/Wds.SixStars.Music.Checks -- src/Wds.SixStars.Api/Content tools/Resonator/resonator.config.json
# With start-resonator.cmd running, append http://127.0.0.1:5000/midi for the live HTTP check.
```

For future voice input, transcribe the request into `Description` and use the same
composition flow with `ExistingNotation` and `OriginalBrief`. The current desktop
microphone/conversation routes remain unchanged.
