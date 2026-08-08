# Star Writing Rating Latency and Kaelia Fix

## What the timing log meant

LM Studio's `prompt_ms` measures prompt ingestion only. It is not the request duration. The expensive field is `predicted_ms`, which measures completion generation.

In the captured run:

- Elusiaa generation: about 4.8 seconds wall time; 2.17 seconds were completion generation.
- Elusiaa review: about 10.6 seconds wall time; 10.42 seconds were completion generation for 1,331 output tokens.

The button was awaiting direct HTTP requests. It was not polling.

## Changes

- The review wire response now uses five flat arrays of 54 integers instead of 135 quoted `score:confidence` strings.
- Each pair is `score, confidence-decile`; the host converts the decile back to a 0-1 confidence and still stores one compact `score:confidence;...` string.
- Review output is capped at 1,200 tokens.
- A 26-pair response is accepted by deriving only the missing final Overall pair from core quality categories.
- A 28-pair response is reduced to the requested first 27 pairs.
- Old malformed history is no longer automatically re-rated before every fresh generation. This removes hidden review/repair calls and prevents legacy data from blocking Kaelia.
- Explicit romance/girlfriend-mode behavior lines are filtered from writing-generation and writing-review character cards.
- Stage timing is written to Debug output as `[StarWritingTiming]`.

## Storage compatibility

Persisted writing notes still store only the original compact vector string format. No expanded emotional-value object is reintroduced.
