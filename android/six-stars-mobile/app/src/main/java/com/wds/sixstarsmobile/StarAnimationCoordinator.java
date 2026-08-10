package com.wds.sixstarsmobile;

import android.os.Handler;
import android.os.SystemClock;

/** Converts audio/speech lifecycle events into a single animation state stream. */
final class StarAnimationCoordinator {
    interface Listener { void onAnimationState(String state); }

    private final Handler mainHandler;
    private Listener listener;
    private int generation;
    private boolean voiceActive;
    private boolean pendingShirtOff;
    private String pendingShirtOffStarId = "";

    StarAnimationCoordinator(Handler mainHandler, Listener listener) {
        this.mainHandler = mainHandler;
        this.listener = listener;
    }

    void setListener(Listener listener) { this.listener = listener; }

    void set(String state) { if (listener != null) listener.onAnimationState(state == null || state.isEmpty() ? "idle" : state); }
    void listening() { set("listening"); }
    void thinking() { set("thinking"); }
    void idle() { if (!voiceActive) set("idle"); }

    int beginVoice(long durationMs, int[] speechSegmentsMs, long playbackElapsedMs) {
        voiceActive = true;
        int token = ++generation;
        long elapsedMs = Math.max(0L, playbackElapsedMs);
        if (speechSegmentsMs == null || speechSegmentsMs.length < 2) {
            set("talking");
            return token;
        }

        boolean speakingNow = false;
        for (int i = 0; i + 1 < speechSegmentsMs.length && i < 480; i += 2) {
            long startMs = Math.max(0L, speechSegmentsMs[i]);
            long segmentDurationMs = Math.max(60L, Math.min(4000L, speechSegmentsMs[i + 1]));
            long endMs = startMs + segmentDurationMs;
            if (endMs <= elapsedMs) continue;
            long startDelayMs = Math.max(0L, startMs - elapsedMs);
            long endDelayMs = Math.max(1L, endMs - elapsedMs);
            if (startDelayMs == 0L) speakingNow = true;
            else mainHandler.postDelayed(() -> { if (token == generation && voiceActive) set("talking"); }, startDelayMs);
            mainHandler.postDelayed(() -> { if (token == generation && voiceActive) set("idle"); }, endDelayMs);
        }
        set(speakingNow ? "talking" : "idle");
        return token;
    }

    void endVoice() {
        voiceActive = false;
        generation++;
        if (!pendingShirtOff) set("idle");
    }

    void cancel(String reason) { voiceActive = false; generation++; set("idle"); }

    void queueShirtOff(String starId) {
        pendingShirtOff = true;
        pendingShirtOffStarId = starId == null ? "" : starId;
    }

    boolean playPendingShirtOff(String selectedStarId) {
        if (!pendingShirtOff || voiceActive) return false;
        if (!pendingShirtOffStarId.isEmpty() && selectedStarId != null && !pendingShirtOffStarId.equalsIgnoreCase(selectedStarId)) return false;
        pendingShirtOff = false;
        set("shirt-off");
        int token = ++generation;
        mainHandler.postDelayed(() -> { if (token == generation) set("idle"); }, 2600L);
        return true;
    }

    boolean isVoiceActive() { return voiceActive; }
}
