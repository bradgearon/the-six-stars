# Normal Chat complete long-form output

The dedicated desktop Story mode and its automatic lexical/LLM story-intent promotion have been removed. A request such as “tell me a one-page story” now remains a normal `Chat` request.

## Foreground behavior

- Normal Chat may return as many ordered `spokenText` chunks as needed.
- Individual `spokenText` items are no longer capped by the foreground JSON schema.
- Parsed speech chunks are whitespace-normalized but never shortened or ellipsized.
- The prompt tells the Star to finish the complete requested story or long answer in the same response instead of redirecting to Story mode or stopping after an opening sample.
- Each chunk remains a small natural TTS unit, so speech can begin as soon as the first synthesized audio is ready while later chunks continue through the existing queue.

## Desktop

- The Story seed, **Tell Me A Story**, and **Interrupt Story** controls are removed.
- The normal message box is used for stories and all other long-form requests.
- The normal desktop output binds to the complete joined `SpokenText` sequence; it does not apply an ellipsis or character limit.

## Android

- The main response window no longer clips ordinary responses to 8,000 characters or story responses to 20,000 characters.
- The production conversation panel stores and renders the complete current Star reply instead of shortening it to a 900-character preview.
- Conversation-history preview lines remain intentionally compact, but the active response window shows the complete response.
- The existing ordered audio-chunk queue speaks every returned `spokenText` item.

## Compatibility

The API retains explicit `RequestType.Story` handling for older/non-desktop callers, but normal Chat is never auto-promoted to it.
