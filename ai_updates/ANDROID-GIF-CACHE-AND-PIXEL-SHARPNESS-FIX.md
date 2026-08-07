# Android GIF cache and pixel sharpness fix

The production Star sprite renderer now keeps decoded GIF drawables in memory for the lifetime of the view and preloads the selected Star's `idle.gif` and `talking.gif` on a background decoder thread. The audio-start transition therefore reuses an already decoded drawable instead of reopening the talking asset when speech begins.

The visible softness was primarily a rendering-filter mismatch rather than an audio-sync problem. PNG fallback frames were already drawn with bitmap filtering disabled, while `AnimatedImageDrawable` used bilinear filtering when Android scaled the GIF into the shared 70% sprite viewport. The GIF path now explicitly disables bitmap filtering as well, so both static and animated pixel art use nearest-neighbor-style scaling.

Missing optional GIF assets are remembered to avoid repeatedly reopening nonexistent files. The existing non-deprecated `ImageDecoder` / `AnimatedImageDrawable` path remains in use on API 28 and newer; API 26-27 continue to show the first GIF frame without using the deprecated `Movie` API.
