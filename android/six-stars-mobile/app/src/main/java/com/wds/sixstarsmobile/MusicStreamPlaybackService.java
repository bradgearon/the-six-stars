package com.wds.sixstarsmobile;

import java.io.IOException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.locks.ReentrantLock;

/** One continuous music AudioTrack, using speech's tested rate/jitter/refill buffering algorithm. */
final class MusicStreamPlaybackService implements AutoCloseable {
    interface Listener {
        void onStarted();
        void onBuffering(boolean buffering, long leadMs, long targetMs);
        void onFinished(boolean complete);
    }
    private final SmartPcmPlaybackWriter writer;
    private final CountDownLatch finished = new CountDownLatch(1);
    private final Thread worker;
    private volatile boolean cancelled, paused, complete;
    private volatile Throwable error;

    MusicStreamPlaybackService(ReentrantLock outputLock, SmartPcmPlaybackWriter.AdaptiveProfile profile,
                               long requestStarted, Listener listener) {
        writer = new SmartPcmPlaybackWriter(profile, new SmartPcmPlaybackWriter.Listener() {
            public void onDebug(String title, String details) { }
            public void onPlaybackStarted(long frame) { listener.onStarted(); }
            public void onLipSyncDataCommitted(SmartPcmPlaybackWriter.LipSyncCommit commit) { }
            public void onSpeechStateChanged(boolean speaking, long frame) { }
            public void onBufferingChanged(boolean buffering, long leadMs, long targetMs) { listener.onBuffering(buffering, leadMs, targetMs); }
            public void onCompleted(SmartPcmPlaybackWriter.Result result) { complete = result.completed; }
            public void onError(Throwable failure) { error = failure; }
        }, true);
        worker = new Thread(() -> {
            boolean locked = false;
            try {
                outputLock.lockInterruptibly(); locked = true;
                if (cancelled) return;
                if (paused) writer.pause();
                writer.start(requestStarted);
                writer.awaitCompletion();
            } catch (InterruptedException ignored) { cancelled = true; }
            finally {
                writer.close();
                if (locked) outputLock.unlock();
                finished.countDown();
                listener.onFinished(complete && !cancelled && error == null);
            }
        }, "resone-music-playback");
        worker.start();
    }
    void write(byte[] pcm, int rate, int channels) throws IOException {
        if (cancelled || isFinished()) throw new IOException("Music playback stopped.");
        writer.write(new SmartPcmPlaybackWriter.PcmChunk(pcm, rate, channels, 0, "", android.os.SystemClock.elapsedRealtime(), null));
    }
    void endInput() { writer.endInput(); }
    boolean isFinished() { return finished.getCount() == 0; }
    boolean isPaused() { return paused; }
    void pause() { paused = true; writer.pause(); }
    void resume() { paused = false; writer.resume(); }
    void awaitCompletion() throws Exception {
        finished.await();
        if (error != null) throw new IOException("Music audio output failed.", error);
        if (!complete || cancelled) throw new IOException("Music playback cancelled before completion.");
    }
    @Override public void close() { cancelled = true; writer.cancel(); worker.interrupt(); }
}
