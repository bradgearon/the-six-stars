interface Env {
  GEMINI_API_KEY: string;
  GROQ_API_KEY: string;
  DASHSCOPE_API_KEY: string;
  APP_PROXY_TOKEN: string;
  ANALYTICS?: AnalyticsEngineDataset;
}

type RouteInfo = {
  provider: "google" | "groq" | "alibaba";
  upstreamUrl: string;
  authHeaderName: string;
  authHeaderValue: string;
};

const MAX_REQUEST_BYTES = 32 * 1024 * 1024;

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "GET" && url.pathname === "/health") {
      return json({ ok: true, service: "the-last-cat-six-stars-ai-proxy" }, 200);
    }

    if (request.method !== "POST") return json({ error: "method_not_allowed" }, 405);
    if (!authorized(request, env.APP_PROXY_TOKEN)) return json({ error: "unauthorized" }, 401);

    const route = resolveRoute(url, env);
    if (!route) return json({ error: "not_found" }, 404);

    const declaredLength = parseInt(request.headers.get("content-length") || "0", 10);
    if (Number.isFinite(declaredLength) && declaredLength > MAX_REQUEST_BYTES) {
      return json({ error: "request_too_large" }, 413);
    }

    const operation = cleanDimension(request.headers.get("x-sixstars-operation") || "unspecified", 64);
    const start = Date.now();

    try {
      const headers = new Headers(request.headers);
      headers.delete("host");
      headers.delete("authorization");
      headers.delete("x-goog-api-key");
      headers.delete("x-sixstars-operation");
      headers.set(route.authHeaderName, route.authHeaderValue);

      const upstream = await fetch(route.upstreamUrl, {
        method: "POST",
        headers,
        body: request.body,
        redirect: "follow"
      });

      const outHeaders = new Headers(upstream.headers);
      outHeaders.set("x-sixstars-proxy", "1");
      outHeaders.delete("set-cookie");

      // Instrument the stream without buffering it. This records end-to-end proxy duration and bytes
      // after the provider body finishes while leaving prompt/transcript/generated content opaque.
      if (upstream.body) {
        const transform = new TransformStream<Uint8Array, Uint8Array>();
        const reader = upstream.body.getReader();
        const writer = transform.writable.getWriter();
        const declaredResponseLength = parseInt(upstream.headers.get("content-length") || "0", 10) || 0;

        ctx.waitUntil(pumpResponseForAnalytics(
          reader,
          writer,
          env,
          route.provider,
          operation,
          upstream.status,
          start,
          declaredLength,
          declaredResponseLength
        ));

        return new Response(transform.readable, {
          status: upstream.status,
          statusText: upstream.statusText,
          headers: outHeaders
        });
      }

      writeAnalytics(env, route.provider, operation, upstream.status, Date.now() - start,
        declaredLength, parseInt(upstream.headers.get("content-length") || "0", 10) || 0);
      return new Response(null, {
        status: upstream.status,
        statusText: upstream.statusText,
        headers: outHeaders
      });
    } catch (_error) {
      writeAnalytics(env, route.provider, operation, 502, Date.now() - start, declaredLength, 0);
      return json({ error: "upstream_failure" }, 502);
    }
  }
};

async function pumpResponseForAnalytics(
  reader: ReadableStreamDefaultReader<Uint8Array>,
  writer: WritableStreamDefaultWriter<Uint8Array>,
  env: Env,
  provider: RouteInfo["provider"],
  operation: string,
  status: number,
  start: number,
  declaredRequestLength: number,
  declaredResponseLength: number
): Promise<void> {
  let actualResponseBytes = 0;
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      if (chunk.value) {
        actualResponseBytes += chunk.value.byteLength;
        await writer.write(chunk.value);
      }
    }
    await writer.close();
  } catch (error) {
    try { await reader.cancel(error); } catch { /* provider stream already closed */ }
    try { await writer.abort(error); } catch { /* downstream already gone */ }
  } finally {
    try { reader.releaseLock(); } catch { /* already released */ }
    writeAnalytics(
      env,
      provider,
      operation,
      status,
      Date.now() - start,
      declaredRequestLength,
      actualResponseBytes > 0 ? actualResponseBytes : declaredResponseLength
    );
  }
}

function writeAnalytics(
  env: Env,
  provider: RouteInfo["provider"],
  operation: string,
  status: number,
  elapsedMs: number,
  declaredRequestLength: number,
  responseLength: number
): void {
  // Privacy rule: analytics contains only coarse operation/provider/status/timing/byte counts.
  // Never write prompt text, transcript text, generated text/audio, IP, user-agent, Star id,
  // relationship state, session id, installation id, or any per-user identifier.
  env.ANALYTICS?.writeDataPoint({
    blobs: [provider, operation, String(Math.floor(status / 100)) + "xx"],
    doubles: [
      1,
      Math.max(0, elapsedMs),
      Number.isFinite(declaredRequestLength) && declaredRequestLength > 0 ? declaredRequestLength : 0,
      Math.max(0, responseLength),
      status
    ],
    indexes: [provider]
  });
}

function resolveRoute(url: URL, env: Env): RouteInfo | null {
  if (url.pathname.startsWith("/google/")) {
    const suffix = url.pathname.substring("/google/".length);
    // The Android app only needs generateContent and streamGenerateContent. Do not turn the
    // extractable app access token into a general-purpose proxy for every Google API method.
    if (!/^v1beta\/models\/[^/]+:(generateContent|streamGenerateContent)$/.test(suffix)) return null;
    const upstream = new URL("https://generativelanguage.googleapis.com/" + suffix);
    upstream.search = url.search;
    return {
      provider: "google",
      upstreamUrl: upstream.toString(),
      authHeaderName: "x-goog-api-key",
      authHeaderValue: env.GEMINI_API_KEY
    };
  }

  if (url.pathname === "/groq/openai/v1/audio/transcriptions") {
    const upstream = new URL("https://api.groq.com/openai/v1/audio/transcriptions");
    upstream.search = url.search;
    return {
      provider: "groq",
      upstreamUrl: upstream.toString(),
      authHeaderName: "authorization",
      authHeaderValue: "Bearer " + env.GROQ_API_KEY
    };
  }

  if (url.pathname === "/alibaba/tts") {
    return {
      provider: "alibaba",
      upstreamUrl: "https://dashscope-intl.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation",
      authHeaderName: "authorization",
      authHeaderValue: "Bearer " + env.DASHSCOPE_API_KEY
    };
  }

  if (url.pathname === "/alibaba/voice-enrollment") {
    return {
      provider: "alibaba",
      upstreamUrl: "https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization",
      authHeaderName: "authorization",
      authHeaderValue: "Bearer " + env.DASHSCOPE_API_KEY
    };
  }

  return null;
}

function authorized(request: Request, expected: string): boolean {
  if (!expected) return false;
  const value = request.headers.get("authorization") || "";
  if (!value.startsWith("Bearer ")) return false;
  return timingSafeEqual(value.substring(7), expected);
}

function timingSafeEqual(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function cleanDimension(value: string, max: number): string {
  const cleaned = value.toLowerCase().replace(/[^a-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "");
  return (cleaned || "unspecified").slice(0, max);
}

function json(value: unknown, status: number): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" }
  });
}
