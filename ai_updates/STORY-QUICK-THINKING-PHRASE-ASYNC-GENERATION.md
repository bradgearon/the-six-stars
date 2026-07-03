# Story Quick Thinking Phrase + Async Generation

Updated story mode so the player hears a short Star-specific line immediately instead of waiting silently for the long story LLM pass.

## Behavior

- When the request is detected as `RequestType.Story`, the runtime now returns a fast manual response from `StarProfile.StoryThinkingPhrases`.
- The full story LLM request starts in the background immediately after the thinking phrase response is created.
- When the full story response is generated, the runtime raises `StoryGenerationCompleted`.
- The desktop host waits for the short thinking phrase to finish, then begins speaking the generated story.
- Existing local story autoplay and TTS prefetch still handle `storyMarkdown` chunks after the generated story starts.

## Data

Added `storyThinkingPhrases` to each Star JSON file with five in-character strings per Star. This keeps the fast acknowledgement deterministic and avoids spending LLM time before the user hears the Star.

## Notes

- The quick thinking phrase does not run the background memory review.
- The full generated story still queues the background memory review and releases it after generated story voice playback completes.
- Cancelling runtime operations cancels the pending generated story and any active story narration.

## Voice loop behavior

After the quick thinking phrase is spoken, the desktop voice loop does not immediately restart listening while the generated story is still pending. This avoids capturing random player speech or work noise before the story begins.
