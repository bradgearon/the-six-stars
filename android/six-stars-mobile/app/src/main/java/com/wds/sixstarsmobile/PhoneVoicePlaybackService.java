package com.wds.sixstarsmobile;

import android.content.Context;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTimestamp;
import android.media.AudioTrack;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Locale;
import java.util.TreeMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingDeque;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Owns the desktop-to-phone TCP voice socket, ordered stream guard, WAV persistence, and AudioTrack
 * playback. The UI only observes lifecycle callbacks and never manipulates socket or playback state.
 */
final class PhoneVoicePlaybackService implements AutoCloseable {
    static final int FRAME_STATUS = 1;
    static final int FRAME_AUDIO_WAV = 2;
    static final int FRAME_ERROR = 3;

    interface Listener {
        void onSocketStatus(String message);
        void onDebug(String title, String details);
        void onError(String title, Throwable error);
        void onStreamState(boolean active, String starId, String action);
        void onPlaybackStarted(PhoneVoiceClip clip, long durationMs, long playbackElapsedMs);
        void onPlaybackFinished(PhoneVoiceClip clip, boolean completedNormally);
        void onQueueIdle();
    }

    static final class PhoneVoiceClip {
        final byte[] wav;
        final String label;
        final String sha256;
        final boolean fromDesktop;
        final String streamId;
        final long streamOrdinal;
        final int audioSequence;
        final int[] speechActivitySegmentsMs;
        final Runnable onCompleted;

        PhoneVoiceClip(byte[] wav, String label, String sha256, boolean fromDesktop,
                       String streamId, long streamOrdinal, int audioSequence,
                       int[] speechActivitySegmentsMs) {
            this(wav, label, sha256, fromDesktop, streamId, streamOrdinal, audioSequence,
                    speechActivitySegmentsMs, null);
        }

        PhoneVoiceClip(byte[] wav, String label, String sha256, boolean fromDesktop,
                       String streamId, long streamOrdinal, int audioSequence,
                       int[] speechActivitySegmentsMs, Runnable onCompleted) {
            this.wav = wav == null ? new byte[0] : wav;
            this.label = label == null ? "phone voice" : label;
            this.sha256 = sha256 == null ? "" : sha256;
            this.fromDesktop = fromDesktop;
            this.streamId = streamId == null ? "" : streamId;
            this.streamOrdinal = streamOrdinal;
            this.audioSequence = audioSequence;
            this.speechActivitySegmentsMs = speechActivitySegmentsMs == null ? new int[0] : speechActivitySegmentsMs;
            this.onCompleted = onCompleted;
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
                    if (audioFormat != 1 || bits != 16) throw new IllegalArgumentException("Only PCM16 WAV is supported.");
                } else if ("data".equals(id)) {
                    data = Arrays.copyOfRange(wav, offset, offset + size);
                }
                offset += size + (size % 2);
            }
            if (data == null) data = Arrays.copyOfRange(wav, 44, wav.length);
            return new ParsedWav(sampleRate, Math.max(1, channels), data);
        }
    }

    private static final Listener NO_OP_LISTENER = new Listener() {
        @Override public void onSocketStatus(String message) { }
        @Override public void onDebug(String title, String details) { }
        @Override public void onError(String title, Throwable error) { }
        @Override public void onStreamState(boolean active, String starId, String action) { }
        @Override public void onPlaybackStarted(PhoneVoiceClip clip, long durationMs, long playbackElapsedMs) { }
        @Override public void onPlaybackFinished(PhoneVoiceClip clip, boolean completedNormally) { }
        @Override public void onQueueIdle() { }
    };

    private final Context context;
    private final LocalNetworkBindingService localNetwork;
    private final Handler mainHandler;
    private volatile Listener listener = NO_OP_LISTENER;
    private final ExecutorService fileExecutor = Executors.newSingleThreadExecutor(r -> new Thread(r, "six-stars-phone-voice-files"));
    private final Object socketLock = new Object();
    private final Object voiceStreamLock = new Object();
    private final Object orderedQueueLock = new Object();
    private final TreeMap<Integer, PhoneVoiceClip> pendingDesktopClips = new TreeMap<>();
    private final HashSet<Integer> acceptedDesktopSequences = new HashSet<>();
    private final WavAudioQueue playbackQueue = new WavAudioQueue();

    private volatile boolean closed;
    private Socket audioSocket;
    private Thread audioSocketThread;
    private String connectedHost = "";
    private int connectedPort = -1;
    private String activeStreamId = "";
    private long activeStreamOrdinal;
    private volatile boolean desktopStreamActive;
    private int nextExpectedSequence;
    private int droppedStaleFrames;
    private final File lastVoiceFile;
    private ByteArrayOutputStream savedPcm;
    private int savedSampleRate = 24000;
    private int savedChannels = 1;

    PhoneVoicePlaybackService(Context context, LocalNetworkBindingService localNetwork) {
        this.context = context.getApplicationContext();
        this.localNetwork = localNetwork;
        this.mainHandler = new Handler(Looper.getMainLooper());
        this.lastVoiceFile = new File(this.context.getFilesDir(), "last-six-stars-phone-voice.wav");
    }

    void setListener(Listener value) { listener = value == null ? NO_OP_LISTENER : value; }

    void start() { playbackQueue.start(); }
    boolean isIdle() { return playbackQueue.isIdle(); }
    boolean isPaused() { return playbackQueue.isPaused(); }
    boolean isDesktopStreamActive() { return desktopStreamActive; }
    void pause() { playbackQueue.pausePlayback(); }
    void resume() { playbackQueue.resumePlayback(); }
    void clearForNewStream() { playbackQueue.clearForNewStream(); }
    void beginPreparedPrefix() { playbackQueue.beginPreparedPrefix(); }
    void enqueuePreparedPrefix(ArrayList<PhoneVoiceClip> clips) { playbackQueue.enqueuePreparedPrefix(clips); }
    void cancelPreparedPrefix() { playbackQueue.cancelPreparedPrefix(); }
    void enqueue(PhoneVoiceClip clip) { playbackQueue.enqueue(clip); }

    void disconnect() {
        synchronized (socketLock) { closeSocketLocked(); }
        resetForReconnect();
        post(() -> listener.onSocketStatus("Phone voice socket disconnected."));
    }

    void connect(String host, int port) {
        if (host == null || host.trim().isEmpty() || port <= 0) return;
        synchronized (socketLock) {
            if (audioSocket != null && audioSocket.isConnected() && !audioSocket.isClosed()
                    && host.equals(connectedHost) && port == connectedPort) return;
            closeSocketLocked();
            connectedHost = host;
            connectedPort = port;
            listener.onDebug("Phone voice socket", "Connecting to " + host + ":" + port
                    + (localNetwork == null ? "" : " via " + localNetwork.routeDescription(host)));
            audioSocketThread = new Thread(() -> socketLoop(host, port), "six-stars-phone-audio-socket");
            audioSocketThread.start();
        }
    }

    private void socketLoop(String host, int port) {
        Socket socket = null;
        try {
            socket = localNetwork == null
                    ? new Socket(host, port)
                    : localNetwork.connectSocket(host, port, 6000);
            socket.setTcpNoDelay(true);
            synchronized (socketLock) {
                if (closed || !host.equals(connectedHost) || port != connectedPort) {
                    try { socket.close(); } catch (Exception ignored) { }
                    return;
                }
                audioSocket = socket;
            }
            resetForReconnect();
            post(() -> listener.onSocketStatus("Phone voice socket connected: " + host + ":" + port));
            DataInputStream input = new DataInputStream(socket.getInputStream());
            while (!closed && !socket.isClosed()) {
                int type;
                try { type = input.readUnsignedByte(); } catch (Exception disconnected) { break; }
                int length = input.readInt();
                if (length < 0 || length > 64 * 1024 * 1024) throw new IllegalStateException("Invalid audio frame length: " + length);
                byte[] payload = new byte[length];
                input.readFully(payload);
                handleFrame(type, payload);
            }
        } catch (Throwable error) {
            if (!closed) post(() -> listener.onError("Phone voice socket disconnected", error));
        } finally {
            boolean wasCurrent;
            synchronized (socketLock) {
                wasCurrent = socket != null && audioSocket == socket;
                if (wasCurrent) audioSocket = null;
            }
            if (socket != null) try { socket.close(); } catch (Exception ignored) { }
            if (wasCurrent) {
                desktopStreamActive = false;
                post(() -> { listener.onStreamState(false, "Star", "disconnected"); listener.onQueueIdle(); });
            }
        }
    }

    private void handleFrame(int type, byte[] payload) throws Exception {
        if (type == FRAME_AUDIO_WAV) {
            if (payload.length < 4) return;
            int headerLength = readIntBigEndian(payload, 0);
            if (headerLength < 0 || 4 + headerLength > payload.length) return;
            JSONObject header = new JSONObject(new String(payload, 4, headerLength, StandardCharsets.UTF_8));
            byte[] wav = Arrays.copyOfRange(payload, 4 + headerLength, payload.length);
            String checksum = sha256Hex(wav);
            int chunkNumber = header.optInt("chunkIndex") + 1;
            int totalChunks = header.optInt("totalChunks", 1);
            int sequence = header.optInt("audioSequence", header.optInt("chunkIndex", 0));
            String starId = header.optString("starId", "Star");
            String streamId = header.optString("streamId", "");
            long ordinal = header.optLong("streamOrdinal", 0L);
            int[] segments = parseSpeechActivitySegments(header.optString("speechActivitySegmentsMs", ""));
            if (!acceptStreamFrame(streamId, ordinal, starId)) {
                droppedStaleFrames++;
                post(() -> listener.onDebug("Stale voice frame dropped", "stream=" + ordinal + ", active=" + activeStreamOrdinal + ", dropped=" + droppedStaleFrames));
                return;
            }
            PhoneVoiceClip clip = new PhoneVoiceClip(wav, starId + " chunk " + chunkNumber + "/" + totalChunks,
                    checksum, true, streamId, ordinal, sequence, segments);
            int queued = enqueueDesktopInOrder(clip);
            post(() -> listener.onSocketStatus((queued > 0 ? "Queued" : "Buffered") + " phone voice chunk " + chunkNumber + "/" + totalChunks));
            return;
        }

        String text = new String(payload, StandardCharsets.UTF_8);
        String message = text;
        try {
            JSONObject status = new JSONObject(text);
            message = status.optString("message", text);
            handleStreamControl(status);
        } catch (Exception ignored) { }
        String finalMessage = message;
        if (type == FRAME_STATUS) post(() -> listener.onSocketStatus("Voice socket: " + finalMessage));
        else if (type == FRAME_ERROR) post(() -> listener.onError("Voice socket error", new IllegalStateException(finalMessage)));
    }

    private boolean acceptStreamFrame(String streamId, long ordinal, String starId) {
        if (ordinal <= 0L || streamId == null || streamId.isEmpty()) return true;
        boolean started = false;
        synchronized (voiceStreamLock) {
            boolean same = streamId.equals(activeStreamId);
            if (!same && !activeStreamId.isEmpty() && ordinal < activeStreamOrdinal) return false;
            if (!same || ordinal > activeStreamOrdinal) {
                activeStreamOrdinal = ordinal;
                activeStreamId = streamId;
                started = true;
                resetOrderedQueue();
                resetSavedAccumulator();
            }
        }
        if (started) {
            desktopStreamActive = true;
            playbackQueue.prepareForDesktopStream();
            post(() -> listener.onStreamState(true, starId, "start"));
        }
        return true;
    }

    private void handleStreamControl(JSONObject status) {
        String action = status.optString("streamAction", "");
        if (action.isEmpty()) return;
        String streamId = status.optString("streamId", "");
        long ordinal = status.optLong("streamOrdinal", 0L);
        String starId = status.optString("starId", "Star");
        if (ordinal <= 0L) return;
        boolean clear = false;
        synchronized (voiceStreamLock) {
            if ("start".equalsIgnoreCase(action)) {
                desktopStreamActive = true;
                if (ordinal != activeStreamOrdinal || !streamId.equals(activeStreamId)) {
                    activeStreamOrdinal = ordinal;
                    activeStreamId = streamId;
                    resetOrderedQueue();
                    resetSavedAccumulator();
                    clear = true;
                }
            } else if ("end".equalsIgnoreCase(action) || "cancel".equalsIgnoreCase(action)) {
                desktopStreamActive = false;
                if (streamId.equals(activeStreamId) || ordinal >= activeStreamOrdinal) {
                    activeStreamOrdinal = ordinal;
                    activeStreamId = streamId;
                    clear = "cancel".equalsIgnoreCase(action);
                    if (clear) { resetOrderedQueue(); resetSavedAccumulator(); }
                }
            }
        }
        if (clear) playbackQueue.prepareForDesktopStream();
        post(() -> listener.onStreamState(desktopStreamActive, starId, action));
    }

    private int enqueueDesktopInOrder(PhoneVoiceClip clip) {
        if (!isCurrent(clip)) return 0;
        int queued = 0;
        synchronized (orderedQueueLock) {
            if (clip.audioSequence < nextExpectedSequence || acceptedDesktopSequences.contains(clip.audioSequence)
                    || pendingDesktopClips.containsKey(clip.audioSequence)) return 0;
            pendingDesktopClips.put(clip.audioSequence, clip);
            while (true) {
                PhoneVoiceClip ready = pendingDesktopClips.remove(nextExpectedSequence);
                if (ready == null) break;
                acceptedDesktopSequences.add(ready.audioSequence);
                playbackQueue.enqueue(ready);
                nextExpectedSequence++;
                queued++;
            }
        }
        return queued;
    }

    private boolean isCurrent(PhoneVoiceClip clip) {
        if (clip == null || !clip.fromDesktop || clip.streamOrdinal <= 0L) return true;
        synchronized (voiceStreamLock) {
            return clip.streamOrdinal == activeStreamOrdinal && clip.streamId.equals(activeStreamId);
        }
    }

    private void resetOrderedQueue() {
        synchronized (orderedQueueLock) {
            pendingDesktopClips.clear();
            acceptedDesktopSequences.clear();
            nextExpectedSequence = 0;
        }
    }

    private void resetSavedAccumulator() { synchronized (this) { savedPcm = null; } }

    private void resetForReconnect() {
        desktopStreamActive = false;
        synchronized (voiceStreamLock) {
            activeStreamId = "";
            activeStreamOrdinal = 0L;
            droppedStaleFrames = 0;
        }
        resetOrderedQueue();
        resetSavedAccumulator();
        playbackQueue.clearForNewStream();
    }

    void playSavedVoice() {
        fileExecutor.execute(() -> {
            try {
                if (!lastVoiceFile.isFile() || lastVoiceFile.length() == 0L) {
                    post(() -> listener.onDebug("Saved voice", "No saved phone voice exists yet."));
                    return;
                }
                byte[] wav = java.nio.file.Files.readAllBytes(lastVoiceFile.toPath());
                String checksum = sha256Hex(wav);
                playbackQueue.enqueue(new PhoneVoiceClip(wav, "saved phone voice replay", checksum, false, "", 0L, 0, new int[0]));
                post(() -> listener.onDebug("Saved voice queued", "bytes=" + wav.length + ", sha256=" + shortHash(checksum)));
            } catch (Throwable error) {
                post(() -> listener.onError("Play saved voice failed", error));
            }
        });
    }

    private void appendToSavedVoice(PhoneVoiceClip clip) {
        try {
            ParsedWav parsed = ParsedWav.parse(clip.wav);
            byte[] combined;
            synchronized (this) {
                if (savedPcm == null || savedSampleRate != parsed.sampleRate || savedChannels != parsed.channels) {
                    savedPcm = new ByteArrayOutputStream();
                    savedSampleRate = parsed.sampleRate;
                    savedChannels = parsed.channels;
                }
                savedPcm.write(parsed.pcm, 0, parsed.pcm.length);
                combined = VoiceCaptureService.wavFromPcm16(savedPcm.toByteArray(), savedSampleRate, savedChannels);
            }
            try (FileOutputStream output = new FileOutputStream(lastVoiceFile, false)) { output.write(combined); }
        } catch (Throwable error) {
            post(() -> listener.onError("Could not save received voice", error));
        }
    }

    private final class WavAudioQueue {
        private final LinkedBlockingDeque<PhoneVoiceClip> queue = new LinkedBlockingDeque<>();
        private final Object pauseLock = new Object();
        private final Object preparedLock = new Object();
        private volatile boolean running;
        private volatile boolean paused;
        private volatile boolean preparedPrefixPending;
        private volatile AudioTrack activeTrack;
        private volatile PhoneVoiceClip activeClip;
        private Thread thread;

        void start() {
            if (running) return;
            running = true;
            thread = new Thread(this::loop, "six-stars-wav-player");
            thread.start();
        }

        void stop() {
            running = false;
            synchronized (pauseLock) { pauseLock.notifyAll(); }
            clearForNewStream();
        }

        boolean isPaused() { return paused; }
        boolean isIdle() { synchronized (preparedLock) { return queue.isEmpty() && activeTrack == null && activeClip == null && !preparedPrefixPending; } }
        void enqueue(PhoneVoiceClip clip) { if (clip != null && clip.wav.length > 0) queue.offerLast(clip); }
        void beginPreparedPrefix() { preparedPrefixPending = true; }
        void cancelPreparedPrefix() { synchronized (preparedLock) { preparedPrefixPending = false; preparedLock.notifyAll(); } }
        void enqueuePreparedPrefix(ArrayList<PhoneVoiceClip> clips) {
            if (clips != null) for (int i = clips.size() - 1; i >= 0; i--) enqueueFirst(clips.get(i));
            cancelPreparedPrefix();
        }
        private void enqueueFirst(PhoneVoiceClip clip) { if (clip != null && clip.wav.length > 0) queue.offerFirst(clip); }

        void pausePlayback() {
            paused = true;
            AudioTrack track = activeTrack;
            if (track != null) try { track.pause(); } catch (Exception ignored) { }
            post(() -> listener.onDebug("Star voice paused", "The current sample position and queue were preserved."));
        }

        void resumePlayback() {
            paused = false;
            AudioTrack track = activeTrack;
            if (track != null) try { track.play(); } catch (Exception ignored) { }
            synchronized (pauseLock) { pauseLock.notifyAll(); }
            post(() -> listener.onDebug("Star voice resumed", "Playback continued without flushing the queue."));
        }

        void prepareForDesktopStream() {
            queue.removeIf(clip -> clip != null && clip.fromDesktop);
            PhoneVoiceClip current = activeClip;
            if (current != null && current.fromDesktop) stopTrack(activeTrack);
            resetOrderedQueue();
        }

        void clearForNewStream() {
            cancelPreparedPrefix();
            queue.clear();
            stopTrack(activeTrack);
            if (thread != null) thread.interrupt();
        }

        private void loop() {
            while (running) {
                try {
                    PhoneVoiceClip clip = queue.takeFirst();
                    if (clip.fromDesktop && preparedPrefixPending) {
                        queue.offerFirst(clip);
                        synchronized (preparedLock) { if (preparedPrefixPending && running) preparedLock.wait(100L); }
                        continue;
                    }
                    if (!isCurrent(clip)) continue;
                    playWav(clip);
                } catch (InterruptedException ignored) {
                } catch (Throwable error) {
                    post(() -> listener.onError("Audio playback failed", error));
                }
            }
        }

        private void playWav(PhoneVoiceClip clip) {
            ParsedWav parsed = ParsedWav.parse(clip.wav);
            int channelConfig = parsed.channels == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
            int minBuffer = AudioTrack.getMinBufferSize(parsed.sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT);
            if (minBuffer <= 0) minBuffer = parsed.sampleRate * parsed.channels * 2 / 5;
            int bufferSize = Math.max(minBuffer, Math.min(parsed.pcm.length, parsed.sampleRate * parsed.channels * 2));
            AudioTrack track = new AudioTrack.Builder()
                    .setAudioAttributes(new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                    .setAudioFormat(new AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(parsed.sampleRate).setChannelMask(channelConfig).build())
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setBufferSizeInBytes(bufferSize)
                    .build();
            activeTrack = track;
            activeClip = clip;
            long expectedFrames = parsed.pcm.length / Math.max(1, parsed.channels * 2);
            long durationMs = expectedFrames * 1000L / Math.max(1, parsed.sampleRate);
            AtomicBoolean started = new AtomicBoolean(false);
            AtomicBoolean finished = new AtomicBoolean(false);
            boolean completed = false;
            Thread watcher = null;
            int offset = 0;
            try {
                track.play();
                watcher = startPlaybackWatcher(track, clip, parsed.sampleRate, durationMs, started, finished);
                while (offset < parsed.pcm.length && running && isCurrent(clip)) {
                    waitWhilePaused(track);
                    int count = Math.min(parsed.pcm.length - offset, Math.max(1024, bufferSize));
                    int written = track.write(parsed.pcm, offset, count, AudioTrack.WRITE_BLOCKING);
                    if (written <= 0) break;
                    offset += written;
                }
                long waitStarted = System.currentTimeMillis();
                long maxWait = Math.max(250L, durationMs + 750L);
                while (running && isCurrent(clip) && Integer.toUnsignedLong(track.getPlaybackHeadPosition()) < expectedFrames
                        && System.currentTimeMillis() - waitStarted < maxWait) {
                    try { Thread.sleep(12L); } catch (InterruptedException interrupted) { break; }
                }
                completed = running && isCurrent(clip) && offset >= parsed.pcm.length;
            } finally {
                finished.set(true);
                if (watcher != null) watcher.interrupt();
                stopTrack(track);
                track.release();
                activeTrack = null;
                activeClip = null;
                if (completed && clip.fromDesktop) appendToSavedVoice(clip);
                if (completed && clip.onCompleted != null) post(clip.onCompleted);
                boolean finalCompleted = completed;
                post(() -> {
                    listener.onPlaybackFinished(clip, finalCompleted);
                    if (isIdle()) listener.onQueueIdle();
                });
            }
        }

        private void waitWhilePaused(AudioTrack track) {
            while (running && paused) {
                try { if (track.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) track.pause(); } catch (Exception ignored) { }
                synchronized (pauseLock) { try { if (paused && running) pauseLock.wait(250L); } catch (InterruptedException ignored) { } }
            }
            try { if (running && track.getPlayState() == AudioTrack.PLAYSTATE_PAUSED) track.play(); } catch (Exception ignored) { }
        }

        private Thread startPlaybackWatcher(AudioTrack track, PhoneVoiceClip clip, int sampleRate, long durationMs,
                                            AtomicBoolean started, AtomicBoolean finished) {
            Thread watcher = new Thread(() -> {
                AudioTimestamp timestamp = new AudioTimestamp();
                long deadline = SystemClock.elapsedRealtime() + Math.max(5000L, Math.min(30000L, durationMs + 5000L));
                while (running && !finished.get() && !started.get() && isCurrent(clip) && SystemClock.elapsedRealtime() < deadline) {
                    long frame = 0L;
                    try {
                        frame = track.getTimestamp(timestamp) && timestamp.framePosition > 0L
                                ? timestamp.framePosition : Integer.toUnsignedLong(track.getPlaybackHeadPosition());
                    } catch (IllegalStateException released) { break; }
                    if (frame > 0L && started.compareAndSet(false, true)) {
                        long elapsed = frame * 1000L / Math.max(1, sampleRate);
                        post(() -> listener.onPlaybackStarted(clip, durationMs, Math.min(durationMs, elapsed)));
                        break;
                    }
                    try { Thread.sleep(10L); } catch (InterruptedException ignored) { break; }
                }
            }, "six-stars-playback-start-watcher");
            watcher.start();
            return watcher;
        }
    }

    private static void stopTrack(AudioTrack track) {
        if (track == null) return;
        try { track.pause(); } catch (Exception ignored) { }
        try { track.flush(); } catch (Exception ignored) { }
        try { track.stop(); } catch (Exception ignored) { }
    }

    static int[] parseSpeechActivitySegments(String compact) {
        if (compact == null || compact.trim().isEmpty()) return new int[0];
        ArrayList<Integer> values = new ArrayList<>();
        for (String part : compact.split(";")) {
            String[] pair = part.trim().split(":");
            if (pair.length != 2) continue;
            try {
                values.add(Math.max(0, Integer.parseInt(pair[0].trim())));
                values.add(Math.max(40, Math.min(4000, Integer.parseInt(pair[1].trim()))));
                if (values.size() >= 480) break;
            } catch (Exception ignored) { }
        }
        int[] result = new int[values.size()];
        for (int i = 0; i < values.size(); i++) result[i] = values.get(i);
        return result;
    }

    private static int readIntBigEndian(byte[] bytes, int offset) {
        return ((bytes[offset] & 0xff) << 24) | ((bytes[offset + 1] & 0xff) << 16)
                | ((bytes[offset + 2] & 0xff) << 8) | (bytes[offset + 3] & 0xff);
    }

    static String sha256Hex(byte[] bytes) {
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(bytes == null ? new byte[0] : bytes);
            StringBuilder builder = new StringBuilder(hash.length * 2);
            for (byte value : hash) builder.append(String.format(Locale.US, "%02x", value & 0xff));
            return builder.toString();
        } catch (Exception impossible) { throw new IllegalStateException(impossible); }
    }

    static String shortHash(String hash) { return hash == null || hash.isEmpty() ? "none" : hash.substring(0, Math.min(12, hash.length())); }
    private void post(Runnable action) { mainHandler.post(action); }

    private void closeSocketLocked() {
        if (audioSocket != null) try { audioSocket.close(); } catch (Exception ignored) { }
        audioSocket = null;
        audioSocketThread = null;
        connectedHost = "";
        connectedPort = -1;
        desktopStreamActive = false;
    }

    @Override public void close() {
        closed = true;
        synchronized (socketLock) { closeSocketLocked(); }
        playbackQueue.stop();
        fileExecutor.shutdownNow();
    }
}
