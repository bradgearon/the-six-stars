package com.wds.sixstarsstandalone;

import android.content.Context;

import okhttp3.*;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/** Native Android equivalent of GoogleAiStudioChatModelClient, with Gemma thinking disabled. */
final class GoogleAiStudioClient {
    private static final String THINK_TRIGGER = "<|think|>";
    private final StandaloneSettings settings;
    private final OkHttpClient client;
    private final NetworkReadyService network;
    private final RuntimeTrace trace;

    GoogleAiStudioClient(Context context, StandaloneSettings settings, RuntimeTrace trace) {
        this.settings = settings;
        this.trace = trace;
        this.network = new NetworkReadyService(context, trace);
        this.client = new OkHttpClient.Builder()
                .connectTimeout(30, TimeUnit.SECONDS)
                .readTimeout(Math.max(30, settings.llmTimeoutSeconds()), TimeUnit.SECONDS)
                .writeTimeout(60, TimeUnit.SECONDS)
                .retryOnConnectionFailure(true)
                .build();
    }

    void onAppResumed() {
        // Sleep/wake can leave an old pooled route behind. This does not cancel active calls; it only
        // prevents the next request from reusing an idle pre-sleep connection.
        client.connectionPool().evictAll();
        network.onAppResumed();
        if (trace != null) trace.log("llm.resume-refresh", "connectionPool=evicted");
    }

    String completeStreaming(List<ChatMessage> messages, String operation, Consumer<String> onDelta) throws Exception {
        requireConfigured();
        JSONObject payload = buildPayload(messages, false, settings.googleSearch(), null, false);
        Request request = request(endpoint("streamGenerateContent") + "?alt=sse", payload, operation);
        StringBuilder all = new StringBuilder();
        GemmaFinalTextFilter finalFilter = new GemmaFinalTextFilter();
        try (Response response = executeWithWakeRecovery(request, operation + "/stream")) {
            if (!response.isSuccessful()) throw new IllegalStateException("Google AI Studio stream failed " + response.code() + ": " + safeBody(response));
            if (response.body() == null) throw new IllegalStateException("Google AI Studio returned no body.");
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(response.body().byteStream(), StandardCharsets.UTF_8))) {
                String line; StringBuilder event = new StringBuilder();
                while ((line = reader.readLine()) != null) {
                    if (line.isEmpty()) {
                        processSse(event.toString(), finalFilter, all, onDelta);
                        event.setLength(0);
                    } else if (line.startsWith("data:")) {
                        if (event.length() > 0) event.append('\n');
                        event.append(line.substring(5).trim());
                    }
                }
                if (event.length() > 0) processSse(event.toString(), finalFilter, all, onDelta);
                String tail = finalFilter.finish();
                if (!tail.isEmpty()) {
                    all.append(tail);
                    if (onDelta != null) onDelta.accept(tail);
                }
            }
        }
        if (all.toString().trim().isEmpty()) throw new IllegalStateException("Google AI Studio returned no final text.");
        return all.toString();
    }

    String completeJson(List<ChatMessage> messages, String operation) throws Exception {
        return completeJson(messages, operation, null);
    }

    /**
     * Non-streaming structured JSON call. Background memory review uses this path intentionally:
     * generationConfig.responseFormat.text.mimeType=application/json plus .schema containing the RAW
     * inner JSON Schema (not an OpenAI response_format wrapper). If the model rejects schema mode we
     * keep the same provider/prompt and fall back to JSON MIME mode, then prompt-enforced JSON.
     */
    String completeJson(List<ChatMessage> messages, String operation, JSONObject responseJsonSchema) throws Exception {
        requireConfigured();
        JSONObject payload = buildPayload(messages, true, false, responseJsonSchema, false);
        if (trace != null) trace.log("llm.json-start", "op=" + operation + " schema=" + (responseJsonSchema == null ? "none" : "primitive-only") + " requestChars=" + payload.toString().length());
        long started = System.nanoTime();
        try {
            String result = completeOnce(payload, responseJsonSchema == null ? "json" : "json-schema", operation);
            if (trace != null) trace.log("llm.json-complete", "op=" + operation + " mode=" + (responseJsonSchema == null ? "json" : "json-schema") + " elapsedMs=" + TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started) + " chars=" + result.length());
            return result;
        } catch (Http400 schemaError) {
            if (responseJsonSchema != null && trace != null) trace.log("llm.json-schema-rejected", "op=" + operation + " mode=current-responseFormat error=" + schemaError.getMessage());
            // A few older GenerateContent deployments still accept the legacy responseMimeType /
            // responseJsonSchema fields. Try that only after a literal HTTP 400, never as a latency retry.
            if (responseJsonSchema != null) {
                try {
                    payload = buildPayload(messages, true, false, responseJsonSchema, true);
                    String result = completeOnce(payload, "json-schema-legacy", operation);
                    if (trace != null) trace.log("llm.json-complete", "op=" + operation + " mode=json-schema-legacy elapsedMs=" + TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started) + " chars=" + result.length());
                    return result;
                } catch (Http400 legacySchemaError) {
                    if (trace != null) trace.log("llm.json-schema-rejected", "op=" + operation + " mode=legacy-responseJsonSchema error=" + legacySchemaError.getMessage());
                }
            }
            try {
                payload = buildPayload(messages, true, false, null, false);
                String result = completeOnce(payload, "json", operation);
                if (trace != null) trace.log("llm.json-complete", "op=" + operation + " mode=json-fallback elapsedMs=" + TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started) + " chars=" + result.length());
                return result;
            } catch (Http400 jsonModeError) {
                payload = buildPayload(messages, false, false, null, false);
                String result = completeOnce(payload, "prompt-json", operation);
                if (trace != null) trace.log("llm.json-complete", "op=" + operation + " mode=prompt-json-fallback elapsedMs=" + TimeUnit.NANOSECONDS.toMillis(System.nanoTime()-started) + " chars=" + result.length());
                return result;
            }
        }
    }

    private String completeOnce(JSONObject payload, String mode, String operation) throws Exception {
        Request request = request(endpoint("generateContent"), payload, operation);
        try (Response response = executeWithWakeRecovery(request, operation + "/" + mode)) {
            String body = safeBody(response);
            if (!response.isSuccessful()) {
                if (response.code() == 400) throw new Http400(body);
                throw new IllegalStateException("Google AI Studio " + mode + " failed " + response.code() + ": " + body);
            }
            String text = stripGemmaThinkingEnvelope(extractText(new JSONObject(body)));
            if (text.trim().isEmpty()) throw new IllegalStateException("Google AI Studio returned no candidate final text.");
            return text;
        }
    }

    private Response executeWithWakeRecovery(Request request, String operation) throws Exception {
        // Normally this is an immediate capability check. After screen-off/wake it waits on Android's
        // network callback instead of allowing the first DNS lookup to race Wi-Fi restoration.
        boolean ready = network.awaitValidatedInternet(8_000L);
        if (trace != null) trace.log("llm.network-before-request", "op=" + operation + " validated=" + ready + " " + network.describe());

        IOException first = null;
        for (int attempt = 1; attempt <= 2; attempt++) {
            try {
                return client.newCall(request).execute();
            } catch (IOException error) {
                if (attempt >= 2 || !NetworkReadyService.isTransientWakeNetworkFailure(error)) throw error;
                first = error;
                if (trace != null) trace.log("llm.wake-network-retry", "op=" + operation + " attempt=" + attempt + " error=" + error);
                client.connectionPool().evictAll();
                network.awaitValidatedInternet(10_000L);
                // One short backoff gives Android's DNS resolver a chance to attach to the restored
                // default network. This is a single retry, not a polling loop.
                Thread.sleep(250L);
            }
        }
        throw first == null ? new IOException("Google request failed without a response.") : first;
    }

    private JSONObject buildPayload(List<ChatMessage> messages, boolean wantsJson, boolean allowSearch, JSONObject responseJsonSchema, boolean legacyJsonFormat) throws Exception {
        StringBuilder system = new StringBuilder();
        JSONArray contents = new JSONArray();
        String lastRole = null; JSONObject current = null; JSONArray parts = null;
        for (ChatMessage m : messages) {
            String clean = disableGemmaThinking(m.content);
            if ("system".equalsIgnoreCase(m.role)) {
                if (system.length() > 0) system.append("\n\n");
                system.append(clean);
                continue;
            }
            String role = ("assistant".equalsIgnoreCase(m.role) || "model".equalsIgnoreCase(m.role)) ? "model" : "user";
            if (role.equals(lastRole) && current != null) parts.put(new JSONObject().put("text", clean));
            else {
                parts = new JSONArray().put(new JSONObject().put("text", clean));
                current = new JSONObject().put("role", role).put("parts", parts);
                contents.put(current); lastRole = role;
            }
        }
        if (contents.length() == 0) contents.put(new JSONObject().put("role","user").put("parts",new JSONArray().put(new JSONObject().put("text",""))));
        JSONObject gen = new JSONObject();
        gen.put("temperature", settings.temperature());

        // IMPORTANT: Gemma 4 thinking is enabled by the <|think|> control token. The standalone app
        // never sends that token and never sends thinkingConfig. We want the exact fast, non-reasoning
        // behavior used by the local app. Returned thought=true parts and textual thought envelopes are
        // also filtered defensively so reasoning can never reach UI/TTS/history/review parsing.
        // Deliberately do not send maxOutputTokens; let EOS/context terminate naturally like desktop.
        if (wantsJson) {
            if (legacyJsonFormat) {
                // Compatibility only. Current Gemini GenerateContent REST uses responseFormat below.
                gen.put("responseMimeType", "application/json");
                if (responseJsonSchema != null) gen.put("responseJsonSchema", responseJsonSchema);
            } else {
                // Current Google GenerateContent structured-output shape:
                // generationConfig.responseFormat.text.mimeType + .schema.
                JSONObject textFormat = new JSONObject().put("mimeType", "application/json");
                if (responseJsonSchema != null) textFormat.put("schema", responseJsonSchema);
                gen.put("responseFormat", new JSONObject().put("text", textFormat));
            }
        }
        JSONObject payload = new JSONObject().put("contents", contents).put("generationConfig", gen);
        if (system.length() > 0) payload.put("systemInstruction", new JSONObject().put("parts", new JSONArray().put(new JSONObject().put("text",disableGemmaThinking(system.toString())))));
        if (allowSearch) payload.put("tools", new JSONArray().put(new JSONObject().put("googleSearch", new JSONObject())));
        return payload;
    }

    private Request request(String url, JSONObject payload, String operation) {
        Request.Builder builder = new Request.Builder().url(url)
                .header("x-sixstars-operation", operation == null ? "llm" : operation)
                .post(RequestBody.create(payload.toString(), MediaType.get("application/json; charset=utf-8")));
        if (settings.proxyEnabled()) builder.header("Authorization", "Bearer " + settings.proxyAccessToken());
        else builder.header("x-goog-api-key", settings.llmKey());
        return builder.build();
    }

    private String endpoint(String method) {
        String base = settings.llmEndpoint().replaceAll("/+$", "");
        return base + "/models/" + settings.llmModel() + ":" + method;
    }

    private void processSse(String data, GemmaFinalTextFilter finalFilter, StringBuilder all, Consumer<String> onDelta) {
        if (data == null || data.trim().isEmpty() || "[DONE]".equals(data.trim())) return;
        try {
            String raw = extractText(new JSONObject(data));
            String text = finalFilter.accept(raw);
            if (!text.isEmpty()) {
                all.append(text);
                if (onDelta != null) onDelta.accept(text);
            }
        } catch (Exception ignored) { }
    }

    private static String extractText(JSONObject root) {
        StringBuilder b = new StringBuilder();
        JSONArray candidates = root.optJSONArray("candidates");
        if (candidates == null) return "";
        for (int i = 0; i < candidates.length(); i++) {
            JSONObject c = candidates.optJSONObject(i);
            if (c == null) continue;
            JSONObject content = c.optJSONObject("content");
            if (content == null) continue;
            JSONArray parts = content.optJSONArray("parts");
            if (parts == null) continue;
            for (int j = 0; j < parts.length(); j++) {
                JSONObject p = parts.optJSONObject(j);
                if (p == null || p.optBoolean("thought", false) || !p.has("text")) continue;
                b.append(p.optString("text", ""));
            }
        }
        return b.toString();
    }

    private static String disableGemmaThinking(String value) {
        if (value == null || value.isEmpty()) return "";
        return value.replace(THINK_TRIGGER, "");
    }

    /** Strip textual Gemma channel wrappers in case a backend serializes them as ordinary text. */
    private static String stripGemmaThinkingEnvelope(String value) {
        GemmaFinalTextFilter f = new GemmaFinalTextFilter();
        return f.accept(value) + f.finish();
    }

    /**
     * Streaming filter for Gemma channel-token output. It intentionally holds only a tiny suffix so
     * a control marker split across SSE events cannot leak reasoning into the live transcript/TTS.
     */
    private static final class GemmaFinalTextFilter {
        private static final String START_A = "<|channel>thought";
        private static final String START_B = "<|channel|>thought";
        private static final String END_A = "<channel|>";
        private static final String END_B = "<|channel|>final";
        private static final int HOLD = 24;
        private final StringBuilder pending = new StringBuilder();
        private boolean suppressingThought;

        String accept(String value) {
            if (value != null && !value.isEmpty()) pending.append(value);
            StringBuilder out = new StringBuilder();
            drain(out, false);
            return out.toString();
        }

        String finish() {
            StringBuilder out = new StringBuilder();
            drain(out, true);
            return out.toString();
        }

        private void drain(StringBuilder out, boolean force) {
            while (pending.length() > 0) {
                if (suppressingThought) {
                    int endA = pending.indexOf(END_A);
                    int endB = pending.indexOf(END_B);
                    int end = firstNonNegative(endA, endB);
                    if (end < 0) {
                        if (force) { pending.setLength(0); return; }
                        if (pending.length() > HOLD) pending.delete(0, pending.length() - HOLD);
                        return;
                    }
                    int markerLength = end == endA ? END_A.length() : END_B.length();
                    pending.delete(0, end + markerLength);
                    suppressingThought = false;
                    trimLeadingChannelNoise();
                    continue;
                }

                int startA = pending.indexOf(START_A);
                int startB = pending.indexOf(START_B);
                int start = firstNonNegative(startA, startB);
                if (start >= 0) {
                    if (start > 0) out.append(pending, 0, start);
                    int markerLength = start == startA ? START_A.length() : START_B.length();
                    pending.delete(0, start + markerLength);
                    suppressingThought = true;
                    continue;
                }

                if (force) {
                    out.append(cleanFinalChannelMarkers(pending.toString()));
                    pending.setLength(0);
                    return;
                }
                if (pending.length() <= HOLD) return;
                int emit = pending.length() - HOLD;
                out.append(pending, 0, emit);
                pending.delete(0, emit);
            }
        }

        private void trimLeadingChannelNoise() {
            while (pending.length() > 0 && (pending.charAt(0) == '\n' || pending.charAt(0) == '\r')) pending.deleteCharAt(0);
            String s = pending.toString();
            String cleaned = cleanFinalChannelMarkers(s);
            if (!cleaned.equals(s)) {
                pending.setLength(0);
                pending.append(cleaned);
            }
        }

        private static String cleanFinalChannelMarkers(String value) {
            if (value == null || value.isEmpty()) return "";
            return value
                    .replace("<|channel>final", "")
                    .replace("<|channel|>final", "")
                    .replace("<channel|>", "");
        }

        private static int firstNonNegative(int a, int b) {
            if (a < 0) return b;
            if (b < 0) return a;
            return Math.min(a, b);
        }
    }

    private static String safeBody(Response response) throws Exception { return response.body() == null ? "" : response.body().string(); }

    private void requireConfigured() {
        if (!settings.llmEnabled()) throw new IllegalStateException("Hosted LLM is disabled.");
        if (settings.proxyEnabled()) {
            if (!settings.proxyConfigured()) throw new IllegalStateException("Cloudflare proxy is enabled but BaseUrl/AccessToken is incomplete. Configure " + settings.file.getAbsolutePath());
        } else if (settings.llmKey().isEmpty()) throw new IllegalStateException("Google AI Studio key is empty. Configure " + settings.file.getAbsolutePath());
    }

    private static final class Http400 extends Exception { Http400(String s){super(s);} }
}
