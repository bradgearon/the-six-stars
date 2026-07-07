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
