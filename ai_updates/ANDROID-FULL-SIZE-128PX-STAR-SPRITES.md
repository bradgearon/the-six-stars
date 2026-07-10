# Android full-size 128 px Star sprites

The production companion now uses the full character stage instead of the previous 70% sprite viewport. The XML padding around `PixelStarAnimationView` was removed, allowing a 128x128 GIF canvas to occupy the largest centered square that fits inside the framed stage.

Pixel-art assets are scaled by the largest whole-number multiplier that fits whenever the source is smaller than the stage. Since all idle and talking GIFs use the same 128x128 canvas, this makes every Star substantially larger while preserving exact pixel blocks, stable positioning, and identical sizing across animation states. Fractional fit scaling remains only as a fallback when a source image is larger than the available stage.
