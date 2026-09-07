# Star Animation Assets

Drop production Star GIFs here using this folder convention:

```text
star_animations/<star-id-lowercase>/<state>.gif
```

Examples:

```text
star_animations/elusiaa/idle.gif
star_animations/elusiaa/talking.gif
star_animations/elusiaa/happy.gif
star_animations/maera/idle.gif
star_animations/maera/talking.gif
star_animations/nyxari/idle.gif
```

Supported state names are open-ended. The current app requests the normal `idle` / `talking` states and can play these fast-speech one-shot emotion states:

- `happy`
- `confused`
- `sad`
- `angry`
- `love`

Place them beside the existing Star GIFs, for example `star_animations/kaelia/happy.gif`. A completed fast-speech emotion control sends only the normalized state name over the bridge WebSocket; Android plays that GIF once and then returns to the current idle/talking-lips state. Missing emotion GIFs are skipped safely.

Keep every GIF state for a given Star on the same source canvas. Production idle/talking assets use a 128×128 canvas. A mismatched canvas can change the integer pixel scale when Android swaps animation states, making the character visibly grow or shrink even when the artwork itself has matching proportions.

## Optional one-shot shirt-off reaction

Place an optional non-looping GIF at:

```text
star_animations/<star-id>/shirt-off.gif
```

`shirt_off.gif` is also accepted for compatibility, but `shirt-off.gif` is the preferred name.

When the background emotion review reports `requestToShowBoobs=true`, Android waits until the current Star voice queue is finished, plays this GIF once, holds its last frame for two seconds, and returns to `idle.gif`. If the asset is absent, the app stays on the normal animation.

## Frame-anchored talking lips (Build 15P)

The Android production renderer can now keep the Star's full body on `idle.gif` while speech is active and draw a small cropped talking-mouth GIF over the current idle frame. This avoids the whole character shifting/bouncing when a separate full-body talking animation has slightly different artwork.

Configuration lives in:

```text
star_animations/lip-positions.json
```

Each Star currently has one configured base animation, `idle`, with one entry for every existing idle GIF frame. Coordinates are source-canvas pixels measured from the **top-left of the 128x128 idle canvas**. Fill in `x` and `y` with the top-left position where the cropped lips belong on that frame:

```json
{
  "frame": 0,
  "x": 61,
  "y": 47,
  "durationMs": 100
}
```

`x: null` / `y: null` means that frame has not been positioned yet. While you are entering coordinates, the renderer holds the most recent configured lip anchor for unfinished frames, so setting only one frame is enough for a first visual test.

For each Star, add the cropped mouth animation here:

```text
star_animations/<star-id>/lips-talking.gif
```

For example:

```text
star_animations/elusiaa/lips-talking.gif
star_animations/kaelia/lips-talking.gif
```

`lips-talking.gif` should be cropped tightly to the lip artwork, with transparency around the lip pixels if needed. Do **not** make it a 128x128 whole-character canvas: the renderer scales the cropped GIF by the same pixel scale as the Star and places its top-left corner at the configured `x`,`y` anchor.

`durationMs` already mirrors the current frame timing in each `idle.gif`; normally only `x` and `y` need editing. If an idle GIF is re-exported with a different frame count or timing, update the matching entries too.

At runtime, lip compositing is enabled only when both conditions are true:

1. at least one non-null idle lip position exists for that Star, and
2. that Star has `lips-talking.gif`.

Until then, Android keeps the existing legacy `talking.gif` behavior, so this can be filled in Star-by-Star without breaking the current app. Speech activity markers still decide when the mouth is talking versus idle; they now turn the lip overlay on/off without restarting the base idle animation.
