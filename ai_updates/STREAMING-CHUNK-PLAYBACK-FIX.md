# Streaming Chunk Playback Fix

Updated `StreamingStarVoiceResponsePipeline` so WebSocket TTS playback drains every queued audio chunk before ending.

## Problem

The first chunk could play, then playback could exit before the second chunk was heard. The old loop could stop based on the chunk header's `chunkIndex/totalChunks` instead of the stronger condition: generation complete, queue empty, and every expected/received chunk processed.

## Fix

- Track generation completion and expected chunk count at the pipeline level.
- Never exit just because the current chunk claims it is the last chunk.
- Continue playback until the queue is empty and processed chunk count matches expected/received chunks.
- Add clearer debug logging for received, queued, processed, played, and expected chunk counts.
- Add a small 75 ms gap between separate WAV chunk playbacks so Windows/NAudio has time to release and reopen the output endpoint.
- Playback errors on one chunk are logged and the next queued chunk is still attempted.

## What to watch in DebugJson / Output

Look for lines like:

```text
Playing chunk 1/2 ... Queue remaining after dequeue: 1
Chunk 1/2 playback complete. Played 1, received 2, expected 2.
Playing chunk 2/2 ...
Chunk 2/2 playback complete. Played 2, received 2, expected 2.
Generation complete and queue empty. Processed 2, received 2, expected 2.
```
