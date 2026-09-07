package com.wds.sixstarsmobile;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.AutomaticGainControl;
import android.media.audiofx.NoiseSuppressor;
import android.os.Build;
import android.os.Process;

import androidx.core.content.ContextCompat;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayDeque;
import java.util.Arrays;
import java.util.concurrent.CompletableFuture;

/** Owns AudioRecord, the blocking microphone thread, PCM accumulation, WAV packaging, and optional live mic monitoring. */
final class VoiceCaptureService implements AutoCloseable {
    static final int SAMPLE_RATE = 16000;

    // Keep the monitor low latency while still giving AudioTrack enough PCM to ride over small
    // AudioRecord / scheduler timing variations. At 16 kHz mono PCM16, one millisecond is 32 bytes.
    private static final int MONITOR_PREROLL_MS = 24;
    private static final int MONITOR_OUTPUT_BUFFER_MS = 80;
    private static final int MONITOR_MAX_BACKLOG_MS = 180;
    private static final float MONITOR_VOLUME = 0.82f;
    private static final int PCM16_MONO_BYTES_PER_MS = (SAMPLE_RATE * 2) / 1000;

    interface Listener {
        void onStarted();
        void onLevel(double percent);
        void onStopped();
        void onError(Throwable error);
    }

    static final class Recording {
        final byte[] pcm16;
        final byte[] wav;
        final int sampleRate;
        final int channels;
        final long durationMs;

        Recording(byte[] pcm16, byte[] wav, int sampleRate, int channels) {
            this.pcm16 = pcm16;
            this.wav = wav;
            this.sampleRate = sampleRate;
            this.channels = channels;
            this.durationMs = pcm16 == null ? 0L : (pcm16.length / 2L / Math.max(1, channels)) * 1000L / Math.max(1, sampleRate);
        }
    }

    private final Context context;
    private final Object gate = new Object();
    private final Object monitorGate = new Object();
    private final ArrayDeque<byte[]> monitorQueue = new ArrayDeque<>();

    private volatile boolean recording;
    private AudioRecord audioRecord;
    private Thread recordingThread;
    private ByteArrayOutputStream recordedPcm;
    private Listener listener;

    private volatile boolean monitorEnabled;
    private volatile AudioTrack monitorTrack;
    private volatile Thread monitorThread;
    private volatile boolean monitorRunning;
    private int monitorQueuedBytes;
    private volatile long monitorFramesWritten;
    private AcousticEchoCanceler monitorEchoCanceler;
    private NoiseSuppressor monitorNoiseSuppressor;
    private AutomaticGainControl monitorAutomaticGainControl;

    VoiceCaptureService(Context context) { this.context = context.getApplicationContext(); }

    boolean hasPermission() {
        return ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
    }

    boolean isRecording() { return recording; }

    boolean isMonitorEnabled() { return monitorEnabled; }

    void setMonitorEnabled(boolean enabled) {
        synchronized (gate) {
            monitorEnabled = enabled;
            if (!enabled) {
                releaseMonitorLocked(false);
                releaseMonitorCaptureEffectsLocked();
            } else if (recording) {
                configureMonitorCaptureEffectsLocked();
                startMonitorLocked();
            }
        }
    }

    void start(Listener listener) {
        synchronized (gate) {
            if (recording) return;
            if (!hasPermission()) throw new SecurityException("RECORD_AUDIO permission has not been granted.");
            this.listener = listener;
            int minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minBuffer <= 0) minBuffer = SAMPLE_RATE;
            // When live monitoring is enabled, use the communication capture path so Android's
            // acoustic echo canceller has a proper far-end playback reference. Without this, the
            // phone speaker can leak the monitor back into the microphone and create the hollow /
            // "recorded in space" sound after only a few tens of milliseconds.
            int audioSource = monitorEnabled
                    ? MediaRecorder.AudioSource.VOICE_COMMUNICATION
                    : MediaRecorder.AudioSource.VOICE_RECOGNITION;
            audioRecord = new AudioRecord(audioSource, SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(minBuffer * 2, SAMPLE_RATE));
            if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                audioRecord.release();
                audioRecord = null;
                throw new IllegalStateException("AudioRecord could not be initialized.");
            }
            recordedPcm = new ByteArrayOutputStream();
            recording = true;
            if (monitorEnabled) configureMonitorCaptureEffectsLocked();
            audioRecord.startRecording();
            if (monitorEnabled) startMonitorLocked();
            final int readSize = 1024; // 32 ms PCM blocks; AudioRecord itself still uses the larger device minimum buffer.
            recordingThread = new Thread(() -> recordLoop(readSize), "six-stars-mobile-recorder");
            recordingThread.start();
        }
        if (listener != null) listener.onStarted();
    }

    private void recordLoop(int readSize) {
        try { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO); } catch (Throwable ignored) { }
        byte[] buffer = new byte[readSize];
        try {
            while (recording) {
                AudioRecord record = audioRecord;
                if (record == null) break;
                int read = record.read(buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                if (read <= 0) continue;
                synchronized (gate) {
                    if (recordedPcm != null) recordedPcm.write(buffer, 0, read);
                }
                enqueueMonitor(buffer, read);
                Listener current = listener;
                if (current != null) current.onLevel(calculateLevel(buffer, read));
            }
        } catch (Throwable error) {
            Listener current = listener;
            if (current != null && recording) current.onError(error);
        }
    }

    CompletableFuture<Recording> stop() {
        return CompletableFuture.supplyAsync(() -> {
            byte[] pcm;
            Thread thread;
            synchronized (gate) {
                if (!recording && recordedPcm == null) return new Recording(new byte[0], new byte[0], SAMPLE_RATE, 1);
                recording = false;
                AudioRecord record = audioRecord;
                audioRecord = null;
                if (record != null) {
                    try { record.stop(); } catch (Exception ignored) { }
                    releaseMonitorCaptureEffectsLocked();
                    record.release();
                } else {
                    releaseMonitorCaptureEffectsLocked();
                }
                thread = recordingThread;
                recordingThread = null;
            }
            if (thread != null && thread != Thread.currentThread()) {
                try { thread.join(1200L); } catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
            }
            synchronized (gate) {
                // Let the monitor drain the small amount of PCM already captured so the final
                // syllable is not chopped when the user releases/stops Talk.
                releaseMonitorLocked(true);
                pcm = recordedPcm == null ? new byte[0] : recordedPcm.toByteArray();
                recordedPcm = null;
            }
            Listener current = listener;
            if (current != null) current.onStopped();
            return new Recording(pcm, wavFromPcm16(pcm, SAMPLE_RATE, 1), SAMPLE_RATE, 1);
        });
    }

    void cancel() { stop(); }

    /**
     * Creates exactly one long-lived MODE_STREAM AudioTrack for the recording session. Microphone
     * capture never writes directly to AudioTrack: a dedicated audio-priority writer thread drains a
     * tiny jitter queue with WRITE_BLOCKING. That keeps every normal PCM block contiguous while
     * preventing speaker timing from stalling AudioRecord/Whisper capture.
     */
    private void startMonitorLocked() {
        if (!monitorEnabled || monitorTrack != null || monitorRunning) return;
        try {
            int minOut = AudioTrack.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minOut <= 0) minOut = bytesForMonitorMs(MONITOR_OUTPUT_BUFFER_MS);
            int desiredOut = bytesForMonitorMs(MONITOR_OUTPUT_BUFFER_MS);
            int outputBuffer = alignPcm16(Math.max(minOut, desiredOut));

            AudioAttributes.Builder monitorAttributes = new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH);
            // The monitor is supposed to be a dry, centered copy of the microphone. Some recent
            // Android devices spatialize MEDIA tracks (especially with headphones), which gives a
            // mono voice an artificial room / phasey "space" character. Explicitly opt this track
            // out while keeping USAGE_MEDIA so screen-recording playback capture can still see it.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S_V2) {
                monitorAttributes.setSpatializationBehavior(AudioAttributes.SPATIALIZATION_BEHAVIOR_NEVER);
            }

            AudioTrack track = new AudioTrack.Builder()
                    .setAudioAttributes(monitorAttributes.build())
                    .setAudioFormat(new AudioFormat.Builder()
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setSampleRate(SAMPLE_RATE)
                            .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                            .build())
                    .setBufferSizeInBytes(outputBuffer)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build();
            if (track.getState() != AudioTrack.STATE_INITIALIZED) {
                track.release();
                return;
            }
            try { track.setVolume(MONITOR_VOLUME); } catch (Throwable ignored) { }

            synchronized (monitorGate) {
                monitorQueue.clear();
                monitorQueuedBytes = 0;
                monitorFramesWritten = 0L;
                monitorRunning = true;
            }
            monitorTrack = track;
            Thread writer = new Thread(() -> monitorLoop(track), "six-stars-mobile-mic-monitor");
            monitorThread = writer;
            writer.start();
        } catch (Throwable ignored) {
            monitorRunning = false;
            monitorTrack = null;
            monitorThread = null;
            synchronized (monitorGate) {
                monitorQueue.clear();
                monitorQueuedBytes = 0;
            }
        }
    }


    /**
     * Enables the capture-side effects that keep the monitor from recursively recording its own
     * loudspeaker output. They affect only the live microphone AudioRecord session; the monitor
     * remains one dry mono AudioTrack. Effects are best-effort because Android devices differ in
     * what their audio HAL exposes.
     */
    private void configureMonitorCaptureEffectsLocked() {
        if (!monitorEnabled || audioRecord == null) return;
        int sessionId;
        try { sessionId = audioRecord.getAudioSessionId(); }
        catch (Throwable ignored) { return; }
        if (sessionId <= 0) return;

        if (monitorEchoCanceler == null && AcousticEchoCanceler.isAvailable()) {
            try {
                monitorEchoCanceler = AcousticEchoCanceler.create(sessionId);
                if (monitorEchoCanceler != null) monitorEchoCanceler.setEnabled(true);
            } catch (Throwable ignored) {
                monitorEchoCanceler = null;
            }
        }
        if (monitorNoiseSuppressor == null && NoiseSuppressor.isAvailable()) {
            try {
                monitorNoiseSuppressor = NoiseSuppressor.create(sessionId);
                if (monitorNoiseSuppressor != null) monitorNoiseSuppressor.setEnabled(true);
            } catch (Throwable ignored) {
                monitorNoiseSuppressor = null;
            }
        }
        // AGC can pump room reflections between words, which exaggerates the hollow quality we are
        // trying to remove. If the HAL exposes it as an effect, explicitly leave it disabled.
        if (monitorAutomaticGainControl == null && AutomaticGainControl.isAvailable()) {
            try {
                monitorAutomaticGainControl = AutomaticGainControl.create(sessionId);
                if (monitorAutomaticGainControl != null) monitorAutomaticGainControl.setEnabled(false);
            } catch (Throwable ignored) {
                monitorAutomaticGainControl = null;
            }
        }
    }

    private void releaseMonitorCaptureEffectsLocked() {
        AcousticEchoCanceler aec = monitorEchoCanceler;
        monitorEchoCanceler = null;
        if (aec != null) { try { aec.release(); } catch (Throwable ignored) { } }

        NoiseSuppressor ns = monitorNoiseSuppressor;
        monitorNoiseSuppressor = null;
        if (ns != null) { try { ns.release(); } catch (Throwable ignored) { } }

        AutomaticGainControl agc = monitorAutomaticGainControl;
        monitorAutomaticGainControl = null;
        if (agc != null) { try { agc.release(); } catch (Throwable ignored) { } }
    }

    private void enqueueMonitor(byte[] buffer, int length) {
        if (!monitorEnabled || !monitorRunning || buffer == null || length <= 0) return;
        int safeLength = alignPcm16(length);
        if (safeLength <= 0) return;

        // AudioRecord reuses its read buffer, so the monitor queue needs its own immutable copy.
        byte[] block = Arrays.copyOf(buffer, safeLength);
        int maxBacklog = Math.max(bytesForMonitorMs(MONITOR_MAX_BACKLOG_MS), block.length * 2);
        synchronized (monitorGate) {
            if (!monitorRunning) return;

            // A healthy monitor never gets here. If the output device stalls, discard only the
            // oldest delayed audio so monitoring remains close to live instead of becoming a
            // half-second echo. Normal scheduling jitter is absorbed by the queue and AudioTrack.
            while (!monitorQueue.isEmpty() && monitorQueuedBytes + block.length > maxBacklog) {
                byte[] dropped = monitorQueue.removeFirst();
                monitorQueuedBytes -= dropped.length;
            }
            if (block.length <= maxBacklog) {
                monitorQueue.addLast(block);
                monitorQueuedBytes += block.length;
                monitorGate.notifyAll();
            }
        }
    }

    private void monitorLoop(AudioTrack track) {
        try { Process.setThreadPriority(Process.THREAD_PRIORITY_AUDIO); } catch (Throwable ignored) { }
        boolean started = false;
        final int prerollBytes = bytesForMonitorMs(MONITOR_PREROLL_MS);
        try {
            while (true) {
                byte[] block;
                synchronized (monitorGate) {
                    while (monitorQueue.isEmpty() && monitorRunning) {
                        try { monitorGate.wait(12L); }
                        catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); return; }
                    }
                    if (monitorQueue.isEmpty() && !monitorRunning) break;

                    // On initial start, let a very small amount of microphone PCM collect before
                    // beginning playback. This avoids the start/underrun/start rhythm that can make
                    // speech sound gated while adding only about one 32 ms capture block of monitor latency.
                    if (!started && monitorRunning && monitorQueuedBytes < prerollBytes) {
                        try { monitorGate.wait(8L); }
                        catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); return; }
                        continue;
                    }

                    block = monitorQueue.removeFirst();
                    monitorQueuedBytes -= block.length;
                }

                if (!started) {
                    track.play();
                    started = true;
                }
                writeMonitorBlock(track, block);
            }
        } catch (Throwable ignored) {
            // Live monitoring is optional; microphone recording remains authoritative.
        } finally {
            synchronized (monitorGate) { monitorGate.notifyAll(); }
        }
    }

    private void writeMonitorBlock(AudioTrack track, byte[] block) {
        if (track == null || block == null || block.length == 0) return;
        int offset = 0;
        while (offset < block.length) {
            int written = track.write(block, offset, block.length - offset, AudioTrack.WRITE_BLOCKING);
            if (written <= 0) return;
            offset += written;
            monitorFramesWritten += written / 2L;
        }
    }

    private void releaseMonitorLocked(boolean drain) {
        AudioTrack track = monitorTrack;
        Thread writer = monitorThread;
        monitorRunning = false;
        synchronized (monitorGate) {
            if (!drain) {
                monitorQueue.clear();
                monitorQueuedBytes = 0;
            }
            monitorGate.notifyAll();
        }

        // Give the dedicated writer enough time to hand the final captured blocks to the one
        // AudioTrack. This is intentionally short so stopping Talk still feels immediate.
        if (writer != null && writer != Thread.currentThread()) {
            try { writer.join(drain ? 260L : 70L); }
            catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
        }

        // WRITE_BLOCKING means bytes are accepted by AudioTrack, not necessarily heard yet. On a
        // normal stop, wait briefly for its playback head to catch up so the last word rounds out.
        if (drain && track != null) {
            long deadline = System.nanoTime() + 180_000_000L;
            long targetFrames = monitorFramesWritten;
            while (System.nanoTime() < deadline) {
                long played = Integer.toUnsignedLong(track.getPlaybackHeadPosition());
                if (played + 16L >= targetFrames) break;
                try { Thread.sleep(6L); }
                catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); break; }
            }
        }

        monitorThread = null;
        monitorTrack = null;
        synchronized (monitorGate) {
            monitorQueue.clear();
            monitorQueuedBytes = 0;
        }
        if (track == null) return;
        try { track.pause(); } catch (Throwable ignored) { }
        try { track.flush(); } catch (Throwable ignored) { }
        try { track.stop(); } catch (Throwable ignored) { }
        try { track.release(); } catch (Throwable ignored) { }
    }

    private static int bytesForMonitorMs(int ms) {
        return alignPcm16(Math.max(2, ms * PCM16_MONO_BYTES_PER_MS));
    }

    private static int alignPcm16(int bytes) {
        return Math.max(0, bytes & ~1);
    }

    private static double calculateLevel(byte[] bytes, int length) {
        if (bytes == null || length < 2) return 0.0;
        double sum = 0.0;
        int samples = length / 2;
        for (int i = 0; i + 1 < length; i += 2) {
            short value = (short)((bytes[i] & 0xff) | (bytes[i + 1] << 8));
            double normalized = value / 32768.0;
            sum += normalized * normalized;
        }
        double rms = Math.sqrt(sum / Math.max(1, samples));
        return Math.max(0.0, Math.min(100.0, rms * 450.0));
    }

    static byte[] wavFromPcm16(byte[] pcm, int sampleRate, int channels) {
        byte[] safe = pcm == null ? new byte[0] : pcm;
        int byteRate = sampleRate * channels * 2;
        ByteBuffer header = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
        header.put(new byte[]{'R', 'I', 'F', 'F'});
        header.putInt(36 + safe.length);
        header.put(new byte[]{'W', 'A', 'V', 'E'});
        header.put(new byte[]{'f', 'm', 't', ' '});
        header.putInt(16);
        header.putShort((short)1);
        header.putShort((short)channels);
        header.putInt(sampleRate);
        header.putInt(byteRate);
        header.putShort((short)(channels * 2));
        header.putShort((short)16);
        header.put(new byte[]{'d', 'a', 't', 'a'});
        header.putInt(safe.length);
        ByteArrayOutputStream wav = new ByteArrayOutputStream(44 + safe.length);
        byte[] headerBytes = header.array();
        wav.write(headerBytes, 0, headerBytes.length);
        wav.write(safe, 0, safe.length);
        return wav.toByteArray();
    }

    @Override public void close() {
        cancel();
        synchronized (gate) { releaseMonitorLocked(false); }
    }
}
