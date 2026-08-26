# Standalone Android UI layout and assets

## Declarative UI contract

The standalone app's visible Android views are declared in XML resources under `app/src/main/res/layout`.

The current screen is:

- `app/src/main/res/layout/activity_main.xml`

This is intentional. Keep visible controls in XML rather than constructing `Button`, `TextView`, `ImageView`, `LinearLayout`, or other UI widgets in Java. Java should bind behavior to view IDs and update runtime data only. This keeps the screen editable in Android Studio's **Design / Split** Layout Editor.

The six Star-selector buttons are also declared directly in `activity_main.xml`; they must not be recreated dynamically in `MainActivity`.

If additional screens are added later (settings, import/export, memory inspection, privacy/analytics controls), create one or more XML files in `res/layout` and keep the same rule.

## Launcher icon

The exact 64x64 PNG supplied for the app icon is preserved at:

- `app/src/main/res/drawable-nodpi/app_icon_source.png`

Generated nearest-neighbor launcher PNGs are present at:

- `app/src/main/res/mipmap-mdpi/ic_launcher.png`
- `app/src/main/res/mipmap-hdpi/ic_launcher.png`
- `app/src/main/res/mipmap-xhdpi/ic_launcher.png`
- `app/src/main/res/mipmap-xxhdpi/ic_launcher.png`
- `app/src/main/res/mipmap-xxxhdpi/ic_launcher.png`

Android 8+ also receives an adaptive launcher-icon declaration in:

- `app/src/main/res/mipmap-anydpi-v26/ic_launcher.xml`

The adaptive foreground references the preserved source PNG, so the source artwork remains easy to replace later.

## Other packaged PNG assets

The standalone APK also includes:

- `drawable-nodpi/portrait_elusiaa.png`
- `drawable-nodpi/portrait_kaelia.png`
- `drawable-nodpi/portrait_maera.png`
- `drawable-nodpi/portrait_khavia.png`
- `drawable-nodpi/portrait_auerlis.png`
- `drawable-nodpi/portrait_nyxari.png`
- `drawable-nodpi/ui_cat_avatar.png`
- `drawable-nodpi/ui_prod_background.png`

These files are Android resources and are packaged into the APK by Gradle.
