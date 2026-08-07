# Daily State Flexible JSON + Desktop Status Area Fix

## Why this update exists

The desktop harness could show a daily-state refresh failure like:

`The JSON value could not be converted to System.String. Path: $.currentDesires`

The local model sometimes returns an array or object for a field that the schema asked to be a string, especially `currentDesires`. The mobile session may already have a usable daily XML while the desktop session is trying to generate its own, so the UI could look like it had a daily state but still report a desktop refresh failure.

## Changes

- Added `FlexibleStringJsonConverter` for model JSON parsing.
  - Strings still deserialize normally.
  - Arrays are flattened into `; ` separated text.
  - Objects prefer fields like `text`, `summary`, `value`, `description`, `name`, `desire`, `feeling`, or `intent`; otherwise they are preserved as compact JSON text.
  - Numbers and booleans become compact string text.
- Registered the converter in `StructuredStarAgent` before daily state response parsing.
- Strengthened the daily-state prompt with an explicit JSON shape rule:
  - summary/desire/posture fields must be strings.
  - only list fields should be arrays.
- Enlarged the desktop runtime status/daily-state area from 120px to 180px.
- Added a visible `Runtime Status / Daily State` label and more spacing inside the scrollable status area.
- When a foreground spoken response has no emotion vectors yet but a background analysis is pending, the desktop Last Emotional Vectors panel now says it is waiting for the deterministic background review instead of resetting to `none yet`.

## Expected result

Daily state refreshes should no longer fail just because the LLM returns `currentDesires` as a list. The desktop UI has more room for daily-state notices, top-emotion summaries, and status messages. The emotion-vector panel should be clearer during the gap between the spoken response and the later deterministic background emotion update.
