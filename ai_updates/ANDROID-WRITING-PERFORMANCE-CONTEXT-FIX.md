# Android persisted-writing performance context fix

## Failure identified

The Android **Play jokes / poetry** command reached the desktop, but the performance-planning
request sent the Star's entire persisted shelf to the local LLM. With a large shelf this produced a
request such as:

```text
request (31113 tokens) exceeds the available context size (16384 tokens)
```

Because the LLM returned HTTP 400, no `StarInteractionResponse` was created and the normal voice
pipeline never received text to speak. The voice socket was connected; there was simply no response
to stream.

## New prompt behavior

The performance request now sends only the **12 newest untold shelf entries** as its candidate
window. The Star still chooses up to five of those candidates according to quality, recency,
emotional contrast, and character.

Each candidate writing is also bounded to 600 characters in this prompt. The request still has no
completion-token ceiling.

The full shelf remains stored in state. A successful performance marks only the selected writings as
told, so later button presses naturally move through the remaining shelf.

## Guaranteed playback fallback

The LLM is used to select and perform the candidate writings. However, a non-cancellation failure in
that optional performance-planning call no longer fails the Android bridge command. The service
falls back to the five newest untold writings verbatim and sends them through the same normal
`StarInteractionResponse` and TTS path.

A genuine foreground cancellation still cancels the request and does not trigger fallback speech.

## Result

Large shelves no longer inflate the performance prompt past the model context window, and a model
format/network/context error cannot make the Play button silently produce no voice.
