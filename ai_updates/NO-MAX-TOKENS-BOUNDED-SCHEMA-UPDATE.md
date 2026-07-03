# No Max Tokens / Bounded Schema Update

This update removes the default per-request `max_tokens` caps from the Six Stars LLM flow.

## Why

The foreground and background requests should not fail just because a hard token cap was too low. The system should stay powerful by asking for less unnecessary data, not by truncating the model.

## Changed defaults

`0` now means: do not send `max_tokens`; let the local backend/model context limit apply.

```json
"LLM": {
  "MaxTokens": 0,
  "SpeechMaxTokens": 0,
  "ReviewMaxTokens": 0
}
```

You can still set a positive value to force a cap:

```bat
set SIX_STARS_LLM_MAX_TOKENS=8192
set SIX_STARS_LLM_SPEECH_MAX_TOKENS=1200
set SIX_STARS_LLM_REVIEW_MAX_TOKENS=2400
```

## How output size is controlled now

The foreground pass is still text-only:

- normal chat: `starId`, `spokenText`
- story start/continuation: story fields plus ordered `spokenText`

The background review uses bounded update slots instead of a token cap:

- up to 9 impactful emotions
- short emotion reasons
- short bounded arrays for quotes, memory notes, relationship events, learned functions, lore, and risk flags
- no voice directive
- no story markdown
- no full 32-emotion matrix

## Debug expectation

The LLM debug panel should now show:

```text
MaxTokens: not sent; backend/model default
```

unless you explicitly configure a positive max-token value.
