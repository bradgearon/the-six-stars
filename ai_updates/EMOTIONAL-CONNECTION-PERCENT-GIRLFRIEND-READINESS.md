# Emotional Connection Percent + Girlfriend Readiness Gate

This update adds a second-stage relationship meter for girlfriend readiness. Heart, Trust, and Romance still move in small deterministic deltas, but EmotionalConnectionPercent is now stored separately in the root state file and changes only through the deterministic emotion pipeline.

Rules implemented:

- None of the Stars are attracted by default.
- Non-allies cannot build the deeper connection meter. Elusiaa remains the only default ally.
- Connection rises only when the fast emotional vector shows high personality-matched intimate positives such as safety, trust, tenderness, affection, vulnerability, attraction, hope, or star-specific matching emotions.
- Negative vector hits such as Disrespected, Pressured, CreepedOut, Hurt, or Unsafe reduce connection.
- Low-effort / at-rest turns slowly decay connection while it is at or below 50%. Above 50%, the connection does not decay at rest unless the player harms the bond.
- Background review may suggest tiny connection changes through suggestedStateDeltas.emotionalConnectionPercent, but deterministic logic clamps and verifies support from the vectors/combo history.
- Girlfriend readiness requires: IsAlly=true, EmotionalConnectionPercent >= 85, Heart >= 20, Trust >= 15, and Romance >= 12.
- The prompt now tells the Star the current connection percent and readiness state, but she should not speak the number unless the player asks about mechanics.
- Clear history resets this state because it resets the StarState object.
