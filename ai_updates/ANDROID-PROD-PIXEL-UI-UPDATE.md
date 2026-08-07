# Android production pixel UI update

This update adds a production-facing Android companion UI while preserving the existing developer/debug harness.

## User-facing changes

- Android now opens in **Prod Mode** by default.
- Prod Mode uses a dark pixel-fantasy layout inspired by the Six Stars mockup:
  - cat avatar header
  - tappable Star title panel
  - large full-body Star stage
  - big **Talk** action panel
  - response/status panels
  - Interstar status and Last Emotional Vectors display
- The selected Star can be changed by tapping the Star title or the **Stars** button.
- The Star picker is a six-card popup with portraits for:
  - Elusiaa
  - Kaelia
  - Maera
  - Khavia
  - Auerlis
  - Nyxari
- The Star picker includes a **Dev Mode** button and a Dev Mode checkbox toggle.
- Dev Mode now includes a **Switch to Prod Mode** button at the top.
- Prod Mode uses `android-phone-prod` as its session id; Dev Mode uses `android-phone-dev`.

## Generated Android assets

Generated PNG assets were added under:

`android/six-stars-mobile/app/src/main/res/drawable-nodpi/`

Assets include:

- production dark sci-fi pixel background
- cat avatar crop from the UI reference
- talk button/sword icon
- heart pixel icon
- title/card/panel frames
- full-body Star assets
- six portrait assets for the Star picker
- exported PNG idle frames from the Elusiaa and Maera GIFs

## Animation behavior

The Android production stage uses a custom nearest-neighbor `PixelSpriteView`.

- It draws all Star sprites with `Paint.setFilterBitmap(false)` for crisp pixel scaling.
- Elusiaa and Maera use exported multi-frame idle PNG sequences.
- Kaelia, Khavia, Auerlis, and Nyxari currently use static full-body PNGs as their idle frame until dedicated GIF/frame sequences are added.

## Notes

This update keeps the existing bridge, audio, recording, photo, interstar-status, and emotion-vector code paths intact. The prod UI reuses the same request methods, so API behavior should match Dev Mode except for the prod session id.
