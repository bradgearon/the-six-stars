# The Last Cat: the Six Stars — Cloudflare AI proxy

This Worker is intentionally tiny. The Android app still owns Star profiles, prompts, state, deterministic relationship scoring, memory, story state, and import/export. The Worker only protects provider credentials and forwards LLM/ASR/TTS traffic.

## Routes

- `POST /google/v1beta/...` -> Google Generative Language API
- `POST /groq/openai/v1/audio/transcriptions` -> Groq transcription API
- `POST /alibaba/tts` -> DashScope Qwen3-TTS synthesis
- `POST /alibaba/voice-enrollment` -> DashScope voice enrollment
- `GET /health` -> no-secret health check

Provider response bodies are streamed/passed through. The Worker does not parse or persist prompts, transcripts, generated text, audio, Star state, relationship state, or memory. Provider routes are allow-listed to only the methods the app uses; the app access token does not expose a general-purpose Google/Groq proxy.

## Deploy

```bash
cd cloudflare/six-stars-ai-proxy
npm install
npx wrangler login
npx wrangler secret put GEMINI_API_KEY
npx wrangler secret put GROQ_API_KEY
npx wrangler secret put DASHSCOPE_API_KEY
npx wrangler secret put APP_PROXY_TOKEN
npm run deploy
```

Use a long random `APP_PROXY_TOKEN`. It protects the endpoint from accidental/public unauthenticated use, but because the Android app must possess it, it is not a replacement for Play Integrity or another future app-attestation/session-token layer. The important provider billing keys remain only in Cloudflare secrets.

For local Worker development, copy `.dev.vars.example` to `.dev.vars`; `.dev.vars` is git-ignored.

## Custom domain

After buying a domain, attach a custom domain such as `api.example.com` to this Worker in Cloudflare. The GitHub Pages site can separately host terms/privacy/support on the main site/domain; GitHub Pages never needs provider secrets.

Then Android can use:

```json
{
  "Proxy": {
    "Enabled": true,
    "BaseUrl": "https://api.example.com",
    "AccessToken": "the same APP_PROXY_TOKEN"
  }
}
```

When Proxy mode is enabled, the device does not require `GEMINI_API_KEY`, `GROQ_API_KEY`, or `DASHSCOPE_API_KEY`.

## Privacy-preserving analytics

`wrangler.jsonc` binds a Workers Analytics Engine dataset named `six_stars_ai_proxy_usage`.

Each request records only:

- provider (`google`, `groq`, `alibaba`)
- coarse operation (`foreground-chat-text`, `foreground-chat-voice`, `background-review`, `daily-state`, `girlfriend-hourly`, `asr-transcription`, `tts-synthesis`, `tts-voice-enrollment`, etc.)
- status class
- count
- end-to-end proxy milliseconds through completion of the streamed upstream body
- declared request bytes when available
- provider response bytes counted while streaming (without retaining the content)
- HTTP status

The code deliberately does **not** write:

- prompt or response text
- speech transcript text
- audio bytes
- Star identity
- Heart / Trust / Romance / connection values
- memory/state contents
- session id
- installation/user id
- IP address
- country
- user-agent/device model

This gives aggregate service-cost/performance/feature-usage information without building a behavioral dossier of an individual relationship with a Star.

If richer product analytics are added later, keep this privacy boundary unless the user explicitly opts into something broader.

### Example aggregate queries

Analytics Engine maps this Worker record as: `blob1=provider`, `blob2=operation`, `blob3=status class`, `double1=count`, `double2=completion ms`, `double3=declared request bytes`, `double4=response bytes`, and `double5=HTTP status`. There is intentionally no user/install/session dimension.

Usage by interaction type over the last day:

```sql
SELECT
  blob2 AS operation,
  SUM(_sample_interval * double1) AS requests
FROM six_stars_ai_proxy_usage
WHERE timestamp > NOW() - INTERVAL '1' DAY
GROUP BY operation
ORDER BY requests DESC
```

Average and p95 completion latency by operation:

```sql
SELECT
  blob2 AS operation,
  SUM(_sample_interval * double2) / SUM(_sample_interval) AS avg_ms,
  quantileExactWeighted(0.95)(double2, _sample_interval) AS p95_ms
FROM six_stars_ai_proxy_usage
WHERE timestamp > NOW() - INTERVAL '1' DAY
GROUP BY operation
ORDER BY avg_ms DESC
```

Failure count by provider/operation:

```sql
SELECT
  blob1 AS provider,
  blob2 AS operation,
  SUM(_sample_interval) AS failures
FROM six_stars_ai_proxy_usage
WHERE timestamp > NOW() - INTERVAL '1' DAY AND double5 >= 400
GROUP BY provider, operation
ORDER BY failures DESC
```

These queries distinguish typed chat from voice-originated chat only as aggregate operation classes; they cannot reconstruct an individual's session or relationship.

## Request-size boundary

The proxy rejects declared request bodies above 32 MiB. It forwards bodies and upstream responses as streams instead of intentionally buffering AI content. A TransformStream counts response bytes and records completion timing as chunks pass through; it never parses or stores the AI payload.
