package com.wds.sixstarsstandalone;

import android.content.Context;

import org.json.JSONObject;

import java.io.*;

/** File-backed settings model. A UI editor is intentionally deferred; this file is the seam for it. */
final class StandaloneSettings {
    final File file;
    final JSONObject root;

    private StandaloneSettings(File file, JSONObject root) { this.file = file; this.root = root; }

    static StandaloneSettings load(Context context) throws Exception {
        File configDir = new File(context.getFilesDir(), "config");
        if (!configDir.exists()) configDir.mkdirs();
        File target = new File(configDir, "standalone-services.json");
        if (!target.exists()) {
            try (InputStream in = context.getAssets().open("standalone-services.json"); OutputStream out = new FileOutputStream(target)) {
                byte[] b = new byte[8192]; int n; while ((n = in.read(b)) >= 0) out.write(b, 0, n);
            }
        }
        return new StandaloneSettings(target, JsonUtil.readObject(target));
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
        return "config=" + file.getAbsolutePath() + " | LLM key=" + (!llmKey().isEmpty()) + " ASR key=" + (!asrKey().isEmpty()) + " TTS key=" + (!ttsKey().isEmpty());
    }
}
