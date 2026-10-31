package com.wds.sixstarsmobile;

import android.content.Context;
import android.os.SystemClock;
import org.json.JSONObject;
import java.io.*;
import java.net.*;

/** Owns the framed HTTP music stream, validation, cancellation, and delivery to the audio service. */
final class MusicStreamClient implements AutoCloseable {
    interface Status { void update(String message); }
    private final Context context;
    private final String bridge;
    private final PhoneVoicePlaybackService audio;
    private volatile HttpURLConnection connection;
    private volatile MusicStreamPlaybackService playback;
    private volatile boolean closed;
    MusicStreamClient(Context context, String bridge, PhoneVoicePlaybackService audio) {
        this.context = context.getApplicationContext(); this.bridge = bridge; this.audio = audio;
    }
    void run(JSONObject request, Status status) throws Exception {
        long requestedAt = SystemClock.elapsedRealtime();
        HttpURLConnection local = null;
        try {
            if (closed) throw new IOException("Music cancelled.");
            local = new LocalNetworkBindingService(context).openHttpConnection(new URL(bridge.replaceAll("/+$", "") + "/api/mobile/music/stream"));
            connection = local;
            if (closed) throw new IOException("Music cancelled.");
            local.setRequestMethod("POST"); local.setDoOutput(true);
            local.setConnectTimeout(15000); local.setReadTimeout(210000);
            local.setRequestProperty("Content-Type", "application/json"); local.setRequestProperty("Accept", "application/x-resone-pcm");
            try (OutputStream output = local.getOutputStream()) { output.write(request.toString().getBytes("UTF-8")); }
            int code = local.getResponseCode();
            if (code != 200) {
                String detail = "";
                try (InputStream in = local.getErrorStream()) { if (in != null) {
                    ByteArrayOutputStream out = new ByteArrayOutputStream(); byte[] block = new byte[1024]; int n;
                    while (out.size() < 8192 && (n = in.read(block, 0, Math.min(block.length, 8192 - out.size()))) > 0) out.write(block, 0, n);
                    detail = out.toString("UTF-8");
                } }
                throw new IOException("Music HTTP " + code + ": " + detail);
            }
            try (DataInputStream in = new DataInputStream(new BufferedInputStream(local.getInputStream(), 16384))) {
                MusicPcmStreamReader.read(in, () -> closed, new MusicPcmStreamReader.Sink() {
                    private boolean first = true;
                    public void begin(int rate, int channels, int frames) throws Exception {
                        playback = audio.openMusicStream(requestedAt, frames * 1000L / rate, status);
                        if (closed) { playback.close(); throw new IOException("Music cancelled."); }
                    }
                    public void chunk(byte[] pcm, int rate, int channels) throws Exception {
                        playback.write(pcm, rate, channels);
                        if (first) { first = false; status.update("First music chunk received; buffering…"); }
                    }
                    public void end() { playback.endInput(); }
                });
                playback.awaitCompletion();
            }
        } catch (Exception error) {
            if (playback != null) playback.close();
            throw error;
        } finally { if (local != null) local.disconnect(); connection = null; }
    }
    @Override public void close() {
        closed = true;
        MusicStreamPlaybackService session = playback; if (session != null) session.close();
        HttpURLConnection local = connection;
        if (local != null) new Thread(local::disconnect, "cancel-music-stream").start();
    }
}
