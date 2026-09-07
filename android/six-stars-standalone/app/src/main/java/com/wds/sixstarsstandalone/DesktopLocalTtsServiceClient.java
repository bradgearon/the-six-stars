package com.wds.sixstarsstandalone;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import org.json.JSONObject;

import java.util.concurrent.TimeUnit;

/**
 * Local-TTS bridge for the standalone Android app.
 *
 * Android sends only StarId + text to the desktop .NET bridge. The desktop runtime owns the
 * canonical Star profile/content lookup, resolves voiceCloneAudioFile to the same absolute path
 * used by normal desktop speech, and then invokes the local qwentts.cpp HTTP adapter.
 *
 * Android never needs to know the qwentts.cpp port or clone-file path; the desktop .NET runtime
 * owns both the voice registration and the native HTTP request.
 */
final class DesktopLocalTtsServiceClient implements StarTtsClient {
    private static final MediaType JSON = MediaType.get("application/json; charset=utf-8");
    private final StandaloneSettings settings;
    private final OkHttpClient client;

    DesktopLocalTtsServiceClient(StandaloneSettings settings) {
        this.settings = settings;
        int timeout = Math.max(30, settings.localTtsTimeoutSeconds());
        client = new OkHttpClient.Builder()
                .connectTimeout(Math.min(30, timeout), TimeUnit.SECONDS)
                .readTimeout(timeout, TimeUnit.SECONDS)
                .writeTimeout(Math.min(30, timeout), TimeUnit.SECONDS)
                .build();
    }

    @Override public String modeName() { return "local-dotnet-qwen-bridge"; }

    @Override
    public TtsAudioResult synthesize(JSONObject profile, String text) throws Exception {
        if (!settings.ttsEnabled()) throw new IllegalStateException("TTS is disabled.");
        String url = settings.localTtsServiceUrl();
        if (url.isEmpty()) throw new IllegalStateException("TTS local service mode is selected but TTS.LocalServiceUrl is empty.");
        String spoken = text == null ? "" : text.trim();
        if (spoken.isEmpty()) throw new IllegalArgumentException("TTS text is empty.");

        String starId = profile.optString("starId", profile.optString("displayName", "Elusiaa"));
        if (starId == null || starId.trim().isEmpty()) starId = "Elusiaa";

        JSONObject payload = new JSONObject()
                .put("starId", starId.trim())
                .put("text", spoken);

        Request request = new Request.Builder()
                .url(url)
                .header("x-sixstars-operation", "tts-local-desktop")
                .post(RequestBody.create(payload.toString(), JSON))
                .build();

        try (Response response = client.newCall(request).execute()) {
            if (!response.isSuccessful()) {
                String body = response.body() == null ? "" : response.body().string();
                throw new IllegalStateException("Desktop local TTS service failed " + response.code() + " at " + url + ": " + body);
            }
            if (response.body() == null) throw new IllegalStateException("Desktop local TTS service returned no audio body.");
            byte[] wav = response.body().bytes();
            if (wav.length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') {
                throw new IllegalStateException("Desktop local TTS service did not return RIFF/WAV audio.");
            }
            return new TtsAudioResult(wav, response.header("Content-Type", "audio/wav"));
        }
    }
}
