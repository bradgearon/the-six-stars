# Android Prod Cat Menu + Image Picker Update

## Summary
Updated the Android production companion screen to keep the main view clean while moving diagnostics/settings into the Last Cat avatar menu.

## Prod UI changes
- Tapping the Last Cat face/avatar now opens the production companion menu.
- The Star selector no longer contains a Dev Mode toggle; it is only for choosing Stars.
- The production screen remains focused on:
  - selected Star title
  - Star idle/talking animation stage
  - relationship line
  - primary Talk button
  - photo/image button
  - secondary text composer
  - small rolling conversation history
- The Star title text and subtitle remain tappable and open the Star selector.

## Companion menu
The cat-face menu now contains the production settings and debug/status information that should not clutter the main prod screen:
- Desktop bridge URL
- Play Star voice on this phone
- Romance Mode
- Save Settings
- Test Bridge
- Test Audio
- Dev Mode
- current relationship line
- connection/audio status
- daily Star state
- interstar state
- last emotional vectors
- top affected emotions
- last Star response
- debug text
- voice journal

## Image sending
- Added a small image/photo button next to the primary Talk button.
- It opens the standard Android chooser so the user can select a gallery image or use the camera when camera permission is available.
- Gallery/content images are read from their content URI and sent to the desktop bridge as a Photo request.
- Camera preview images are still supported as JPEG fallback when the camera app returns a thumbnail bitmap.

## Assets
Added:
- `android/six-stars-mobile/app/src/main/res/drawable-nodpi/ui_photo_pixel.png`

## Notes
- Prod Mode still uses XML layout in `activity_prod_companion.xml`.
- Dev Mode still keeps a Switch to Prod Mode button so testers are not trapped after entering Dev Mode.
