package com.wds.sixstarsstandalone;

import okhttp3.*;
import okio.BufferedSource;
import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/** Native Android equivalent of GoogleAiStudioChatModelClient. */
final class GoogleAiStudioClient {
    private final StandaloneSettings settings;
    private final OkHttpClient client;

    GoogleAiStudioClient(StandaloneSettings settings) {
        this.settings = settings;
        this.client = new OkHttpClient.Builder()
                .connectTimeout(30, TimeUnit.SECONDS)
                .readTimeout(Math.max(30, settings.llmTimeoutSeconds()), TimeUnit.SECONDS)
                .writeTimeout(60, TimeUnit.SECONDS)
                .build();
    }

    String completeStreaming(List<ChatMessage> messages, Consumer<String> onDelta) throws Exception {
        requireConfigured();
        JSONObject payload = buildPayload(messages, false, settings.googleSearch());
        Request request = request(endpoint("streamGenerateContent") + "?alt=sse", payload);
        StringBuilder all = new StringBuilder();
        try (Response response = client.newCall(request).execute()) {
            if (!response.isSuccessful()) throw new IllegalStateException("Google AI Studio stream failed " + response.code() + ": " + safeBody(response));
            if (response.body() == null) throw new IllegalStateException("Google AI Studio returned no body.");
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(response.body().byteStream(), StandardCharsets.UTF_8))) {
                String line; StringBuilder event = new StringBuilder();
                while ((line = reader.readLine()) != null) {
                    if (line.isEmpty()) { processSse(event.toString(), all, onDelta); event.setLength(0); }
                    else if (line.startsWith("data:")) { if (event.length() > 0) event.append('\n'); event.append(line.substring(5).trim()); }
                }
                if (event.length() > 0) processSse(event.toString(), all, onDelta);
            }
        }
        if (all.toString().trim().isEmpty()) throw new IllegalStateException("Google AI Studio returned no text.");
        return all.toString();
    }

    String completeJson(List<ChatMessage> messages) throws Exception {
        requireConfigured();
        JSONObject payload = buildPayload(messages, true, false);
        try {
            return completeOnce(payload, "json");
        } catch (Http400 e) {
            // Match desktop hosted adapter behavior: keep provider, retry without responseMimeType.
            payload = buildPayload(messages, false, false);
            return completeOnce(payload, "prompt-json");
        }
    }

    private String completeOnce(JSONObject payload, String mode) throws Exception {
        Request request = request(endpoint("generateContent"), payload);
        try (Response response = client.newCall(request).execute()) {
            String body = safeBody(response);
            if (!response.isSuccessful()) {
                if (response.code() == 400) throw new Http400(body);
                throw new IllegalStateException("Google AI Studio " + mode + " failed " + response.code() + ": " + body);
            }
            String text = extractText(new JSONObject(body));
            if (text.trim().isEmpty()) throw new IllegalStateException("Google AI Studio returned no candidate text.");
            return text;
        }
    }

    private JSONObject buildPayload(List<ChatMessage> messages, boolean wantsJson, boolean allowSearch) throws Exception {
        StringBuilder system = new StringBuilder();
        JSONArray contents = new JSONArray();
        String lastRole = null; JSONObject current = null; JSONArray parts = null;
        for (ChatMessage m : messages) {
            if ("system".equalsIgnoreCase(m.role)) { if (system.length() > 0) system.append("\n\n"); system.append(m.content); continue; }
            String role = ("assistant".equalsIgnoreCase(m.role) || "model".equalsIgnoreCase(m.role)) ? "model" : "user";
            if (role.equals(lastRole) && current != null) parts.put(new JSONObject().put("text", m.content));
            else {
                parts = new JSONArray().put(new JSONObject().put("text", m.content));
                current = new JSONObject().put("role", role).put("parts", parts); contents.put(current); lastRole = role;
            }
        }
        if (contents.length() == 0) contents.put(new JSONObject().put("role","user").put("parts",new JSONArray().put(new JSONObject().put("text",""))));
        JSONObject gen = new JSONObject();
        gen.put("temperature", settings.temperature());
        gen.put("thinkingConfig", new JSONObject().put("thinkingLevel", normalizeThinking(settings.thinkingLevel())));
        // Deliberately do not send maxOutputTokens. The desktop Star runtime also lets the model stop
        // on EOS/context; artificial per-request caps can truncate speech, reviews, and state JSON.
        if (wantsJson) gen.put("responseMimeType", "application/json");
        JSONObject payload = new JSONObject().put("contents", contents).put("generationConfig", gen);
        if (system.length() > 0) payload.put("systemInstruction", new JSONObject().put("parts", new JSONArray().put(new JSONObject().put("text",system.toString()))));
        if (allowSearch) payload.put("tools", new JSONArray().put(new JSONObject().put("googleSearch", new JSONObject())));
        return payload;
    }

    private Request request(String url, JSONObject payload) {
        return new Request.Builder().url(url)
                .header("x-goog-api-key", settings.llmKey())
                .post(RequestBody.create(payload.toString(), MediaType.get("application/json; charset=utf-8")))
                .build();
    }

    private String endpoint(String method) {
        String base = settings.llmEndpoint().replaceAll("/+$", "");
        return base + "/models/" + settings.llmModel() + ":" + method;
    }

    private void processSse(String data, StringBuilder all, Consumer<String> onDelta) {
        if (data == null || data.trim().isEmpty() || "[DONE]".equals(data.trim())) return;
        try {
            String text = extractText(new JSONObject(data));
            if (!text.isEmpty()) { all.append(text); if (onDelta != null) onDelta.accept(text); }
        } catch (Exception ignored) { }
    }

    private static String extractText(JSONObject root) {
        StringBuilder b = new StringBuilder(); JSONArray candidates = root.optJSONArray("candidates"); if (candidates == null) return "";
        for (int i=0;i<candidates.length();i++) { JSONObject c=candidates.optJSONObject(i); if(c==null)continue; JSONObject content=c.optJSONObject("content"); if(content==null)continue; JSONArray parts=content.optJSONArray("parts"); if(parts==null)continue; for(int j=0;j<parts.length();j++){JSONObject p=parts.optJSONObject(j); if(p!=null && p.has("text")) b.append(p.optString("text",""));} }
        return b.toString();
    }

    private static String safeBody(Response response) throws Exception { return response.body() == null ? "" : response.body().string(); }
    private static String normalizeThinking(String v) { String x=v==null?"":v.trim().toLowerCase(Locale.ROOT); return (x.equals("minimal")||x.equals("off")||x.equals("none"))?"minimal":"high"; }
    private void requireConfigured() { if (!settings.llmEnabled()) throw new IllegalStateException("Hosted LLM is disabled."); if (settings.llmKey().isEmpty()) throw new IllegalStateException("Google AI Studio key is empty. Configure " + settings.file.getAbsolutePath()); }
    private static final class Http400 extends Exception { Http400(String s){super(s);} }
}
