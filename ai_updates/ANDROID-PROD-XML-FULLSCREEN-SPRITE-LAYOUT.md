# Android Prod XML Fullscreen Sprite Layout

This update cleans up the Android production companion screen and makes the Star stage easier to tweak from XML.

## What changed

- `activity_prod_companion.xml` now declares the Star animation view directly:
  - `com.wds.sixstarsmobile.PixelStarAnimationView`
  - id: `@+id/prod_sprite_view`
- `MainActivity` no longer creates the production sprite view in code and inserts it into the stage manually.
- `PixelStarAnimationView` is now a standalone Android View class instead of a private inner class.
- The renderer now loads Star GIFs from `assets/star_animations/<star>/<state>.gif` and samples them into cropped animation frames.
- Transparent or near-black padding is trimmed before drawing, so full-body Star sprites fill the stage much better.
- Static PNG fallback frames are cropped the same way, so Khavia / Auerlis / Kaelia / Nyxari no longer stay tiny inside a large black source canvas.
- Prod layout spacing was tightened:
  - smaller header/avatar/title block
  - slightly smaller Talk/Text controls
  - slightly smaller conversation history panel
  - more screen space goes to the Star stage
- The root layout now uses `fitsSystemWindows=true` to reduce status-bar overlap.
- The conversation history window scrolls back to the top after refresh so newest-first messages are visible cleanly.

## Animation asset convention

Drop future emotion/talking GIFs here:

```text
android/six-stars-mobile/app/src/main/assets/star_animations/elusiaa/idle.gif
android/six-stars-mobile/app/src/main/assets/star_animations/elusiaa/talking.gif
android/six-stars-mobile/app/src/main/assets/star_animations/elusiaa/happy.gif
android/six-stars-mobile/app/src/main/assets/star_animations/maera/idle.gif
android/six-stars-mobile/app/src/main/assets/star_animations/maera/talking.gif
android/six-stars-mobile/app/src/main/assets/star_animations/khavia/idle.gif
```

The runtime tries the requested state first, then falls back to `idle.gif`, then falls back to `res/drawable-nodpi/star_<star>_<state>_<index>.png` frames.

## Files touched

- `android/six-stars-mobile/app/src/main/res/layout/activity_prod_companion.xml`
- `android/six-stars-mobile/app/src/main/java/com/wds/sixstarsmobile/MainActivity.java`
- `android/six-stars-mobile/app/src/main/java/com/wds/sixstarsmobile/PixelStarAnimationView.java`
