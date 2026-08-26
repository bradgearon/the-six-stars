# Cloudflare proxy, private aggregate analytics, important quotes, and interstar guard

## Cloudflare provider proxy

A new independent Worker project lives at `cloudflare/six-stars-ai-proxy`.

The standalone Android application can now run in either mode:

- **Direct**: device holds Google/Groq/DashScope developer keys.
- **Proxy**: device holds only the Worker base URL plus an app access token. Provider billing keys remain Cloudflare Worker secrets.

The Worker is a transport/security boundary only. It must not become the source of truth for Star state, memory, prompt logic, deterministic scoring, profile data, girlfriend state, story state, or future game import semantics.

### Android Proxy settings

```json
"Proxy": {
  "Enabled": true,
  "BaseUrl": "https://api.example.com",
  "AccessToken": "..."
}
```

When enabled, provider endpoints are rewritten through the Worker automatically and direct provider API keys may be empty on-device.

## Privacy analytics contract

The app sends an `x-sixstars-operation` category only. The Worker may aggregate provider, operation, status, end-to-end streamed completion latency, and byte counts through Analytics Engine. Response bytes are counted while streaming and are never buffered for analytics. It must not record conversation text, ASR transcript, TTS content, audio, Star id, relationship values, memory, session/user/install identifiers, IP, country, or user-agent as custom product analytics.

This is deliberately sufficient for questions like:

- How many typed foreground chats vs voice-originated foreground chats vs background reviews are happening?
- How often is microphone transcription used?
- How much TTS / voice enrollment traffic is generated?
- Which provider is producing failures or latency?

without collecting the intimate substance of the player's conversations.

## Important-player-quote decision moved back to background LLM

The temporary deterministic "high emotion means remember the whole player message" heuristic was too broad because ordinary interactions commonly create emotions.

New rule:

1. The protected background review decides whether exact wording is unusually significant.
2. Most turns must return `importantPlayerQuotes: []`.
3. Good candidates are rare relationship milestones, promises/commitments, deeply revealing personal statements, pivotal values/moral statements, or uniquely memorable wording that the Star would realistically quote much later.
4. Routine greetings, ordinary affection/flirting/sexual lines, questions, instructions, jokes, daily updates, repeated sentiments, and merely high-emotion turns are not enough.
5. The LLM must copy exact words from the current player turn.
6. Deterministic storage does **not** judge significance anymore; it only verifies that the proposed exact quote occurs verbatim in the current player text before making it durable.
7. Maximum three per review, with zero or one preferred.

The desktop background schema now allows up to three important quotes. The standalone app uses the same copied review prompts and applies the same exact-text validation.

## Interstar Last Cat relationship guard

Diagnosis: interstar reaction/feedback requests intentionally produced a `lastCatEmotionalEffectVector`, but those synthetic internal requests were passed through the normal `RelationshipDeltaCalculator`. That meant second-hand relay feelings could:

- award Heart / Trust / Romance / EmotionalConnectionPercent toward the Last Cat;
- enter the direct-player emotion-vector history;
- later participate in combo/history scoring, creating the appearance of connection backfill for Stars the player had not directly spoken with.

That is now prohibited.

For `interstarMicroUpdate=true`:

- Last Cat H/T/R/connection delta is always zero;
- direct-player relationship accumulators are not touched;
- interstar vectors are not inserted into direct-player emotion-vector history;
- second-hand feelings may still be shown/retained as hidden interstar awareness;
- `sourceStar...` / per-Star vectors still update the separate `OtherStars` Star-to-Star Heart/Trust relationship state.

Only a **direct Last Cat interaction** may move the measurable Last Cat relationship meters.

The standalone runtime does not currently execute the desktop interstar scheduler, so it had no active interstar backfill path. Its copied interstar prompts now carry the same invariant, and any future standalone port must preserve this rule as part of the import-compatibility contract.
