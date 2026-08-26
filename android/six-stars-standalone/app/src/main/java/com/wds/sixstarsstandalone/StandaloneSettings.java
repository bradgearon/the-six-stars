package com.wds.sixstarsstandalone;

import android.content.Context;

import org.json.JSONObject;

import java.io.*;
import java.util.Iterator;

/**
 * File-backed hosted-service settings for the server-free Android app.
 *
 * Precedence (lowest -> highest):
 *  1. app/src/main/assets/standalone-services.json
 *  2. optional app/src/main/assets/standalone-services.local.json (developer build-time secrets)
 *  3. optional <app files>/config/standalone-services.local.json (future Settings UI/runtime override)
 *
 * standalone-services.local.json is intentionally git-ignored by the repository's *.local.json rule.
 */
final class StandaloneSettings {
    final File file;
    final JSONObject root;
    private final boolean buildTimeLocalAssetLoaded;
    private final boolean runtimeOverrideLoaded;

    private StandaloneSettings(File file, JSONObject root, boolean buildTimeLocalAssetLoaded, boolean runtimeOverrideLoaded) {
        this.file = file;
        this.root = root;
        this.buildTimeLocalAssetLoaded = buildTimeLocalAssetLoaded;
        this.runtimeOverrideLoaded = runtimeOverrideLoaded;
    }

    static StandaloneSettings load(Context context) throws Exception {
        JSONObject merged;
        try (InputStream in = context.getAssets().open("standalone-services.json")) {
            merged = new JSONObject(JsonUtil.readUtf8(in));
        }

        boolean buildLocal = false;
        try (InputStream in = context.getAssets().open("standalone-services.local.json")) {
            mergeInto(merged, new JSONObject(JsonUtil.readUtf8(in)));
            buildLocal = true;
        } catch (IOException ignored) {
            // Optional local developer secrets file.
        }

        File configDir = new File(context.getFilesDir(), "config");
        if (!configDir.exists() && !configDir.mkdirs()) {
            throw new IOException("Unable to create settings directory: " + configDir.getAbsolutePath());
        }
        File runtimeOverride = new File(configDir, "standalone-services.local.json");
        boolean runtimeLocal = runtimeOverride.exists();
        if (runtimeLocal) {
            mergeInto(merged, JsonUtil.readObject(runtimeOverride));
        } else {
            migrateLegacyKeysIfPresent(configDir, merged, runtimeOverride);
            runtimeLocal = runtimeOverride.exists();
            if (runtimeLocal) mergeInto(merged, JsonUtil.readObject(runtimeOverride));
        }

        return new StandaloneSettings(runtimeOverride, merged, buildLocal, runtimeLocal);
    }

    /** Migrate only non-empty API keys from the old copied standalone-services.json layout. */
    private static void migrateLegacyKeysIfPresent(File configDir, JSONObject merged, File runtimeOverride) {
        File legacy = new File(configDir, "standalone-services.json");
        if (!legacy.exists()) return;
        try {
            JSONObject old = JsonUtil.readObject(legacy);
            JSONObject local = new JSONObject();
            copyNonEmptyApiKey(old, local, "LLM");
            copyNonEmptyApiKey(old, local, "ASR");
            copyNonEmptyApiKey(old, local, "TTS");
            if (local.length() > 0) {
                JsonUtil.writeObject(runtimeOverride, local);
                mergeInto(merged, local);
            }
        } catch (Exception ignored) {
            // Legacy migration must never prevent app startup.
        }
    }

    private static void copyNonEmptyApiKey(JSONObject source, JSONObject target, String section) throws Exception {
        JSONObject src = source.optJSONObject(section);
        if (src == null) return;
        String key = src.optString("ApiKey", "").trim();
        if (key.isEmpty()) return;
        target.put(section, new JSONObject().put("ApiKey", key));
    }

    private static void mergeInto(JSONObject target, JSONObject override) throws Exception {
        Iterator<String> keys = override.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            Object value = override.get(key);
            if (value instanceof JSONObject && target.opt(key) instanceof JSONObject) {
                mergeInto(target.getJSONObject(key), (JSONObject)value);
            } else {
                target.put(key, value);
            }
        }
    }

    String sessionId() { return root.optString("SessionId", "android-standalone"); }
    JSONObject llm() { return JsonUtil.object(root, "LLM"); }
    JSONObject asr() { return JsonUtil.object(root, "ASR"); }
    JSONObject tts() { return JsonUtil.object(root, "TTS"); }
    JSONObject runtime() { return JsonUtil.object(root, "Runtime"); }

    boolean llmEnabled() { return llm().optBoolean("Enabled", true); }
    String llmKey() { return llm().optString("ApiKey", "").trim(); }
    String llmEndpoint() { return llm().optString("EndpointBaseUrl", "https://generativelanguage.googleapis.com/v1beta"); }
    String llmModel() { return llm().optString("Model", "gemma-4-26b-a4b-it"); }
    String thinkingLevel() { return llm().optString("ThinkingLevel", "high"); }
    boolean googleSearch() { return llm().optBoolean("EnableGoogleSearch", false); }
    double temperature() { return llm().optDouble("Temperature", 0.75); }
    // No foreground/review output-token cap is configurable here by design; keep parity with desktop.
    int llmTimeoutSeconds() { return llm().optInt("TimeoutSeconds", 600); }

    boolean asrEnabled() { return asr().optBoolean("Enabled", true); }
    String asrKey() { return asr().optString("ApiKey", "").trim(); }
    String asrEndpoint() { return asr().optString("EndpointUrl", "https://api.groq.com/openai/v1/audio/transcriptions"); }
    String asrModel() { return asr().optString("Model", "whisper-large-v3-turbo"); }
    String asrLanguage() { return asr().optString("Language", "en"); }
    int asrTimeoutSeconds() { return asr().optInt("TimeoutSeconds", 60); }

    boolean ttsEnabled() { return tts().optBoolean("Enabled", true); }
    String ttsKey() { return tts().optString("ApiKey", "").trim(); }
    String ttsEndpoint() { return tts().optString("EndpointUrl", "https://dashscope-intl.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation"); }
    String enrollmentEndpoint() { return tts().optString("VoiceEnrollmentEndpointUrl", "https://dashscope-intl.aliyuncs.com/api/v1/services/audio/tts/customization"); }
    String ttsModel() { return tts().optString("Model", "qwen3-tts-vc-2026-01-22"); }
    String enrollmentModel() { return tts().optString("VoiceEnrollmentModel", "qwen-voice-enrollment"); }
    String defaultLanguage() { return tts().optString("DefaultLanguage", "English"); }
    boolean autoCloneVoices() { return tts().optBoolean("AutoCloneVoices", true); }
    int ttsTimeoutSeconds() { return tts().optInt("TimeoutSeconds", 120); }
    JSONObject configuredVoiceIds() { return JsonUtil.object(tts(), "VoiceIds"); }

    boolean romanceMode() { return runtime().optBoolean("RomanceMode", true); }
    int shortTermMemoryMinutes() { return runtime().optInt("ShortTermMemoryMinutes", 30); }
    int recentConversationTurns() { return runtime().optInt("RecentConversationTurns", 10); }
    boolean blockForReview() { return runtime().optBoolean("BlockNextForegroundUntilPrimaryReviewSettles", true); }
    boolean dailyStateEnabled() { return runtime().optBoolean("EnableDailyStateRefresh", true); }
    boolean girlfriendHourlyEnabled() { return runtime().optBoolean("EnableGirlfriendHourlyRefresh", true); }
    boolean traceEnabled() { return runtime().optBoolean("TraceEnabled", true); }

    String configurationSummary() {
        return "LLM key=" + (!llmKey().isEmpty()) + " ASR key=" + (!asrKey().isEmpty()) + " TTS key=" + (!ttsKey().isEmpty())
                + " | buildLocal=" + buildTimeLocalAssetLoaded + " runtimeLocal=" + runtimeOverrideLoaded;
    }

    String configurationHelp() {
        return "For developer builds copy app/src/main/assets/standalone-services.local.json.example to standalone-services.local.json and add the three API keys. Runtime override path: " + file.getAbsolutePath();
    }
}
