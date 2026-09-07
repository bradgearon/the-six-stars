package com.wds.sixstarsstandalone;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTimestamp;
import android.media.AudioTrack;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.concurrent.LinkedBlockingDeque;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Queue-backed WAV player using the same generate-ahead/playback-behind and speech-activity
 * animation philosophy as the production thin client.
 */
final class AudioPlaybackService implements AutoCloseable {
    interface Listener {
        void onPlaybackStarted(String starId, String label, long durationMs, long playbackElapsedMs, int[] speechActivitySegmentsMs);
        void onPlaybackFinished(String starId, String label, boolean completedNormally);
        void onPlaybackError(String starId, String label, Throwable error);
        void onQueueIdle();
    }

    private static final Listener NO_OP = new Listener() {
        @Override public void onPlaybackStarted(String starId, String label, long durationMs, long playbackElapsedMs, int[] speechActivitySegmentsMs) { }
        @Override public void onPlaybackFinished(String starId, String label, boolean completedNormally) { }
        @Override public void onPlaybackError(String starId, String label, Throwable error) { }
        @Override public void onQueueIdle() { }
    };

    private static final class QueuedAudio {
        final TtsAudioResult audio;
        final String starId;
        final String label;
        final WavSpeechActivityAnalyzer.Analysis activity;

        QueuedAudio(TtsAudioResult audio, String starId, String label, WavSpeechActivityAnalyzer.Analysis activity) {
            this.audio = audio;
            this.starId = starId == null || starId.trim().isEmpty() ? "Star" : starId.trim();
            this.label = label == null ? "Star voice" : label;
            this.activity = activity == null ? WavSpeechActivityAnalyzer.Analysis.EMPTY : activity;
        }
    }

    private static final class ParsedWav {
        final int sampleRate;
        final int channels;
        final byte[] pcm;

        ParsedWav(int sampleRate, int channels, byte[] pcm) {
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.pcm = pcm;
        }

        static ParsedWav parse(byte[] wav) {
            if (wav == null || wav.length == 0) throw new IllegalArgumentException("TTS WAV is empty.");
            if (wav.length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') {
                return new ParsedWav(24000, 1, wav);
            }
            int offset = 12;
            int sampleRate = 24000;
            int channels = 1;
            byte[] data = null;
            while (offset + 8 <= wav.length) {
                String id = new String(wav, offset, 4, StandardCharsets.US_ASCII);
                int size = ByteBuffer.wrap(wav, offset + 4, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
                offset += 8;
                if (size < 0 || offset + size > wav.length) break;
                if ("fmt ".equals(id) && size >= 16) {
                    ByteBuffer fmt = ByteBuffer.wrap(wav, offset, size).order(ByteOrder.LITTLE_ENDIAN);
                    int audioFormat = fmt.getShort() & 0xffff;
                    channels = fmt.getShort() & 0xffff;
                    sampleRate = fmt.getInt();
                    fmt.getInt();
                    fmt.getShort();
                    int bits = fmt.getShort() & 0xffff;
                    if (audioFormat != 1 || bits != 16) throw new IllegalArgumentException("Only PCM16 WAV playback is supported.");
                } else if ("data".equals(id)) {
                    data = Arrays.copyOfRange(wav, offset, offset + size);
                }
                offset += size + (size % 2);
            }
            if (data == null) data = Arrays.copyOfRange(wav, Math.min(44, wav.length), wav.length);
            return new ParsedWav(Math.max(8000, sampleRate), Math.max(1, channels), data);
        }
    }

    private final LinkedBlockingDeque<QueuedAudio> queue = new LinkedBlockingDeque<>();
    private final AtomicInteger queuedCount = new AtomicInteger();
    private volatile Listener listener = NO_OP;
    private volatile boolean running = true;
    private volatile AudioTrack activeTrack;
    private final Thread playbackThread;

    AudioPlaybackService(android.content.Context ignoredContext) {
        playbackThread = new Thread(this::playbackLoop, "six-stars-standalone-wav-player");
        playbackThread.start();
    }

    void setListener(Listener value) { listener = value == null ? NO_OP : value; }

    WavSpeechActivityAnalyzer.Analysis enqueue(TtsAudioResult audio, String starId, String label) {
        if (!running || audio == null || audio.bytes == null || audio.bytes.length == 0) return WavSpeechActivityAnalyzer.Analysis.EMPTY;
        WavSpeechActivityAnalyzer.Analysis analysis = WavSpeechActivityAnalyzer.analyze(audio.bytes);
        queuedCount.incrementAndGet();
        queue.offerLast(new QueuedAudio(audio, starId, label, analysis));
        return analysis;
    }

    int queuedCount() { return Math.max(0, queuedCount.get()); }
    boolean isIdle() { return queue.isEmpty() && activeTrack == null; }

    private void playbackLoop() {
        while (running) {
            try {
                QueuedAudio clip = queue.takeFirst();
                queuedCount.decrementAndGet();
                playWav(clip);
            } catch (InterruptedException ignored) {
            } catch (Throwable error) {
                listener.onPlaybackError("Star", "Star voice", error);
            }
        }
    }

    private void playWav(QueuedAudio clip) {
        ParsedWav parsed = ParsedWav.parse(clip.audio.bytes);
        int channelConfig = parsed.channels == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
        int minBuffer = AudioTrack.getMinBufferSize(parsed.sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT);
        if (minBuffer <= 0) minBuffer = parsed.sampleRate * parsed.channels * 2 / 5;
        int bufferSize = Math.max(minBuffer, Math.min(parsed.pcm.length, parsed.sampleRate * parsed.channels * 2));
        AudioTrack track = new AudioTrack.Builder()
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(parsed.sampleRate)
                        .setChannelMask(channelConfig)
                        .build())
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setBufferSizeInBytes(bufferSize)
                .build();

        activeTrack = track;
        boolean completed = false;
        int offset = 0;
        long expectedFrames = parsed.pcm.length / Math.max(1, parsed.channels * 2);
        long calculatedDurationMs = expectedFrames * 1000L / Math.max(1, parsed.sampleRate);
        long durationMs = clip.activity.durationMs > 0 ? clip.activity.durationMs : calculatedDurationMs;
        AtomicBoolean started = new AtomicBoolean(false);
        AtomicBoolean finished = new AtomicBoolean(false);
        Thread watcher = null;
        try {
            track.play();
            watcher = startPlaybackWatcher(track, clip, parsed.sampleRate, durationMs, started, finished);
            while (offset < parsed.pcm.length && running) {
                int count = Math.min(parsed.pcm.length - offset, Math.max(1024, bufferSize));
                int written = track.write(parsed.pcm, offset, count, AudioTrack.WRITE_BLOCKING);
                if (written <= 0) break;
                offset += written;
            }

            long drainStarted = System.currentTimeMillis();
            long maxWait = Math.max(1200L, durationMs + 1500L);
            boolean drained = false;
            while (running && System.currentTimeMillis() - drainStarted < maxWait) {
                long frames = Integer.toUnsignedLong(track.getPlaybackHeadPosition());
                if (frames >= expectedFrames) { drained = true; break; }
                Thread.sleep(12L);
            }
            if (drained && running) Thread.sleep(35L);
            completed = running && drained && offset >= parsed.pcm.length;
        } catch (Throwable error) {
            listener.onPlaybackError(clip.starId, clip.label, error);
        } finally {
            finished.set(true);
            if (watcher != null) watcher.interrupt();
            stopTrack(track);
            try { track.release(); } catch (Exception ignored) { }
            activeTrack = null;
            listener.onPlaybackFinished(clip.starId, clip.label, completed);
            if (isIdle()) listener.onQueueIdle();
        }
    }

    private Thread startPlaybackWatcher(AudioTrack track, QueuedAudio clip, int sampleRate, long durationMs,
                                        AtomicBoolean started, AtomicBoolean finished) {
        Thread watcher = new Thread(() -> {
            AudioTimestamp timestamp = new AudioTimestamp();
            long deadline = android.os.SystemClock.elapsedRealtime() + Math.max(5000L, Math.min(30000L, durationMs + 5000L));
            while (running && !finished.get() && !started.get() && android.os.SystemClock.elapsedRealtime() < deadline) {
                long frame = 0L;
                try {
                    frame = track.getTimestamp(timestamp) && timestamp.framePosition > 0L
                            ? timestamp.framePosition : Integer.toUnsignedLong(track.getPlaybackHeadPosition());
                } catch (IllegalStateException released) { break; }
                if (frame > 0L && started.compareAndSet(false, true)) {
                    long elapsed = frame * 1000L / Math.max(1, sampleRate);
                    listener.onPlaybackStarted(clip.starId, clip.label, durationMs, Math.min(durationMs, elapsed), clip.activity.segmentsMs);
                    break;
                }
                try { Thread.sleep(10L); } catch (InterruptedException ignored) { break; }
            }
            if (!finished.get() && started.compareAndSet(false, true)) {
                listener.onPlaybackStarted(clip.starId, clip.label, durationMs, 0L, clip.activity.segmentsMs);
            }
        }, "six-stars-standalone-playback-start");
        watcher.setDaemon(true);
        watcher.start();
        return watcher;
    }

    private static void stopTrack(AudioTrack track) {
        if (track == null) return;
        try { track.pause(); } catch (Exception ignored) { }
        try { track.flush(); } catch (Exception ignored) { }
        try { track.stop(); } catch (Exception ignored) { }
    }

    @Override public void close() {
        running = false;
        queue.clear();
        queuedCount.set(0);
        stopTrack(activeTrack);
        playbackThread.interrupt();
    }
}
