# Scriban Prompt Templates + Background Vectors Update

This update moves the fast Star prompt construction into editable Scriban-style `.sbn` template files under `src/Wds.SixStars.Api/Content/PromptTemplates`.

## Changed

- Added `PromptTemplateRenderer` and template files:
  - `speech_system.sbn`
  - `speech_user.sbn`
  - `review_system.sbn`
  - `review_user.sbn`
- Preserved the recent conversation history section in the foreground prompt so each Star can continue the last few player/Star turns naturally.
- Foreground speech now requests only what the player needs to hear quickly:
  - normal chat: `starId`, `spokenText`
  - story: `starId`, `spokenText`, `storyTitle`, `storyId`, `storyMarkdown`, `storySummary`, `storyIsFinished`, `storyPartIndex`
- Moved `emotionalEffectVector` and `negativeEmotionVector` to the background memory/emotion review pass.
- Background review now computes and returns compact vectors along with durable quote/memory/relationship updates.
- The foreground prompt still includes Star identity, relationship state, remembered markers, exact recent turns, story history, screen summary, and task-specific rules.

## Why

The foreground LLM call should behave like the Star speaking aloud. Durable analysis, emotional deltas, quote extraction, spam detection, and memory writes belong in the background pass after TTS begins.

## Template strategy

The templates use Scriban-style `{{ Key }}` placeholders and are copied to the build output. The C# prompt builder prepares compact sections and renders them through the template renderer. This keeps the current prompt behavior but makes the structure easier to scale, split by activity, and later replace with full Scriban if desired.
