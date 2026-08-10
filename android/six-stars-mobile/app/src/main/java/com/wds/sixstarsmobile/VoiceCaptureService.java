package com.wds.sixstarsmobile;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;

import androidx.core.content.ContextCompat;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.CompletableFuture;

/** Owns AudioRecord, the blocking microphone thread, PCM accumulation, and WAV packaging. */
final class VoiceCaptureService implements AutoCloseable {
    static final int SAMPLE_RATE = 16000;

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
    private volatile boolean recording;
    private AudioRecord audioRecord;
    private Thread recordingThread;
    private ByteArrayOutputStream recordedPcm;
    private Listener listener;

    VoiceCaptureService(Context context) { this.context = context.getApplicationContext(); }

    boolean hasPermission() {
        return ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
    }

    boolean isRecording() { return recording; }

    void start(Listener listener) {
        synchronized (gate) {
            if (recording) return;
            if (!hasPermission()) throw new SecurityException("RECORD_AUDIO permission has not been granted.");
            this.listener = listener;
            int minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minBuffer <= 0) minBuffer = SAMPLE_RATE;
            audioRecord = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(minBuffer * 2, SAMPLE_RATE));
            if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                audioRecord.release();
                audioRecord = null;
                throw new IllegalStateException("AudioRecord could not be initialized.");
            }
            recordedPcm = new ByteArrayOutputStream();
            recording = true;
            audioRecord.startRecording();
            final int readSize = Math.max(1024, minBuffer);
            recordingThread = new Thread(() -> recordLoop(readSize), "six-stars-mobile-recorder");
            recordingThread.start();
        }
        if (listener != null) listener.onStarted();
    }

    private void recordLoop(int readSize) {
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
                    record.release();
                }
                thread = recordingThread;
                recordingThread = null;
            }
            if (thread != null && thread != Thread.currentThread()) {
                try { thread.join(1200L); } catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); }
            }
            synchronized (gate) {
                pcm = recordedPcm == null ? new byte[0] : recordedPcm.toByteArray();
                recordedPcm = null;
            }
            Listener current = listener;
            if (current != null) current.onStopped();
            return new Recording(pcm, wavFromPcm16(pcm, SAMPLE_RATE, 1), SAMPLE_RATE, 1);
        });
    }

    void cancel() { stop(); }

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

    @Override public void close() { cancel(); }
}
