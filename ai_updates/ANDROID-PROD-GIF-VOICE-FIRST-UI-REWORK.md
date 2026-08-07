# Android Prod GIF + Voice-First UI Rework

## Summary

The Android production companion screen has been reworked into a cleaner one-screen production UI:

- Prod mode now uses an XML layout: `app/src/main/res/layout/activity_prod_companion.xml`.
- The production view shows only:
  - Last Cat avatar + tappable Star name panel
  - Full-body Star animation stage
  - relationship line
  - primary voice-first Talk control
  - secondary pencil/text composer
  - small rolling conversation history window
- Debug/status/interstar/emotion-vector panels remain available in Dev Mode and still update internally, but they are not shown on the Prod screen.

## Animation Assets

The app now supports real GIF animation assets from:

```text
app/src/main/assets/star_animations/<star-id-lowercase>/<state>.gif
```

Included now:

```text
app/src/main/assets/star_animations/elusiaa/idle.gif
app/src/main/assets/star_animations/maera/idle.gif
```

These are copied from the supplied Elusiaa and Maera idle GIFs.

## Future animation states

Add future files using this convention:

```text
star_animations/elusiaa/talking.gif
star_animations/elusiaa/happy.gif
star_animations/elusiaa/hurt.gif
star_animations/elusiaa/flattered.gif
star_animations/maera/talking.gif
star_animations/maera/angry.gif
star_animations/nyxari/idle.gif
star_animations/nyxari/talking.gif
```

The runtime tries the requested state first, then falls back to `idle.gif`, then falls back to drawable frame resources named like:

```text
star_elusiaa_idle_0.png
star_elusiaa_idle_1.png
star_elusiaa_talking_0.png
star_elusiaa_talking_1.png
```

This makes it easy to add either GIFs or PNG frame sequences later.

## Voice-first Talk flow

Prod Mode now treats **Talk** as the primary voice action:

1. Tap **Talk** to start recording.
2. The same control changes to **Send Voice**.
3. A **Cancel** button appears in the same voice area.
4. Tap **Send Voice** to stop and send the recording.
5. Tap **Cancel** to discard the recording.

The text composer is secondary:

1. Tap the pencil/Text control.
2. The text box opens below the voice control.
3. Type and tap **Send Text**.

## Star selector

The Star name/title text panel is now fully clickable, not only the avatar/image. The picker keeps the six portrait cards and includes the Dev Mode button.

## Production layering

The production UI is now separated into:

- XML layout: `activity_prod_companion.xml`
- PNG interface assets: `res/drawable-nodpi/`
- XML shape styles: `res/drawable/prod_*.xml`
- Star animation assets: `assets/star_animations/`
- Runtime wiring: `MainActivity.buildProdUi()` and `PixelStarAnimationView`

Dev Mode remains programmatic and unchanged for diagnostics.
