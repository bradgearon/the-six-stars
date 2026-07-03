# Story Intent Alias Word Gate

Updated the cheap pre-flight gate for the story intent classifier.

Previously the story-intent LLM check only ran when the player message contained the standalone word `story`.

Now it runs when the message contains a story-like alias or a story narration phrase, including:

- story / stories
- storytelling / storyteller / backstory
- tale / tales / fairytale / fairy tale
- fable / fables
- legend / legends
- myth / myths
- parable / parables
- saga / sagas
- chronicle / chronicles
- folktale / folktales
- yarn / yarns
- narrative / narration
- phrases like `once upon a time`, `bedtime story`, `campfire story`, `cave story`, `short story`, `long story`
- contextual story requests like `tell an adventure`, `narrate a journey`, `continue the chapter`, or `start a scene`

The alias gate still does not directly promote the request to Story mode. It only decides whether to send the tiny intent-classifier LLM request. The classifier still makes the final decision so questions about the story system or game design can remain normal chat.

Debug changes:

- `storyIntentTrigger` shows which alias triggered the intent check.
- `storyIntentCheckSource=skipped_no_story_alias` when no alias is found.

Also updated the mock agent and StructuredStarAgent heuristic fallback to use the same shared lexicon.
