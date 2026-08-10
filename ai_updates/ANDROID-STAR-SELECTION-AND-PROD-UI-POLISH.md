# Android Star Selection and Production UI Polish

- Removed the accidental per-Star `Post 5 to X` buttons from the Star selection popup and restored the popup to Star switching only.
- Updated Star selection cards to use the first idle frame (`idle_0`) as the preview image, with a larger framed crop that fills the card better and shows the Star more clearly.
- Wrapped the Star selection popup in a `ScrollView` so the grid remains usable on smaller screens.
- Simplified redundant instructional copy such as `tap to switch` and the production conversation placeholder text.
- Tightened text styling consistency across programmatic production UI controls by standardizing on monospace text without extra font padding.
- Refined the production header alignment so the Star name and subtitle sit more cleanly and centrally in the title panel.
- Replaced the prepared-thought yellow text indicator with a visual highlight on the Talk button so the UI signals that the Star wants to speak without adding extra text clutter.
