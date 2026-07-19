# Foreground emotion-vector rollback

The experimental change that moved `emotionalEffectVector` and `negativeEmotionVector` into the fast spoken-response pass has been rolled back.

Foreground speech now uses the established response behavior again. The background review remains responsible for interpreting emotional effects and applying durable memory, relationship, and state updates.

The unrelated Android UI cleanup remains: breakup is available from the selected Star popup rather than as a standalone button on the main companion screen.
