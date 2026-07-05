# Story Seed Repeat / Prompt Cleanup / History Cleanup Update

- Story mode now treats the player's story seed as a hard creative requirement.
- Prior stories are presented as forbidden repeat plots, not as available story-deck choices.
- Removed the story deck menu from the story prompt so the model does not keep choosing the same waterskin story.
- Angel stories are now included only as lore memory, with an explicit instruction not to retell them unless the player asks for that exact story.
- Story foreground output no longer asks for emotion vectors; background review handles emotionalEffectVector and negativeEmotionVector.
- Story prompts no longer use the normal fast-chat heading; story mode uses a story foreground contract.
- storyIsFinished now means the generated story is a complete episode, not that local audio autoplay has already spoken every cached chunk.
- Recent deterministic emotion update history is no longer sent to the background LLM review prompt.
- Parser-fallback replies such as “thoughts came back in a shape the system could not fully read” are filtered out of recent conversation history, last remembered reply, and background review history so they do not become in-character memory.
- Technical JSON salvage notes are filtered out of prompt memory context.
- Kaelia's domain was corrected from Fatalism to Mystery; Void is treated as strange, mysterious force rather than fatalism.
- Elusiaa's Last Cat relationship note now says she sometimes calls him “little one,” leaving room for other names and pet names.
