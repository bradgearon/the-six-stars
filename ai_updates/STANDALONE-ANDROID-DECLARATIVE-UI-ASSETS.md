# Standalone Android declarative UI + asset preservation

The standalone Android relationship-builder must keep its visible views in Android XML layout resources so its screens remain directly editable in Android Studio's Layout Editor.

- Do not construct visible UI widget trees programmatically when an XML layout can represent them.
- Java/Kotlin code may bind listeners, populate dynamic text/state, switch portrait resources, and control runtime behavior.
- New settings/import/export/privacy screens should be added as XML layouts rather than imperative view construction.

The user-supplied launcher icon must remain present as an original PNG source resource at `android/six-stars-standalone/app/src/main/res/drawable-nodpi/app_icon_source.png` in addition to generated density-specific launcher assets. Star portraits and other packaged PNGs must not be omitted when producing standalone Android source ZIPs.
