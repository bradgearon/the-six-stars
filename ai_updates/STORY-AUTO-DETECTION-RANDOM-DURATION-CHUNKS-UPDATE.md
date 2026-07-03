# Story Auto-Detection + Random Duration + Short Speech Chunks Update

This update makes story narration work from normal typed or voice requests without requiring the dedicated **Tell Me A Story** button. The button still works, but ordinary player speech such as "Elusiaa, tell me a story" can now be promoted into Story mode automatically.

## What changed

### 1. Pre-flight story intent check

A new quick intent classifier runs before the main Star response generation when the incoming request is still normal `Chat` mode.

Files added/updated:

- `src/Wds.SixStars.Api/Engine/IStarRequestIntentClassifier.cs`
- `src/Wds.SixStars.Api/Models/StarRequestIntentResult.cs`
- `src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`
- `src/Wds.SixStars.Api/Ai/StarPromptBuilder.cs`
- `src/Wds.SixStars.Api/Ai/StarResponseJsonSchema.cs`
- `src/Wds.SixStars.Api/Engine/SixStarsRuntime.cs`

The classifier returns compact JSON with:

- `isStoryRequest`
- `confidence`
- `storySeed`
- `reason`

If it decides the player is asking the Star to tell, narrate, create, or continue a story, the runtime promotes the request to `RequestType.Story` before the main LLM call.

A heuristic fallback is included for cases where the classifier call fails, so obvious phrases like "tell me a scary story" or "continue the story" can still switch into Story mode.

## 2. Random story length target

New stories now get a randomized target length from **2 to 45 minutes**.

Files updated:

- `src/Wds.SixStars.Api/Models/StoryOptions.cs`
- `src/Wds.SixStars.Api/Engine/SixStarsRuntime.cs`
- `src/Wds.SixStars.Api/Ai/StarPromptBuilder.cs`
- `src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`

New story fields:

- `TargetMinutes`
- `TargetChunkCount`
- `MaxSentencesPerChunk`

The runtime fills these when a new story starts. It currently plans about four short chunks per minute, with a minimum of eight chunks, so the LLM has enough material for serial spoken narration.

## 3. One-or-two-sentence story chunks

The foreground story prompt now tells the LLM to generate `storyMarkdown` as numbered chunks:

```markdown
## Chunk 0
One or two story sentences.

---

## Chunk 1
One or two story sentences.
```

`spokenText` is also required to be the actual story prose, not an introduction like "Let me tell you...".

The runtime validates this after the LLM returns:

- It normalizes `storyMarkdown` into `## Chunk N` sections.
- It splits long spoken text into one-or-two-sentence TTS chunks.
- If the LLM tries to speak a preface or mismatched text, it replaces that with the expected markdown chunk.

## 4. Story requests preserve emotional reaction

Story mode now tells the Star to react emotionally through the story itself instead of through a preface. For example, if the player asks softly, the opening imagery should feel comforting; if the player asks playfully, the opening should be playful; if the seed is dark, the opening can become eerie or wondrous.

## 5. Button kept for now

The existing Tell Story button still explicitly starts Story mode. Normal chat and voice input can now also auto-enter Story mode when the pre-flight check sees that the player is asking for a story.

## Validation notes

A non-compiler syntax sweep was run to verify balanced braces/comments/strings in the edited C# files. `dotnet build` could not be run in this environment because the `dotnet` command is not installed.
