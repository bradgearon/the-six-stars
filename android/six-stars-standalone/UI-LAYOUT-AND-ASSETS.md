# Standalone Android UI layout and assets

## Production UI parity + declarative UI contract

The standalone app's main screen is deliberately the same production XML used by the existing thin-client Android app:

- thin client: `android/six-stars-mobile/app/src/main/res/layout/activity_prod_companion.xml`
- standalone: `android/six-stars-standalone/app/src/main/res/layout/activity_main.xml`

The files are expected to remain identical except for the package-qualified `PixelStarAnimationView` class name. Do not create a second standalone visual language unless that is an intentional product change.

Visible Android views remain declared in XML resources so the app can be edited in Android Studio's **Design / Split** Layout Editor. Java should bind behavior to view IDs and update runtime data only; it should not construct the visible view tree dynamically.

Current declarative layouts:

- `activity_main.xml` — exact production companion shell.
- `dialog_star_selection.xml` — six-Star selection popup.
- `dialog_companion_console.xml` — Last Cat/debug popup with a dedicated scrollable runtime log.

Future settings, import/export, privacy/analytics and memory-inspection screens should follow the same rule.

## Production UI assets

The standalone project now carries the same production presentation assets required by `activity_main.xml`, including:

- Star idle sprites (`star_*_idle_0.png`)
- Star animation GIF assets under `app/src/main/assets/star_animations/`
- `ui_avatar_frame.png`
- `ui_cancel_pixel.png`
- `ui_cat_avatar.png`
- `ui_character_frame.png`
- `ui_heart_pixel.png`
- `ui_mic_pixel.png`
- `ui_pencil_pixel.png`
- `ui_photo_pixel.png`
- `ui_prod_background.png`
- `ui_selection_card.png`
- `ui_send_pixel.png`
- `ui_talk_button_frame.png`
- `ui_talk_sword.png`
- `ui_title_frame.png`
- matching production XML shape/background resources.

## Launcher icon

The launcher icon is owned by the Android Studio Image Asset-generated resources in this project. The latest user-provided source intentionally uses these generated names and the manifest points at them directly:

- `app/src/main/app_icon_source-playstore.png` — Play Store/source artwork retained by the project.
- `app/src/main/res/mipmap-*/app_icon_source.webp` — generated launcher icons.
- `app/src/main/res/mipmap-*/app_icon_source_foreground.webp` — generated adaptive foregrounds.
- `app/src/main/res/mipmap-*/app_icon_source_round.webp` — generated round variants.
- `app/src/main/res/mipmap-anydpi-v26/app_icon_source.xml`
- `app/src/main/res/mipmap-anydpi-v26/app_icon_source_round.xml`
- `app/src/main/res/drawable/app_icon_source_background.xml`

`AndroidManifest.xml` uses `@mipmap/app_icon_source` and `@mipmap/app_icon_source_round`. Do not replace these with the older `ic_launcher` resources when merging UI/runtime work.

The production companion UI assets are independent of the launcher icon and can be synchronized from the thin-client app without touching these generated launcher resources.
