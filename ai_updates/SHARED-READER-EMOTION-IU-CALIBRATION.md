# Shared reader-emotion IU calibration

The first ReaderEmotionIuV1 rollout accidentally let each actor calibrate future reviews from that actor's own persisted shelf. That created a feedback loop: an actor receiving high early values persisted more work, then those high-valued works became that actor's future calibration references; actors receiving low early values had no equivalent anchors. IU stopped behaving like one shared currency.

This change makes review calibration actor-neutral:

- Spark and Constellation review continue using the same compact 31-position semicolon-delimited reader-emotion IU vector.
- The reviewer no longer receives a character/personality card and is explicitly forbidden to change magnitude based on author identity.
- Reader-emotion magnitude references come only from the shared curated calibration pack, never from the current actor's shelf.
- The shared system prompt supplies one fixed IU ladder from absent through landmark-level effects.
- The Spark calibration pack contains low, medium, strong, and excellent contrast anchors in authored file order.
- If four or more of the five Spark candidates receive an identical complete vector, the runtime requests one calibration re-review instead of silently accepting a likely template collapse.
- Existing actor-specific persistence gates and `all` rule shapes are preserved. They were not the root cause.
- The global Overall excellence backstop is evaluated independently of those gates for both Spark and Constellation output.
- Desktop writing output exposes every Spark's reader-emotion summary and the batch's five Overall IU values so future calibration drift is visible.

Existing saved ReaderEmotionIuV1 values remain historical data, but they are no longer fed back into the reviewer as calibration.
