# Top-IU Spark Listen Selection

The short-form shelf performance path now separates deterministic shelf quality from the Star's own taste.

1. Only untold `spark` artifacts are eligible. Constellation Stories are not consumed by this path.
2. The deterministic writing ranker orders the entire eligible Spark pool by saved open-ended IU review values.
3. The highest 100 Sparks (or all of them when fewer than 100 remain) become the performance candidate window.
4. The full Spark text, Overall IU, deterministic public IU value, and complete 27-value vector are shown to the viewpoint Star.
5. The Star chooses exactly five favorites when five or more candidates are available. The prompt explicitly tells her not to mechanically choose the first five.
6. Only those selected Sparks are marked told.
7. If the Star-selection call fails or violates the ID/spoken-part contract, the request fails visibly and no Spark is marked told. There is no hidden top-five or recency fallback.

The Android production Listen control is now two-stage. Tapping Listen opens a compact submenu:

- **Thought** keeps the existing prepared/at-rest Listen behavior.
- **Spark** runs the high-IU Spark performance path above.
- **Story** displays a decorative constellation marker but intentionally does not consume a Constellation Story yet. It is the UI slot for the future long-form shelf reader.

The Constellation icon cycles through a small set of decorative star patterns each time the submenu opens.
