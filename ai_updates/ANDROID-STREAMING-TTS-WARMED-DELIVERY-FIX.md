# Android Streaming TTS Warmed Delivery Fix

## Problem
Build 15 moved foreground Android speech to the same live LLM delimiter stream used by desktop. The LLM response arrived faster, but Android could remain silent because the desktop opened the phone playback stream and began sending WAV chunks before the correlated text response completed. That changed the ordering of a phone voice path that had previously been stable as text first, then voice.

## Fix
The LLM and Qwen TTS still run concurrently. Every complete `§` speech chunk is queued to Qwen immediately while the LLM continues generating.

The difference is only the second hop to Android:

1. LLM streams a complete `§` thought.
2. Qwen synthesizes that thought immediately.
3. Finished WAV chunks are held in an ordered in-memory warm queue.
4. The Android text response is written first.
5. Desktop sends the normal Android `start` control.
6. Warmed WAVs flush in order immediately.
7. Any later TTS chunks continue directly to the phone as they finish.
8. Desktop sends `end` after the live TTS queue drains.

This preserves the latency benefit of overlapping LLM generation and TTS without making Android's playback/control lifecycle race the response transport.

## Implementation note
The warm queue and direct-send path share one transport gate. This matters because `MobileAudioStreamSession` assigns the authoritative `audioSequence` at send time. Holding the gate while the warm queue flushes prevents a newly generated chunk from being sent ahead of an older warmed chunk.

## Expected journal order
A healthy foreground reply should show roughly:

- `Live TTS armed before LLM`
- one or more `Live TTS chunk warmed`
- `Text response ready`
- `Starting warmed Android voice stream ... after correlated text transport`
- one or more `Warmed/live voice chunk sent`
- Android `Playback started`
- stream `end`

If Qwen has not finished a chunk before text is ready, the warm queue can be empty; later chunks still stream normally after the Android start control.
