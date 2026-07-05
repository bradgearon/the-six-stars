# Clear Text Boxes + Automatic Story Seed Update

- Desktop `PlayerText` and `StorySuggestion` now start blank.
- Desktop typed text boxes clear after a manual request is queued, while the queued request keeps its captured text/suggestion.
- `Send Message` no longer injects a default greeting; it asks for typed text instead.
- `Tell Me A Story` no longer injects a default cave-story prompt. If the message and story seed are blank, the selected Star generates her own story from persona, lore, relationship state, and story history.
- Android message box now starts blank with a hint and clears immediately after `Send Message` queues the HTTP request.
- Android still allows a blank message when the request type is `Story`, so the selected Star can invent the story herself.
