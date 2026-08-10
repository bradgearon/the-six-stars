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

Supported state names are open-ended. The current app requests:

- `idle`
- `talking`

Future emotional state names can be wired by calling `setProdAnimationState("happy")`, `setProdAnimationState("hurt")`, etc. If a state GIF does not exist, the app falls back to `idle.gif`, then to PNG frame resources.

Keep every GIF state for a given Star on the same source canvas. Production idle/talking assets use a 128×128 canvas. A mismatched canvas can change the integer pixel scale when Android swaps animation states, making the character visibly grow or shrink even when the artwork itself has matching proportions.

## Optional one-shot shirt-off reaction

Place an optional non-looping GIF at:

```text
star_animations/<star-id>/shirt-off.gif
```

`shirt_off.gif` is also accepted for compatibility, but `shirt-off.gif` is the preferred name.

When the background emotion review reports `requestToShowBoobs=true`, Android waits until the current Star voice queue is finished, plays this GIF once, holds its last frame for two seconds, and returns to `idle.gif`. If the asset is absent, the app stays on the normal animation.
