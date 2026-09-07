package com.wds.sixstarsmobile;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.os.SystemClock;

import java.io.ByteArrayOutputStream;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingDeque;
import java.util.concurrent.TimeUnit;

/**
 * Self-contained streaming PCM writer for one logical Star response.
 *
 * Network code only feeds PCM and closes input. This class owns the adaptive startup reservoir,
 * AudioTrack writes, emergency pause/refill, forward-only RMS lip markers, and playback-head based
 * mouth state. Audio that has already been committed is never re-analysed or re-timed.
 */
final class SmartPcmPlaybackWriter implements AutoCloseable {
    private static final int DEFAULT_STARTUP_MS = 900;
    private static final int MIN_STARTUP_MS = 500;
    private static final int MAX_STARTUP_MS = 2200;
    private static final int MIN_IDLE_START_MS = 180;
    private static final int EMERGENCY_LEAD_MS = 180;
    private static final int MIN_REFILL_MS = 550;
    private static final int AUDIO_TRACK_CAPACITY_MS = 3200;
    private static final int MAX_COMMIT_BATCH_MS = 720;
    private static final int COMPLETION_TOLERANCE_MS = 20;
    private static final double RATE_ALPHA = 0.25;
    private static final double JITTER_ALPHA = 0.25;

    interface Listener {
        void onDebug(String title, String details);
        void onPlaybackStarted(long playbackFrame);
        void onLipSyncDataCommitted(LipSyncCommit commit);
        void onSpeechStateChanged(boolean speaking, long playbackFrame);
        void onBufferingChanged(boolean buffering, long leadMs, long targetMs);
        void onCompleted(Result result);
        void onError(Throwable error);
    }

    static final class PcmChunk {
        final byte[] pcm;
        final int sampleRate;
        final int channels;
        final int textChars;
        final String textKey;
        final long receivedAtElapsedMs;
        final Object tag;

        PcmChunk(byte[] pcm, int sampleRate, int channels, int textChars,
                 String textKey, long receivedAtElapsedMs, Object tag) {
            this.pcm = pcm == null ? new byte[0] : pcm;
            this.sampleRate = sampleRate;
            this.channels = Math.max(1, channels);
            this.textChars = Math.max(0, textChars);
            this.textKey = textKey == null ? "" : textKey;
            this.receivedAtElapsedMs = receivedAtElapsedMs > 0L
                    ? receivedAtElapsedMs : SystemClock.elapsedRealtime();
            this.tag = tag;
        }

        long frameCount() {
            return pcm.length / Math.max(1, channels * 2L);
        }

        long durationMs() {
            return frameCount() * 1000L / Math.max(1, sampleRate);
        }
    }

    static final class LipSyncCommit {
        final long startFrame;
        final long endFrame;
        final int sampleRate;
        final int[] absoluteSegmentsMs;
        final double peak;
        final double highThreshold;
        final double lowThreshold;

        LipSyncCommit(long startFrame, long endFrame, int sampleRate, int[] absoluteSegmentsMs,
                      double peak, double highThreshold, double lowThreshold) {
            this.startFrame = startFrame;
            this.endFrame = endFrame;
            this.sampleRate = sampleRate;
            this.absoluteSegmentsMs = absoluteSegmentsMs == null ? new int[0] : absoluteSegmentsMs;
            this.peak = peak;
            this.highThreshold = highThreshold;
            this.lowThreshold = lowThreshold;
        }
    }

    static final class Result {
        final boolean completed;
        final double generationRate;
        final int underruns;
        final int emergencyRefills;
        final long learnedStartupMs;
        final long committedFrames;

        Result(boolean completed, double generationRate, int underruns, int emergencyRefills,
               long learnedStartupMs, long committedFrames) {
            this.completed = completed;
            this.generationRate = generationRate;
            this.underruns = underruns;
            this.emergencyRefills = emergencyRefills;
            this.learnedStartupMs = learnedStartupMs;
            this.committedFrames = committedFrames;
        }
    }

    /** Shared across logical responses so successful/rough streams tune the next startup lead. */
    static final class AdaptiveProfile {
        private double learnedStartupMs = DEFAULT_STARTUP_MS;
        private double learnedMsPerChar = 68.0;

        synchronized double startupMs() { return learnedStartupMs; }
        synchronized double msPerChar() { return learnedMsPerChar; }

        synchronized void increaseStartup(double requestedMs) {
            learnedStartupMs = Math.min(MAX_STARTUP_MS, Math.max(learnedStartupMs, requestedMs));
        }

        synchronized void decreaseStartup(double amountMs) {
            learnedStartupMs = Math.max(MIN_STARTUP_MS, learnedStartupMs - Math.max(0.0, amountMs));
        }

        synchronized void observeMsPerChar(double sample) {
            double bounded = Math.max(35.0, Math.min(130.0, sample));
            learnedMsPerChar = learnedMsPerChar * 0.85 + bounded * 0.15;
        }
    }

    private static final Listener NO_OP = new Listener() {
        @Override public void onDebug(String title, String details) { }
        @Override public void onPlaybackStarted(long playbackFrame) { }
        @Override public void onLipSyncDataCommitted(LipSyncCommit commit) { }
        @Override public void onSpeechStateChanged(boolean speaking, long playbackFrame) { }
        @Override public void onBufferingChanged(boolean buffering, long leadMs, long targetMs) { }
        @Override public void onCompleted(Result result) { }
        @Override public void onError(Throwable error) { }
    };

    private final AdaptiveProfile profile;
    private final Listener listener;
    private final LinkedBlockingDeque<PcmChunk> input = new LinkedBlockingDeque<>();
    private final CountDownLatch finished = new CountDownLatch(1);
    private final Object pauseGate = new Object();
    private final Object lipGate = new Object();
    private final ArrayDeque<LipSegment> lipTimeline = new ArrayDeque<>();

    private volatile boolean inputEnded;
    private volatile boolean cancelled;
    private volatile boolean externallyPaused;
    private volatile boolean buffering;
    private volatile boolean workerStarted;
    private volatile boolean observerRunning;
    private volatile AudioTrack track;
    private volatile long committedFrames;
    private volatile long lastInputArrivalElapsedMs;
    private volatile long startRequestedElapsedMs;
    private volatile long firstInputObservedElapsedMs;
    private volatile long firstInputAudioMs;
    private volatile long firstUnderlyingWriteElapsedMs;
    private volatile long firstPlaybackFrameElapsedMs;
    private volatile boolean speechState;
    private Thread workerThread;
    private Thread observerThread;

    SmartPcmPlaybackWriter(AdaptiveProfile profile, Listener listener) {
        this.profile = profile == null ? new AdaptiveProfile() : profile;
        this.listener = listener == null ? NO_OP : listener;
    }

    void start() {
        if (workerStarted) return;
        workerStarted = true;
        startRequestedElapsedMs = SystemClock.elapsedRealtime();
        workerThread = new Thread(this::runWorker, "six-stars-smart-pcm-writer");
        workerThread.start();
    }

    void write(PcmChunk chunk) {
        if (chunk == null || chunk.pcm.length == 0 || cancelled || inputEnded) return;

        // This is the production-speed signal that is available before a second packet exists:
        // how long this writer actually waited after start() for playable PCM to be handed to it.
        if (firstInputObservedElapsedMs <= 0L) {
            synchronized (this) {
                if (firstInputObservedElapsedMs <= 0L) {
                    firstInputObservedElapsedMs = SystemClock.elapsedRealtime();
                    firstInputAudioMs = Math.max(1L, chunk.durationMs());
                }
            }
        }

        lastInputArrivalElapsedMs = Math.max(lastInputArrivalElapsedMs, chunk.receivedAtElapsedMs);
        input.offerLast(chunk);
    }

    void endInput() {
        inputEnded = true;
        synchronized (pauseGate) { pauseGate.notifyAll(); }
    }

    void pause() {
        externallyPaused = true;
        AudioTrack local = track;
        if (local != null) try { local.pause(); } catch (Throwable ignored) { }
    }

    void resume() {
        externallyPaused = false;
        synchronized (pauseGate) { pauseGate.notifyAll(); }
        AudioTrack local = track;
        if (local != null && !buffering) try { local.play(); } catch (Throwable ignored) { }
    }

    void cancel() {
        cancelled = true;
        input.clear();
        synchronized (pauseGate) { pauseGate.notifyAll(); }
        stopTrack(track);
        Thread worker = workerThread;
        if (worker != null) worker.interrupt();
        Thread observer = observerThread;
        if (observer != null) observer.interrupt();
    }

    boolean isFinished() { return finished.getCount() == 0L; }

    void awaitCompletion() throws InterruptedException { finished.await(); }

    @Override public void close() { cancel(); }

    private void runWorker() {
        AdaptiveStats stats = new AdaptiveStats();
        AudioTrack localTrack = null;
        int initialUnderruns = 0;
        int lastUnderruns = 0;
        int emergencyRefills = 0;
        boolean completed = false;
        String startupReason = "cancelled";
        long startupAudioMs = 0L;
        long startupTargetMs = Math.round(profile.startupMs());

        try {
            ArrayList<PcmChunk> startup = new ArrayList<>();
            while (!cancelled) {
                PcmChunk chunk = input.poll(50L, TimeUnit.MILLISECONDS);
                if (chunk != null) {
                    validateFirstOrFormat(startup, chunk);
                    stats.observe(chunk, profile);
                    startup.add(chunk);
                    startupAudioMs += Math.max(1L, chunk.durationMs());
                    startupTargetMs = calculateTargetMs(stats);
                }

                if (startupAudioMs >= startupTargetMs) {
                    startupReason = "target";
                    break;
                }
                if (inputEnded && input.isEmpty() && !startup.isEmpty()) {
                    // The total response is smaller than the preferred startup window. End-of-input
                    // is authoritative: play every byte we actually have rather than waiting for an
                    // impossible target size.
                    startupReason = "input-ended-short";
                    break;
                }
                if (!startup.isEmpty() && startupAudioMs >= MIN_IDLE_START_MS
                        && producerIdleMs() >= calculateProducerIdleReleaseMs(stats)) {
                    // Self-contained short-phrase release. This also handles a small first TTS phrase
                    // when the next phrase is intentionally delayed: start what is safe now, and let
                    // emergency refill pause the same AudioTrack later if generation really stalls.
                    startupReason = "producer-idle";
                    break;
                }
            }

            if (cancelled || startup.isEmpty()) return;

            PcmChunk first = startup.get(0);
            int sampleRate = first.sampleRate;
            int channels = first.channels;
            localTrack = createTrack(sampleRate, channels);
            track = localTrack;

            commitBatch(localTrack, startup, stats);
            initialUnderruns = localTrack.getUnderrunCount();
            lastUnderruns = initialUnderruns;

            if (!externallyPaused) localTrack.play();
            startPlaybackObserver(localTrack, sampleRate);

            final long startupForLog = startupAudioMs;
            final long targetForLog = startupTargetMs;
            final String reasonForLog = startupReason;
            listener.onDebug("Smart PCM writer started",
                    "reason=" + reasonForLog
                            + ", startup=" + startupForLog + "ms"
                            + ", target=" + targetForLog + "ms"
                            + ", rate=" + formatRate(effectiveGenerationRate(stats)) + "x"
                            + ", packetRate=" + formatRate(stats.rate) + "x"
                            + ", firstChunkWait=" + firstChunkWaitMs() + "ms"
                            + ", firstChunkRate=" + formatRate(firstChunkGenerationRate()) + "x"
                            + ", jitter=" + Math.round(stats.jitterMs) + "ms"
                            + ", start->firstWrite=" + Math.max(0L, firstUnderlyingWriteElapsedMs - startRequestedElapsedMs) + "ms.");

            while (!cancelled) {
                waitWhileExternallyPaused(localTrack);

                PcmChunk firstNew = input.poll(24L, TimeUnit.MILLISECONDS);
                if (firstNew != null) {
                    ArrayList<PcmChunk> batch = drainCommitBatch(firstNew, stats, sampleRate, channels);
                    commitBatch(localTrack, batch, stats);
                }

                long playedFrames = playbackHead(localTrack);
                long leadMs = framesToMs(Math.max(0L, committedFrames - playedFrames), sampleRate);
                long liveTargetMs = calculateTargetMs(stats);
                long lowWaterMs = Math.max(EMERGENCY_LEAD_MS, Math.min(650L, Math.round(liveTargetMs * 0.30)));

                int underruns = localTrack.getUnderrunCount();
                if (underruns > lastUnderruns) {
                    profile.increaseStartup(Math.max(profile.startupMs() + 250.0, liveTargetMs));
                    lastUnderruns = underruns;
                }

                if (!inputEnded && input.isEmpty() && leadMs < lowWaterMs && committedFrames > playedFrames
                        && producerIdleMs() < calculateProducerIdleReleaseMs(stats)) {
                    emergencyRefills++;
                    profile.increaseStartup(Math.max(profile.startupMs() + 180.0, liveTargetMs));
                    refill(localTrack, stats, sampleRate, channels, liveTargetMs, leadMs);
                }

                if (inputEnded && input.isEmpty()) {
                    long head = playbackHead(localTrack);
                    long toleranceFrames = msToFrames(COMPLETION_TOLERANCE_MS, sampleRate);
                    if (head + toleranceFrames >= committedFrames) {
                        completed = true;
                        break;
                    }
                }
            }

            stats.finishTextEstimate(profile);
            double completedGenerationRate = effectiveGenerationRate(stats);
            if (completed && lastUnderruns == initialUnderruns && emergencyRefills == 0
                    && completedGenerationRate > 1.45) {
                profile.decreaseStartup(40.0);
            }
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        } catch (Throwable error) {
            if (!cancelled) listener.onError(error);
        } finally {
            observerRunning = false;
            Thread observer = observerThread;
            if (observer != null) observer.interrupt();

            if (speechState) {
                speechState = false;
                listener.onSpeechStateChanged(false, track == null ? 0L : playbackHead(track));
            }

            stopTrack(localTrack);
            if (localTrack != null) try { localTrack.release(); } catch (Throwable ignored) { }
            track = null;

            Result result = new Result(
                    completed,
                    effectiveGenerationRate(stats),
                    Math.max(0, lastUnderruns - initialUnderruns),
                    emergencyRefills,
                    Math.round(profile.startupMs()),
                    committedFrames);
            listener.onCompleted(result);
            finished.countDown();
        }
    }

    private void refill(AudioTrack localTrack, AdaptiveStats stats, int sampleRate, int channels,
                        long liveTargetMs, long leadBeforeMs) throws InterruptedException {
        buffering = true;
        try { localTrack.pause(); } catch (Throwable ignored) { }
        listener.onBufferingChanged(true, leadBeforeMs, liveTargetMs);

        long refillTargetMs = Math.max(MIN_REFILL_MS, Math.min(1400L, Math.round(liveTargetMs * 0.70)));
        long refillStartedAt = SystemClock.elapsedRealtime();

        while (!cancelled && !externallyPaused) {
            long head = playbackHead(localTrack);
            long leadMs = framesToMs(Math.max(0L, committedFrames - head), sampleRate);
            if (leadMs >= refillTargetMs) break;
            if (inputEnded && input.isEmpty()) break;

            PcmChunk chunk = input.poll(50L, TimeUnit.MILLISECONDS);
            if (chunk != null) {
                ArrayList<PcmChunk> batch = drainCommitBatch(chunk, stats, sampleRate, channels);
                commitBatch(localTrack, batch, stats);
                continue;
            }

            // Do not strand the tail of a short phrase just because the next producer packet is
            // delayed. If the producer has gone idle and we still have audible lead, release the
            // existing AudioTrack buffer and let it play; a later packet can be written to the same
            // stream and another emergency refill can occur if needed.
            if (leadMs > 0L && producerIdleMs() >= calculateProducerIdleReleaseMs(stats)) break;
        }

        buffering = false;
        long leadAfterMs = framesToMs(Math.max(0L, committedFrames - playbackHead(localTrack)), sampleRate);
        if (!cancelled && !externallyPaused) try { localTrack.play(); } catch (Throwable ignored) { }
        listener.onBufferingChanged(false, leadAfterMs, liveTargetMs);
        listener.onDebug("Smart PCM refill",
                "lead=" + leadBeforeMs + "ms -> " + leadAfterMs + "ms"
                        + ", target=" + refillTargetMs + "ms"
                        + ", waited=" + Math.max(0L, SystemClock.elapsedRealtime() - refillStartedAt) + "ms.");
    }

    private ArrayList<PcmChunk> drainCommitBatch(PcmChunk first, AdaptiveStats stats,
                                                  int sampleRate, int channels) {
        ArrayList<PcmChunk> batch = new ArrayList<>();
        addValidatedChunk(batch, first, stats, sampleRate, channels);
        long batchMs = Math.max(1L, first.durationMs());

        while (batchMs < MAX_COMMIT_BATCH_MS) {
            PcmChunk next = input.pollFirst();
            if (next == null) break;
            long nextMs = Math.max(1L, next.durationMs());
            if (batchMs + nextMs > MAX_COMMIT_BATCH_MS && !batch.isEmpty()) {
                input.offerFirst(next);
                break;
            }
            addValidatedChunk(batch, next, stats, sampleRate, channels);
            batchMs += nextMs;
        }
        return batch;
    }

    private void addValidatedChunk(ArrayList<PcmChunk> batch, PcmChunk chunk, AdaptiveStats stats,
                                   int sampleRate, int channels) {
        validateFormat(chunk, sampleRate, channels);
        stats.observe(chunk, profile);
        batch.add(chunk);
    }

    private void commitBatch(AudioTrack localTrack, List<PcmChunk> chunks, AdaptiveStats stats) {
        if (chunks == null || chunks.isEmpty()) return;
        PcmChunk first = chunks.get(0);
        int sampleRate = first.sampleRate;
        int channels = first.channels;

        ByteArrayOutputStream pcm = new ByteArrayOutputStream();
        for (PcmChunk chunk : chunks) {
            validateFormat(chunk, sampleRate, channels);
            pcm.write(chunk.pcm, 0, chunk.pcm.length);
        }
        byte[] bytes = pcm.toByteArray();
        if (bytes.length == 0) return;

        long startFrame = committedFrames;
        long frames = bytes.length / Math.max(1, channels * 2L);
        long endFrame = startFrame + frames;

        PcmSpeechActivityAnalyzer.Analysis analysis = PcmSpeechActivityAnalyzer.analyzePcm16(bytes, sampleRate, channels);
        int[] absoluteSegments = appendLipData(startFrame, endFrame, sampleRate, analysis);

        if (firstUnderlyingWriteElapsedMs <= 0L) firstUnderlyingWriteElapsedMs = SystemClock.elapsedRealtime();
        int offset = 0;
        while (offset < bytes.length && !cancelled) {
            int written = localTrack.write(bytes, offset, bytes.length - offset, AudioTrack.WRITE_BLOCKING);
            if (written <= 0) throw new IllegalStateException("AudioTrack rejected PCM write: " + written);
            offset += written;
        }
        if (cancelled) return;

        committedFrames = endFrame;
        listener.onLipSyncDataCommitted(new LipSyncCommit(
                startFrame, endFrame, sampleRate, absoluteSegments,
                analysis.peak, analysis.highThreshold, analysis.lowThreshold));
    }

    private int[] appendLipData(long batchStartFrame, long batchEndFrame, int sampleRate,
                                PcmSpeechActivityAnalyzer.Analysis analysis) {
        int[] local = analysis == null ? new int[0] : analysis.segmentsMs;

        if (local.length < 2) {
            // Empty RMS markers on a genuinely quiet tail mean mouth closed. The old fallback made
            // every such trailing transport block count as speech and kept talking.gif alive after
            // the audible phoneme had ended. Retain the whole-block fallback only for a clearly
            // audible block whose threshold envelope was too unstable to form a segment.
            double peak = analysis == null ? 0.0 : analysis.peak;
            if (peak < 0.008) return new int[0];

            long startMs = framesToMs(batchStartFrame, sampleRate);
            long durationMs = Math.max(1L, framesToMs(batchEndFrame - batchStartFrame, sampleRate));
            synchronized (lipGate) {
                lipTimeline.addLast(new LipSegment(batchStartFrame, batchEndFrame));
            }
            return new int[] { clampInt(startMs), clampInt(durationMs) };
        }

        ArrayList<Integer> absolute = new ArrayList<>(local.length);
        synchronized (lipGate) {
            for (int i = 0; i + 1 < local.length; i += 2) {
                long localStartMs = Math.max(0L, local[i]);
                long durationMs = Math.max(1L, local[i + 1]);
                long segmentStartFrame = Math.min(
                        batchEndFrame,
                        batchStartFrame + msToFrames(localStartMs, sampleRate));
                long requestedEndFrame = segmentStartFrame + msToFrames(durationMs, sampleRate);
                long segmentEndFrame = Math.min(batchEndFrame, requestedEndFrame);
                if (segmentEndFrame <= segmentStartFrame) continue;

                // A live marker cannot claim frames that are outside the PCM block that produced it.
                lipTimeline.addLast(new LipSegment(segmentStartFrame, segmentEndFrame));
                absolute.add(clampInt(framesToMs(segmentStartFrame, sampleRate)));
                absolute.add(clampInt(Math.max(
                        1L, framesToMs(segmentEndFrame - segmentStartFrame, sampleRate))));
            }
        }

        int[] result = new int[absolute.size()];
        for (int i = 0; i < absolute.size(); i++) result[i] = absolute.get(i);
        return result;
    }

    private void startPlaybackObserver(AudioTrack localTrack, int sampleRate) {
        observerRunning = true;
        observerThread = new Thread(() -> {
            boolean playbackStartedSent = false;
            while (observerRunning && !cancelled) {
                try {
                    long head = playbackHead(localTrack);
                    if (!playbackStartedSent && head > 0L) {
                        playbackStartedSent = true;
                        firstPlaybackFrameElapsedMs = SystemClock.elapsedRealtime();
                        listener.onPlaybackStarted(head);
                        listener.onDebug("Smart PCM playback began",
                                "firstPlaybackFrame=" + head
                                        + ", firstWrite->speaker=" + Math.max(0L, firstPlaybackFrameElapsedMs - firstUnderlyingWriteElapsedMs) + "ms.");
                    }

                    boolean speaking = false;
                    if (playbackStartedSent && !buffering && !externallyPaused) {
                        speaking = isSpeakingAt(head);
                    }
                    if (speaking != speechState) {
                        speechState = speaking;
                        listener.onSpeechStateChanged(speaking, head);
                    }
                    Thread.sleep(8L);
                } catch (InterruptedException ignored) {
                    break;
                } catch (Throwable ignored) {
                    try { Thread.sleep(16L); } catch (InterruptedException stop) { break; }
                }
            }
        }, "six-stars-smart-pcm-playback-observer");
        observerThread.start();
    }

    private boolean isSpeakingAt(long playbackFrame) {
        synchronized (lipGate) {
            while (!lipTimeline.isEmpty() && lipTimeline.peekFirst().endFrame <= playbackFrame) {
                lipTimeline.removeFirst();
            }
            for (LipSegment segment : lipTimeline) {
                if (segment.startFrame > playbackFrame) break;
                if (playbackFrame >= segment.startFrame && playbackFrame < segment.endFrame) return true;
            }
            return false;
        }
    }

    private void waitWhileExternallyPaused(AudioTrack localTrack) throws InterruptedException {
        while (!cancelled && externallyPaused) {
            try { if (localTrack.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) localTrack.pause(); } catch (Throwable ignored) { }
            synchronized (pauseGate) {
                if (externallyPaused && !cancelled) pauseGate.wait(250L);
            }
        }
        if (!cancelled && !buffering) {
            try { if (localTrack.getPlayState() == AudioTrack.PLAYSTATE_PAUSED) localTrack.play(); } catch (Throwable ignored) { }
        }
    }

    private long calculateTargetMs(AdaptiveStats stats) {
        double rate = effectiveGenerationRate(stats);
        if (rate <= 0.0) {
            return Math.round(Math.max(MIN_STARTUP_MS, Math.min(MAX_STARTUP_MS, profile.startupMs())));
        }

        double rateReserve;
        if (rate >= 2.5) rateReserve = 0.0;
        else if (rate >= 1.8) rateReserve = 100.0;
        else if (rate >= 1.4) rateReserve = 260.0;
        else if (rate >= 1.15) rateReserve = 460.0;
        else if (rate >= 1.0) rateReserve = 720.0;
        else rateReserve = 950.0 + Math.min(500.0, (1.0 - rate) * 1400.0);

        double jitterReserve = Math.min(550.0,
                stats.jitterMs * 2.2 + Math.max(0.0, stats.maxGapMs - stats.averageGapMs) * 0.55);
        double predictedLead = 0.0;
        if (rate < 1.0) {
            double remainingAudioMs = stats.predictedRemainingAudioMs(profile.msPerChar());
            predictedLead = Math.min(900.0, remainingAudioMs * (1.0 - rate));
        }

        double dynamic = MIN_STARTUP_MS + rateReserve + jitterReserve + predictedLead;
        return Math.round(Math.max(MIN_STARTUP_MS,
                Math.min(MAX_STARTUP_MS, Math.max(profile.startupMs(), dynamic))));
    }

    private long firstChunkWaitMs() {
        long start = startRequestedElapsedMs;
        long first = firstInputObservedElapsedMs;
        if (start <= 0L || first <= 0L) return 0L;
        return Math.max(1L, first - start);
    }

    private double firstChunkGenerationRate() {
        long waitMs = firstChunkWaitMs();
        long audioMs = firstInputAudioMs;
        if (waitMs <= 0L || audioMs <= 0L) return 0.0;
        return Math.max(0.05, Math.min(12.0, audioMs / (double) waitMs));
    }

    private double effectiveGenerationRate(AdaptiveStats stats) {
        double firstRate = firstChunkGenerationRate();
        double sustainedRate = stats == null ? 0.0 : stats.rate;

        if (sustainedRate <= 0.0) return firstRate;
        if (firstRate <= 0.0) return sustainedRate;

        // Fade the cold-start estimate out quickly once packet-to-packet production timing exists.
        int chunks = stats == null ? 0 : stats.observedChunks;
        if (chunks <= 2) return sustainedRate * 0.65 + firstRate * 0.35;
        if (chunks == 3) return sustainedRate * 0.85 + firstRate * 0.15;
        return sustainedRate;
    }

    private long calculateProducerIdleReleaseMs(AdaptiveStats stats) {
        if (stats.averageGapMs <= 0.0) return 320L;
        double adaptive = stats.averageGapMs * 2.2 + stats.jitterMs * 2.0 + 80.0;
        return Math.round(Math.max(220.0, Math.min(650.0, adaptive)));
    }

    private long producerIdleMs() {
        long last = lastInputArrivalElapsedMs;
        if (last <= 0L) return 0L;
        return Math.max(0L, SystemClock.elapsedRealtime() - last);
    }

    private AudioTrack createTrack(int sampleRate, int channels) {
        int channelConfig = channels == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
        int minBuffer = AudioTrack.getMinBufferSize(sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT);
        if (minBuffer <= 0) minBuffer = bytesForMs(sampleRate, channels, 400);
        int bufferSize = Math.max(minBuffer, bytesForMs(sampleRate, channels, AUDIO_TRACK_CAPACITY_MS));
        return new AudioTrack.Builder()
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(sampleRate)
                        .setChannelMask(channelConfig)
                        .build())
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setBufferSizeInBytes(bufferSize)
                .build();
    }

    private void validateFirstOrFormat(ArrayList<PcmChunk> startup, PcmChunk chunk) {
        if (startup.isEmpty()) return;
        PcmChunk first = startup.get(0);
        validateFormat(chunk, first.sampleRate, first.channels);
    }

    private void validateFormat(PcmChunk chunk, int sampleRate, int channels) {
        if (chunk.sampleRate != sampleRate || chunk.channels != channels) {
            throw new IllegalStateException("PCM stream format changed mid-response: "
                    + chunk.sampleRate + "Hz/" + chunk.channels + "ch after "
                    + sampleRate + "Hz/" + channels + "ch.");
        }
    }

    private static long playbackHead(AudioTrack track) {
        if (track == null) return 0L;
        try { return Integer.toUnsignedLong(track.getPlaybackHeadPosition()); }
        catch (Throwable ignored) { return 0L; }
    }

    private static int bytesForMs(int sampleRate, int channels, int milliseconds) {
        long bytes = (long) sampleRate * Math.max(1, channels) * 2L * Math.max(1, milliseconds) / 1000L;
        bytes &= ~1L;
        return (int) Math.min(Integer.MAX_VALUE, Math.max(2048L, bytes));
    }

    private static long msToFrames(long ms, int sampleRate) {
        return Math.max(0L, ms) * Math.max(1, sampleRate) / 1000L;
    }

    private static long framesToMs(long frames, int sampleRate) {
        return Math.max(0L, frames) * 1000L / Math.max(1, sampleRate);
    }

    private static int clampInt(long value) {
        return (int) Math.max(0L, Math.min(Integer.MAX_VALUE, value));
    }

    private static String formatRate(double value) {
        return String.format(Locale.US, "%.2f", Math.max(0.0, value));
    }

    private static void stopTrack(AudioTrack track) {
        if (track == null) return;
        try { track.pause(); } catch (Throwable ignored) { }
        try { track.flush(); } catch (Throwable ignored) { }
        try { track.stop(); } catch (Throwable ignored) { }
    }

    private static final class LipSegment {
        final long startFrame;
        final long endFrame;
        LipSegment(long startFrame, long endFrame) {
            this.startFrame = Math.max(0L, startFrame);
            this.endFrame = Math.max(this.startFrame + 1L, endFrame);
        }
    }

    private static final class AdaptiveStats {
        long lastArrivalMs;
        int observedChunks;
        double rate;
        double averageGapMs;
        double jitterMs;
        double maxGapMs;
        String textKey = "";
        int textChars;
        long textAudioMs;

        void observe(PcmChunk chunk, AdaptiveProfile profile) {
            observedChunks++;
            long nowMs = chunk.receivedAtElapsedMs;
            long durationMs = Math.max(1L, chunk.durationMs());
            if (lastArrivalMs > 0L) {
                double gap = Math.max(1.0, nowMs - lastArrivalMs);
                double instantRate = Math.max(0.05, Math.min(12.0, durationMs / gap));
                rate = rate <= 0.0 ? instantRate : rate * (1.0 - RATE_ALPHA) + instantRate * RATE_ALPHA;
                if (averageGapMs <= 0.0) averageGapMs = gap;
                double deviation = Math.abs(gap - averageGapMs);
                averageGapMs = averageGapMs * (1.0 - JITTER_ALPHA) + gap * JITTER_ALPHA;
                jitterMs = jitterMs * (1.0 - JITTER_ALPHA) + deviation * JITTER_ALPHA;
                maxGapMs = Math.max(maxGapMs * 0.94, gap);
            }
            lastArrivalMs = nowMs;

            String incomingKey = chunk.textKey;
            if (!incomingKey.equals(textKey)) {
                finishTextEstimate(profile);
                textKey = incomingKey;
                textChars = chunk.textChars;
                textAudioMs = 0L;
            }
            textAudioMs += durationMs;
        }

        void finishTextEstimate(AdaptiveProfile profile) {
            if (textChars >= 12 && textAudioMs >= 400L) {
                profile.observeMsPerChar(textAudioMs / (double) textChars);
            }
            textKey = "";
            textChars = 0;
            textAudioMs = 0L;
        }

        double predictedRemainingAudioMs(double msPerChar) {
            if (textChars <= 0) return 0.0;
            return Math.max(0.0, textChars * msPerChar - textAudioMs);
        }
    }
}
