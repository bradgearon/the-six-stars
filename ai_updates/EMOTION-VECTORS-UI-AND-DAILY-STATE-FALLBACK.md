# Emotion Vectors UI + Daily State Fallback Update

This patch adds a dedicated "Last Emotional Vectors" readout to both the desktop WPF harness and the Android bridge app, and it reduces the chance that a missing desktop daily-state JSON file blocks normal requests.

## Desktop UI changes

- Added a new **Last Emotional Vectors** panel beneath the Star spoken output.
- The panel shows the most recent positive and negative emotion vectors in a readable form, for example:
  - `Good: +Comforted 72 @ 0.91 | +Mercy 68 @ 0.88`
  - `Bad: -Pressured 25 @ 0.60`
- The raw vector strings are also shown so the deterministic layer can be debugged directly.
- The main status area was enlarged and made scrollable so background memory updates, top affected emotions, and daily-state notices no longer get visually cut off.
- Long daily-state refresh summaries are no longer appended into the one-line `Status`; they appear in the dedicated daily-state line instead.

## Android UI changes

- Added a **Last emotional vectors** readout near the top of the mobile app, directly below the selected Star state summary.
- Mobile responses, bridge events, selected-star updates, photo responses, story events, and background emotion updates now carry:
  - `emotionalEffectVector`
  - `negativeEmotionVector`
  - `lastEmotionVectorsText`
- Android can display either the preformatted vector text from desktop or parse the raw vector strings locally if needed.

## Daily state robustness

- The daily-state file store now tolerates missing, empty, disappearing, or unreadable `current.meta.json` files.
- If `current.xml` exists but the metadata JSON is missing or broken, the store now creates a safe fallback record from `current.xml` instead of throwing.
- The normal Star request prompt injection now tries fallback daily-state sessions if the current session has no daily XML yet:
  - `desktop-local-dev`
  - `android-phone-dev`
  - `local-dev`
- This means the desktop can reuse the Android daily state when the Android session has already generated one but the desktop session has not.
- The desktop UI state refresh also checks those fallback sessions before showing `Today: no daily Star state yet.`
- The JSON-backed Star state store now also tolerates a state JSON file that disappears mid-read or becomes unreadable, falling back to a clean in-memory save instead of crashing the request.

## Notes

The deterministic relationship scoring still owns Heart / Trust / Romance movement. This update only exposes the latest emotion-vector evidence and makes the daily-state loading path more forgiving.
