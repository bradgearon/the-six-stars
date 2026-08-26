package com.wds.sixstarsstandalone;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.ByteString;
import org.json.JSONArray;
import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Android client for the repository's existing scripts/websocket_tts_server.py protocol.
 *
 * The desktop launcher starts that server on port 8100 and the desktop application uses the same
 * generate payload/binary WAV framing through WebSocketTtsVoiceGenerator. The Android standalone
 * app can point this client at the desktop machine's LAN address and therefore use the exact local
 * Qwen3-TTS/voice-clone service without consuming hosted TTS quota.
 */
final class LocalQwenTtsClient implements StarTtsClient {
    private static final AtomicInteger NEXT_REQUEST_ID = new AtomicInteger();
    private final StandaloneSettings settings;
    private final OkHttpClient client;

    LocalQwenTtsClient(StandaloneSettings settings) {
        this.settings = settings;
        int timeout = Math.max(30, settings.localTtsTimeoutSeconds());
        client = new OkHttpClient.Builder()
                .connectTimeout(Math.min(30, timeout), TimeUnit.SECONDS)
                .readTimeout(0, TimeUnit.SECONDS) // WebSocket lifecycle is bounded by our latch timeout.
                .writeTimeout(Math.min(30, timeout), TimeUnit.SECONDS)
                .build();
    }

    @Override public String modeName() { return "local-qwen-websocket"; }

    @Override
    public TtsAudioResult synthesize(JSONObject profile, String text) throws Exception {
        if (!settings.ttsEnabled()) throw new IllegalStateException("TTS is disabled.");
        String url = settings.localTtsWebSocketUrl();
        if (url.isEmpty()) throw new IllegalStateException("TTS local mode is selected but TTS.LocalWebSocketUrl is empty.");
        if (text == null || text.trim().isEmpty()) throw new IllegalArgumentException("TTS text is empty.");

        int requestId = NEXT_REQUEST_ID.incrementAndGet();
        String starId = profile.optString("starId", profile.optString("displayName", "star"));
        String voiceId = profile.optString("voiceId", starId);
        String cloneAudio = profile.optString("voiceCloneAudioFile", "");
        String cloneRefText = profile.optString("voiceCloneRefText", "");

        JSONObject payload = new JSONObject()
                .put("type", "generate")
                .put("requestId", requestId)
                .put("text", text)
                // Explicit one-item textParts tells the server not to split this already sentence-sized
                // Android chunk again. This keeps one queueTts() call -> one WAV response.
                .put("textParts", new JSONArray().put(text))
                .put("starId", starId)
                .put("voiceId", voiceId)
                .put("model", settings.localTtsModel())
                .put("language", settings.localTtsLanguage())
                .put("responseFormat", "wav")
                .put("taskType", settings.localTtsTaskType())
                .put("speed", settings.localTtsSpeed())
                .put("chunkGenerationMode", "first_then_prefetch")
                .put("maxParallelChunks", 1);
        if (!cloneAudio.isEmpty()) payload.put("voiceCloneAudio", cloneAudio);
        if (!cloneRefText.isEmpty()) payload.put("voiceCloneRefText", cloneRefText);

        CountDownLatch done = new CountDownLatch(1);
        AtomicReference<byte[]> audio = new AtomicReference<>();
        AtomicReference<Throwable> failure = new AtomicReference<>();
        AtomicReference<WebSocket> socketRef = new AtomicReference<>();

        Request request = new Request.Builder().url(url).build();
        WebSocket socket = client.newWebSocket(request, new WebSocketListener() {
            @Override public void onOpen(WebSocket webSocket, Response response) {
                socketRef.set(webSocket);
                if (!webSocket.send(payload.toString())) {
                    failure.compareAndSet(null, new IllegalStateException("Local Qwen TTS WebSocket rejected the generation request."));
                    done.countDown();
                }
            }

            @Override public void onMessage(WebSocket webSocket, String message) {
                try {
                    JSONObject json = new JSONObject(message);
                    String type = json.optString("type", "");
                    if ("error".equals(type)) {
                        failure.compareAndSet(null, new IllegalStateException("Local Qwen TTS error: " + json.optString("message", "unknown error")));
                        done.countDown();
                    } else if ("complete".equals(type)) {
                        if (json.optBoolean("cancelled", false)) {
                            failure.compareAndSet(null, new IllegalStateException("Local Qwen TTS generation was cancelled."));
                        }
                        done.countDown();
                        webSocket.close(1000, "generation complete");
                    }
                } catch (Exception e) {
                    failure.compareAndSet(null, e);
                    done.countDown();
                }
            }

            @Override public void onMessage(WebSocket webSocket, ByteString bytes) {
                try {
                    ParsedAudioChunk parsed = parseAudioChunk(bytes.toByteArray());
                    // textParts contains exactly one item, so receiving more than one audio message would
                    // mean a protocol/server mismatch. Keep the first complete WAV and flag duplicates.
                    if (!audio.compareAndSet(null, parsed.wav)) {
                        failure.compareAndSet(null, new IllegalStateException("Local Qwen TTS returned multiple WAV chunks for a single explicit textPart."));
                        done.countDown();
                    }
                } catch (Throwable e) {
                    failure.compareAndSet(null, e);
                    done.countDown();
                }
            }

            @Override public void onClosing(WebSocket webSocket, int code, String reason) {
                webSocket.close(code, reason);
            }

            @Override public void onClosed(WebSocket webSocket, int code, String reason) {
                if (audio.get() == null && failure.get() == null) {
                    failure.compareAndSet(null, new IllegalStateException("Local Qwen TTS WebSocket closed before audio arrived: " + code + " " + reason));
                }
                done.countDown();
            }

            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {
                failure.compareAndSet(null, t);
                done.countDown();
            }
        });
        socketRef.set(socket);

        boolean completed = done.await(Math.max(30, settings.localTtsTimeoutSeconds()), TimeUnit.SECONDS);
        if (!completed) {
            try { socket.cancel(); } catch (Exception ignored) {}
            throw new IllegalStateException("Local Qwen TTS timed out after " + settings.localTtsTimeoutSeconds() + " seconds at " + url);
        }
        if (failure.get() != null) throw new IllegalStateException("Local Qwen TTS request failed at " + url, failure.get());
        byte[] wav = audio.get();
        if (wav == null || wav.length == 0) throw new IllegalStateException("Local Qwen TTS completed without returning WAV audio.");
        return new TtsAudioResult(wav, "audio/wav");
    }

    private static ParsedAudioChunk parseAudioChunk(byte[] message) {
        if (message == null || message.length < 24) throw new IllegalArgumentException("Local Qwen TTS binary frame is too short.");
        ByteBuffer b = ByteBuffer.wrap(message).order(ByteOrder.LITTLE_ENDIAN);
        int chunkIndex = b.getInt();
        int totalChunks = b.getInt();
        String text = readString(b, "text");
        String starId = readString(b, "starId");
        String voiceId = readString(b, "voiceId");
        byte[] wav = new byte[b.remaining()];
        b.get(wav);
        if (wav.length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') {
            throw new IllegalArgumentException("Local Qwen TTS binary frame did not contain RIFF/WAV audio.");
        }
        return new ParsedAudioChunk(chunkIndex, totalChunks, text, starId, voiceId, wav);
    }

    private static String readString(ByteBuffer b, String name) {
        if (b.remaining() < 4) throw new IllegalArgumentException("Missing " + name + " length in local TTS frame.");
        int n = b.getInt();
        if (n < 0 || n > b.remaining()) throw new IllegalArgumentException("Invalid " + name + " length in local TTS frame: " + n);
        byte[] bytes = new byte[n];
        b.get(bytes);
        return new String(bytes, StandardCharsets.UTF_8);
    }

    private static final class ParsedAudioChunk {
        final int index;
        final int total;
        final String text;
        final String starId;
        final String voiceId;
        final byte[] wav;
        ParsedAudioChunk(int index, int total, String text, String starId, String voiceId, byte[] wav) {
            this.index=index; this.total=total; this.text=text; this.starId=starId; this.voiceId=voiceId; this.wav=wav;
        }
    }
}
