package com.wds.sixstarsmobile;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.Base64;
import android.view.Gravity;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.DataInputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.Socket;
import java.net.URL;
import java.security.MessageDigest;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Arrays;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;

public final class MainActivity extends Activity {
    private static final int REQUEST_RECORD_AUDIO = 301;
    private static final int SAMPLE_RATE = 16000;
    private static final int FRAME_STATUS = 1;
    private static final int FRAME_AUDIO_WAV = 2;
    private static final int FRAME_ERROR = 3;

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService httpExecutor = Executors.newSingleThreadExecutor();
    private final Object socketLock = new Object();

    private EditText serverUrlEdit;
    private Spinner starSpinner;
    private Spinner requestTypeSpinner;
    private EditText messageEdit;
    private EditText areaEdit;
    private EditText questEdit;
    private CheckBox romanceCheck;
    private CheckBox playOnPhoneCheck;
    private TextView connectionStatusText;
    private TextView audioStatusText;
    private TextView responseText;
    private TextView debugText;
    private TextView voiceDebugText;
    private Button recordButton;
    private File lastVoiceAudioFile;
    private String lastVoiceAudioChecksum = "";
    private ByteArrayOutputStream lastVoicePcmAccumulator;
    private int lastVoiceAccumulatorSampleRate = 24000;
    private int lastVoiceAccumulatorChannels = 1;
    private int lastVoiceAccumulatorTotalChunks = 0;
    private int lastVoiceAccumulatorChunks = 0;

    private volatile boolean destroyed;
    private volatile boolean recording;
    private AudioRecord audioRecord;
    private Thread recordingThread;
    private ByteArrayOutputStream recordedPcm;

    private Socket audioSocket;
    private Thread audioSocketThread;
    private String connectedAudioHost = "";
    private int connectedAudioPort = -1;
    private final WavAudioQueue wavAudioQueue = new WavAudioQueue();

    private final Runnable healthLoop = new Runnable() {
        @Override
        public void run() {
            if (destroyed) return;
            testConnection(false);
            mainHandler.postDelayed(this, 5000);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        lastVoiceAudioFile = new File(getFilesDir(), "last-six-stars-phone-voice.wav");
        buildUi();
        requestAudioPermissionIfNeeded();
        wavAudioQueue.start();
        mainHandler.postDelayed(healthLoop, 600);
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        stopRecording(false);
        closeAudioSocket();
        wavAudioQueue.stop();
        httpExecutor.shutdownNow();
        super.onDestroy();
    }

    private void buildUi() {
        ScrollView scrollView = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(14), dp(14), dp(14), dp(24));
        scrollView.addView(root);

        TextView title = new TextView(this);
        title.setText("Six Stars Mobile");
        title.setTextSize(22);
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        title.setPadding(0, 0, 0, dp(10));
        root.addView(title);

        connectionStatusText = label("Connecting to desktop host...");
        connectionStatusText.setTextSize(14);
        root.addView(connectionStatusText);

        audioStatusText = label("Phone voice socket not connected yet.");
        audioStatusText.setTextSize(14);
        root.addView(audioStatusText);

        root.addView(label("Desktop bridge URL"));
        serverUrlEdit = input("http://grasshoppah:8066", false);
        root.addView(serverUrlEdit);

        LinearLayout connectionButtons = row();
        Button testButton = button("Test Connection");
        testButton.setOnClickListener(v -> testConnection(true));
        Button audioTestButton = button("Test Phone Audio");
        audioTestButton.setOnClickListener(v -> testPhoneAudio());
        Button playSavedButton = button("Play Saved Voice");
        playSavedButton.setOnClickListener(v -> playSavedVoiceAudio());
        connectionButtons.addView(testButton, weightParams(1));
        connectionButtons.addView(audioTestButton, weightParams(1));
        connectionButtons.addView(playSavedButton, weightParams(1));
        root.addView(connectionButtons);

        LinearLayout selectors = row();
        starSpinner = new Spinner(this);
        starSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari"}));
        requestTypeSpinner = new Spinner(this);
        requestTypeSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Chat", "Story", "Letter", "BattleUpdate", "Debug"}));
        selectors.addView(starSpinner, weightParams(1));
        selectors.addView(requestTypeSpinner, weightParams(1));
        root.addView(selectors);

        LinearLayout contextRow = row();
        areaEdit = input("VirethornCaves", false);
        questEdit = input("AndroidPhoneBridge", false);
        contextRow.addView(areaEdit, weightParams(1));
        contextRow.addView(questEdit, weightParams(1));
        root.addView(contextRow);

        romanceCheck = new CheckBox(this);
        romanceCheck.setText("Romance Mode");
        romanceCheck.setChecked(true);
        root.addView(romanceCheck);

        playOnPhoneCheck = new CheckBox(this);
        playOnPhoneCheck.setText("Play Star voice on this phone");
        playOnPhoneCheck.setChecked(true);
        playOnPhoneCheck.setOnCheckedChangeListener((buttonView, isChecked) -> {
            if (isChecked) {
                testConnection(false);
            } else {
                closeAudioSocket();
                audioStatusText.setText("Phone voice socket disabled.");
            }
        });
        root.addView(playOnPhoneCheck);

        root.addView(label("Message to selected Star"));
        messageEdit = input("Elusiaa, hi. I am testing from my Android phone. Stay close and answer naturally.", true);
        messageEdit.setMinLines(4);
        root.addView(messageEdit);

        LinearLayout actionRow = row();
        Button sendButton = button("Send Message");
        sendButton.setOnClickListener(v -> sendMessage());
        recordButton = button("Record Voice");
        recordButton.setOnClickListener(v -> toggleRecording());
        actionRow.addView(sendButton, weightParams(1));
        actionRow.addView(recordButton, weightParams(1));
        root.addView(actionRow);

        root.addView(label("Star response"));
        responseText = output("No response yet.", 6);
        root.addView(responseText);

        root.addView(label("Status / debug"));
        debugText = output("Basic status only. Voice audio streams over the phone socket instead of being stored in this window.", 7);
        debugText.setTextIsSelectable(true);
        root.addView(debugText);

        root.addView(label("Elusiaa's voice stream journal"));
        voiceDebugText = output("✨ Elusiaa is listening for the next voice stream. Checksums will appear here.", 9);
        voiceDebugText.setTextIsSelectable(true);
        root.addView(voiceDebugText);

        setContentView(scrollView);
    }

    private TextView label(String text) {
        TextView label = new TextView(this);
        label.setText(text);
        label.setTextSize(14);
        label.setPadding(0, dp(8), 0, dp(4));
        return label;
    }

    private EditText input(String text, boolean multiLine) {
        EditText editText = new EditText(this);
        editText.setText(text);
        editText.setSingleLine(!multiLine);
        editText.setInputType(multiLine
                ? InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
                : InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        editText.setPadding(dp(10), dp(8), dp(10), dp(8));
        return editText;
    }

    private TextView output(String text, int minLines) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setMinLines(minLines);
        view.setPadding(dp(10), dp(8), dp(10), dp(8));
        view.setBackgroundColor(0xFFF3F4F6);
        return view;
    }

    private Button button(String text) {
        Button button = new Button(this);
        button.setText(text);
        return button;
    }

    private LinearLayout row() {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(4), 0, dp(4));
        return row;
    }

    private LinearLayout.LayoutParams weightParams(float weight) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, weight);
        lp.setMargins(dp(3), dp(3), dp(3), dp(3));
        return lp;
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }

    private void requestAudioPermissionIfNeeded() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQUEST_RECORD_AUDIO);
        }
    }

    private String baseUrl() {
        String value = serverUrlEdit.getText().toString().trim();
        if (value.endsWith("/")) value = value.substring(0, value.length() - 1);
        return value.length() == 0 ? "http://grasshoppah:8066" : value;
    }

    private JSONObject baseRequestJson() throws Exception {
        JSONObject json = new JSONObject();
        json.put("sessionId", "android-phone-dev");
        json.put("starId", starSpinner.getSelectedItem().toString());
        json.put("requestType", requestTypeSpinner.getSelectedItem().toString());
        json.put("area", areaEdit.getText().toString());
        json.put("quest", questEdit.getText().toString());
        json.put("romanceMode", romanceCheck.isChecked());
        json.put("speakOnDesktop", false);
        json.put("returnAudio", playOnPhoneCheck != null && playOnPhoneCheck.isChecked());
        json.put("clientName", "SixStarsMobileAndroid");
        return json;
    }

    private void testConnection(boolean manual) {
        String url = baseUrl() + "/health";
        httpExecutor.execute(() -> {
            try {
                String body = get(url, 8000);
                JSONObject json = new JSONObject(body);
                int audioPort = json.optInt("audioPort", defaultAudioPort());
                String host = new URL(baseUrl()).getHost();
                runOnUiThread(() -> {
                    connectionStatusText.setText("Connected: " + json.optString("machineName", "desktop") + " at " + baseUrl());
                    if (manual) setDebug("Health OK", "HTTP port " + json.optInt("port") + ", audio port " + audioPort + ", audio clients " + json.optInt("audioClientCount"));
                    if (manual) appendVoiceDebug("Elusiaa checked the bridge", "HTTP is alive. audioPort=" + audioPort + ", audioClients=" + json.optInt("audioClientCount"));
                });
                if (playOnPhoneCheck != null && playOnPhoneCheck.isChecked()) {
                    ensureAudioSocket(host, audioPort);
                }
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    connectionStatusText.setText("Not connected to " + baseUrl() + " — " + ex.getMessage());
                    if (manual) setDebug("Health failed", ex.toString());
                });
            }
        });
    }

    private int defaultAudioPort() {
        try {
            URL url = new URL(baseUrl());
            int port = url.getPort() > 0 ? url.getPort() : 8066;
            return port >= 65535 ? port - 1 : port + 1;
        } catch (Exception ignored) {
            return 8067;
        }
    }

    private void testPhoneAudio() {
        try {
            byte[] localWav = createTestToneWav(24000, 1.0, 440.0);
            String checksum = sha256Hex(localWav);
            wavAudioQueue.enqueue(new PhoneVoiceClip(localWav, "local 24 kHz test beep", checksum, false));
            setDebug("Local phone audio test", "Queued a local 24 kHz PCM WAV beep. sha256=" + shortHash(checksum) + ". If you hear this, Android AudioTrack playback works.");
            appendVoiceDebug("Auerlis sparked a local test tone", "bytes=" + localWav.length + ", sha256=" + shortHash(checksum));
        } catch (Exception ex) {
            setDebug("Local phone audio test failed", ex.toString());
        }

        testConnection(false);
        httpExecutor.execute(() -> {
            try {
                String body = postJson(baseUrl() + "/api/mobile/test-audio", "{}", 30000);
                JSONObject json = new JSONObject(body);
                boolean ok = json.optBoolean("ok", false);
                String message = optStringEither(json, "message", "Message", ok ? "Desktop test tone sent." : "Desktop test tone failed.");
                String error = optStringEither(json, "error", "Error", "");
                int sentClients = optIntEither(json, "sentClients", "SentClients", 0);
                int wavBytes = optIntEither(json, "wavBytes", "WavBytes", 0);
                String checksum = optStringEither(json, "sha256", "Sha256", "");
                runOnUiThread(() -> {
                    setDebug("Desktop phone-audio socket test",
                            "ok=" + ok + ", sentClients=" + sentClients + ", wavBytes=" + wavBytes + ", sha256=" + shortHash(checksum)
                                    + (message.length() > 0 ? "\n" + message : "")
                                    + (error.length() > 0 ? "\n" + error : ""));
                    appendVoiceDebug("Nyxari watched the desktop test response", "ok=" + ok + ", clients=" + sentClients + ", bytes=" + wavBytes + ", sha256=" + shortHash(checksum));
                });
            } catch (Exception ex) {
                runOnUiThread(() -> setDebug("Desktop phone-audio socket test failed", ex.toString()));
            }
        });
    }

    private void sendMessage() {
        httpExecutor.execute(() -> {
            try {
                JSONObject json = baseRequestJson();
                json.put("playerText", messageEdit.getText().toString());
                runOnUiThread(() -> {
                    setDebug("Sending text", "Waiting for Star response. Voice will arrive on phone socket when generated.");
                    appendVoiceDebug(starSpinner.getSelectedItem().toString() + " received your message", "HTTP /api/mobile/message sent at " + now());
                });
                String body = postJson(baseUrl() + "/api/mobile/message", json.toString(), 180000);
                handleConversationResponse("Text request complete", body);
            } catch (Exception ex) {
                runOnUiThread(() -> setDebug("Text request failed", ex.toString()));
            }
        });
    }

    private void toggleRecording() {
        if (recording) stopRecording(true); else startRecording();
    }

    private void startRecording() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestAudioPermissionIfNeeded();
            setDebug("Microphone permission needed", "Grant RECORD_AUDIO and press Record Voice again.");
            return;
        }

        int minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
        int bufferSize = Math.max(minBuffer, SAMPLE_RATE * 2);
        recordedPcm = new ByteArrayOutputStream();
        audioRecord = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bufferSize);
        audioRecord.startRecording();
        recording = true;
        recordButton.setText("Stop / Send");
        setDebug("Recording", "Speak now. Press Stop / Send when finished.");

        recordingThread = new Thread(() -> {
            byte[] buffer = new byte[2048];
            while (recording && audioRecord != null) {
                int read = audioRecord.read(buffer, 0, buffer.length);
                if (read > 0) {
                    synchronized (this) {
                        if (recordedPcm != null) recordedPcm.write(buffer, 0, read);
                    }
                }
            }
        }, "six-stars-mobile-recorder");
        recordingThread.start();
    }

    private void stopRecording(boolean send) {
        boolean wasRecording = recording;
        recording = false;
        if (audioRecord != null) {
            try { audioRecord.stop(); } catch (Exception ignored) { }
            try { audioRecord.release(); } catch (Exception ignored) { }
            audioRecord = null;
        }
        if (recordingThread != null) {
            try { recordingThread.join(500); } catch (InterruptedException ignored) { }
            recordingThread = null;
        }
        if (recordButton != null) recordButton.setText("Record Voice");

        if (send && wasRecording) {
            byte[] pcm;
            synchronized (this) {
                pcm = recordedPcm == null ? new byte[0] : recordedPcm.toByteArray();
            }
            if (pcm.length < SAMPLE_RATE / 2) {
                setDebug("Recording too short", "Captured " + pcm.length + " bytes. Record at least about one second.");
                return;
            }
            sendAudio(pcm);
        }
    }

    private void sendAudio(byte[] pcm16) {
        httpExecutor.execute(() -> {
            try {
                byte[] wav = wavFromPcm16(pcm16, SAMPLE_RATE, 1);
                JSONObject json = baseRequestJson();
                json.put("audioFormat", "wav");
                json.put("sampleRate", SAMPLE_RATE);
                json.put("channels", 1);
                json.put("bitsPerSample", 16);
                json.put("audioBase64", Base64.encodeToString(wav, Base64.NO_WRAP));
                String checksum = sha256Hex(wav);
                runOnUiThread(() -> {
                    setDebug("Sending voice", "Uploading recorded WAV to desktop Whisper. Star voice will stream back on the socket. recordedSha256=" + shortHash(checksum));
                    appendVoiceDebug("Khavia carried your recorded voice", "HTTP /api/mobile/audio sent at " + now() + ", bytes=" + wav.length + ", sha256=" + shortHash(checksum));
                });
                String body = postJson(baseUrl() + "/api/mobile/audio", json.toString(), 240000);
                handleConversationResponse("Voice request complete", body);
            } catch (Exception ex) {
                runOnUiThread(() -> setDebug("Voice request failed", ex.toString()));
            }
        });
    }

    private void handleConversationResponse(String title, String body) {
        runOnUiThread(() -> {
            try {
                JSONObject json = new JSONObject(body);
                String transcript = optStringEither(json, "transcript", "Transcript", "");
                String spoken = optStringEither(json, "spokenText", "SpokenText", "");
                String starOutput = optStringEither(json, "starOutput", "StarOutput", "");
                String starId = optStringEither(json, "starId", "StarId", "Star");
                int chunks = optIntEither(json, "voiceChunksSent", "VoiceChunksSent", 0);
                int bytes = optIntEither(json, "voiceBytesSent", "VoiceBytesSent", 0);
                String voiceMessage = optStringEither(json, "voiceMessage", "VoiceMessage", "");
                String voiceError = optStringEither(json, "voiceError", "VoiceError", "");

                String visibleResponse = starOutput.length() > 0 ? starOutput : spoken;
                if (visibleResponse.length() == 0) {
                    visibleResponse = "No spoken text was returned in the HTTP response. Check the desktop debug output for the Star response.";
                }

                String header = title + " — " + starId;
                if (transcript.length() > 0) header += "\nTranscript: " + transcript;
                responseText.setText(header + "\n\n" + clip(visibleResponse, 8000));

                String details = "Response chars: " + visibleResponse.length()
                        + "\nSpoken chars: " + spoken.length()
                        + "\nVoice chunks queued: " + chunks
                        + "\nVoice bytes: " + bytes;
                if (voiceMessage.length() > 0) details += "\n" + voiceMessage;
                if (voiceError.length() > 0) details += "\nVoice note: " + voiceError;
                setDebug(title, details);
                appendVoiceDebug(starId + " returned text", "HTTP response at " + now() + ", responseChars=" + visibleResponse.length() + ", spokenChars=" + spoken.length() + ", voiceMessage=" + clip(voiceMessage, 140));
            } catch (Exception ex) {
                responseText.setText(clip(body, 8000));
                setDebug(title + " (raw)", clip(body, 900));
            }
        });
    }

    private String get(String urlText, int timeoutMs) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(urlText).openConnection();
        connection.setConnectTimeout(timeoutMs);
        connection.setReadTimeout(timeoutMs);
        connection.setRequestMethod("GET");
        return readResponse(connection);
    }

    private String postJson(String urlText, String json, int timeoutMs) throws Exception {
        byte[] bytes = json.getBytes(StandardCharsets.UTF_8);
        HttpURLConnection connection = (HttpURLConnection) new URL(urlText).openConnection();
        connection.setConnectTimeout(6000);
        connection.setReadTimeout(timeoutMs);
        connection.setRequestMethod("POST");
        connection.setDoOutput(true);
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        connection.setRequestProperty("Content-Length", Integer.toString(bytes.length));
        try (OutputStream out = connection.getOutputStream()) {
            out.write(bytes);
        }
        return readResponse(connection);
    }

    private String readResponse(HttpURLConnection connection) throws Exception {
        int code = connection.getResponseCode();
        InputStream inputStream = code >= 200 && code < 300 ? connection.getInputStream() : connection.getErrorStream();
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        if (inputStream != null) {
            byte[] chunk = new byte[4096];
            int read;
            while ((read = inputStream.read(chunk)) >= 0) buffer.write(chunk, 0, read);
        }
        String body = buffer.toString("UTF-8");
        if (code < 200 || code >= 300) throw new RuntimeException("HTTP " + code + ": " + body);
        return body;
    }

    private void ensureAudioSocket(String host, int port) {
        synchronized (socketLock) {
            if (audioSocket != null && audioSocket.isConnected() && !audioSocket.isClosed()
                    && host.equals(connectedAudioHost) && port == connectedAudioPort) {
                return;
            }
            closeAudioSocketLocked();
            connectedAudioHost = host;
            connectedAudioPort = port;
            appendVoiceDebug("Maera opened the phone voice socket", "Connecting to " + host + ":" + port);
            audioSocketThread = new Thread(() -> audioSocketLoop(host, port), "six-stars-phone-audio-socket");
            audioSocketThread.start();
        }
    }

    private void audioSocketLoop(String host, int port) {
        try {
            Socket socket = new Socket(host, port);
            socket.setTcpNoDelay(true);
            synchronized (socketLock) {
                audioSocket = socket;
            }
            runOnUiThread(() -> {
                audioStatusText.setText("Phone voice socket connected: " + host + ":" + port);
                appendVoiceDebug("Elusiaa connected the voice socket", host + ":" + port);
            });
            DataInputStream in = new DataInputStream(socket.getInputStream());
            while (!destroyed && !socket.isClosed()) {
                int type;
                try {
                    type = in.readUnsignedByte();
                } catch (Exception closed) {
                    break;
                }
                int length = in.readInt();
                if (length < 0 || length > 64 * 1024 * 1024) throw new RuntimeException("Invalid audio frame length: " + length);
                byte[] payload = new byte[length];
                in.readFully(payload);
                handleAudioSocketFrame(type, payload);
            }
        } catch (Exception ex) {
            if (!destroyed && playOnPhoneCheck != null && playOnPhoneCheck.isChecked()) {
                runOnUiThread(() -> {
                    audioStatusText.setText("Phone voice socket disconnected: " + ex.getMessage());
                    appendVoiceDebug("Nyxari lost the voice socket", ex.getMessage());
                });
            }
        } finally {
            synchronized (socketLock) {
                if (audioSocket != null) {
                    try { audioSocket.close(); } catch (Exception ignored) { }
                    audioSocket = null;
                }
            }
        }
    }

    private void handleAudioSocketFrame(int type, byte[] payload) throws Exception {
        if (type == FRAME_AUDIO_WAV) {
            if (payload.length < 4) return;
            int headerLength = readIntBigEndian(payload, 0);
            if (headerLength < 0 || 4 + headerLength > payload.length) return;
            String headerJson = new String(payload, 4, headerLength, StandardCharsets.UTF_8);
            JSONObject header = new JSONObject(headerJson);
            byte[] wav = Arrays.copyOfRange(payload, 4 + headerLength, payload.length);
            String receivedChecksum = sha256Hex(wav);
            String expectedChecksum = header.optString("sha256", "");
            boolean checksumOk = expectedChecksum.length() == 0 || expectedChecksum.equalsIgnoreCase(receivedChecksum);
            int chunkNumber = header.optInt("chunkIndex") + 1;
            int totalChunks = header.optInt("totalChunks", 1);
            String starId = header.optString("starId", "Star");
            String savedChecksum = saveLastVoiceAudioFromStreamChunk(wav, header.optInt("chunkIndex"), totalChunks);
            wavAudioQueue.enqueue(new PhoneVoiceClip(wav, starId + " chunk " + chunkNumber + "/" + totalChunks, receivedChecksum, true));
            runOnUiThread(() -> {
                audioStatusText.setText("Queued phone voice chunk " + chunkNumber
                        + "/" + totalChunks
                        + " — " + wav.length + " bytes — sha256 " + shortHash(receivedChecksum)
                        + (checksumOk ? " ✓" : " mismatch"));
                appendVoiceDebug(starId + " voice chunk arrived", "chunk=" + chunkNumber + "/" + totalChunks + ", bytes=" + wav.length + ", frameSha256=" + shortHash(receivedChecksum) + ", savedFileSha256=" + shortHash(savedChecksum) + ", saved=" + lastVoiceAudioFile.getName() + ", checksum=" + (checksumOk ? "ok" : "mismatch expected " + shortHash(expectedChecksum)));
            });
            return;
        }

        String text = new String(payload, StandardCharsets.UTF_8);
        String message = text;
        try {
            JSONObject status = new JSONObject(text);
            message = status.optString("message", text);
        } catch (Exception ignored) { }
        final String displayMessage = message;
        if (type == FRAME_STATUS) {
            runOnUiThread(() -> {
                audioStatusText.setText("Voice socket: " + displayMessage);
                appendVoiceDebug("Elusiaa's socket status", displayMessage);
            });
        } else if (type == FRAME_ERROR) {
            runOnUiThread(() -> {
                setDebug("Voice socket error", displayMessage);
                appendVoiceDebug("Maera felt a voice socket error", displayMessage);
            });
        }
    }

    private void closeAudioSocket() {
        synchronized (socketLock) {
            closeAudioSocketLocked();
        }
    }

    private void closeAudioSocketLocked() {
        if (audioSocket != null) {
            try { audioSocket.close(); } catch (Exception ignored) { }
            audioSocket = null;
        }
        audioSocketThread = null;
        connectedAudioHost = "";
        connectedAudioPort = -1;
    }



    private String saveLastVoiceAudioFromStreamChunk(byte[] wav, int chunkIndex, int totalChunks) {
        try {
            ParsedWav parsed = ParsedWav.parse(wav);
            byte[] combinedWav;
            synchronized (this) {
                if (chunkIndex <= 0 || lastVoicePcmAccumulator == null
                        || parsed.sampleRate != lastVoiceAccumulatorSampleRate
                        || parsed.channels != lastVoiceAccumulatorChannels) {
                    lastVoicePcmAccumulator = new ByteArrayOutputStream();
                    lastVoiceAccumulatorSampleRate = parsed.sampleRate;
                    lastVoiceAccumulatorChannels = parsed.channels;
                    lastVoiceAccumulatorTotalChunks = totalChunks;
                    lastVoiceAccumulatorChunks = 0;
                }
                lastVoicePcmAccumulator.write(parsed.pcm, 0, parsed.pcm.length);
                lastVoiceAccumulatorChunks++;
                combinedWav = wavFromPcm16(lastVoicePcmAccumulator.toByteArray(), lastVoiceAccumulatorSampleRate, lastVoiceAccumulatorChannels);
            }
            String combinedChecksum = sha256Hex(combinedWav);
            saveLastVoiceAudio(combinedWav, combinedChecksum);
            return combinedChecksum;
        } catch (Exception ex) {
            saveLastVoiceAudio(wav, "");
            runOnUiThread(() -> appendVoiceDebug("Khavia saved only the latest voice chunk", "Could not combine chunks: " + ex.toString()));
            return "";
        }
    }

    private void saveLastVoiceAudio(byte[] wav, String checksum) {
        try {
            if (lastVoiceAudioFile == null) lastVoiceAudioFile = new File(getFilesDir(), "last-six-stars-phone-voice.wav");
            try (FileOutputStream out = new FileOutputStream(lastVoiceAudioFile, false)) {
                out.write(wav);
            }
            lastVoiceAudioChecksum = checksum == null ? "" : checksum;
        } catch (Exception ex) {
            runOnUiThread(() -> appendVoiceDebug("Kaelia could not save the received voice", ex.toString()));
        }
    }

    private void playSavedVoiceAudio() {
        httpExecutor.execute(() -> {
            try {
                if (lastVoiceAudioFile == null) lastVoiceAudioFile = new File(getFilesDir(), "last-six-stars-phone-voice.wav");
                if (!lastVoiceAudioFile.exists() || lastVoiceAudioFile.length() <= 0) {
                    runOnUiThread(() -> appendVoiceDebug("Elusiaa searched for saved voice", "No saved phone voice file exists yet."));
                    return;
                }
                byte[] wav = java.nio.file.Files.readAllBytes(lastVoiceAudioFile.toPath());
                String checksum = sha256Hex(wav);
                lastVoiceAudioChecksum = checksum;
                wavAudioQueue.enqueue(new PhoneVoiceClip(wav, "saved phone voice replay", checksum, false));
                runOnUiThread(() -> {
                    setDebug("Play Saved Voice", "Queued " + lastVoiceAudioFile.getName() + " for replay. bytes=" + wav.length + ", sha256=" + shortHash(checksum));
                    appendVoiceDebug("Elusiaa replayed the saved voice", "bytes=" + wav.length + ", sha256=" + shortHash(checksum) + ", accumulatedChunks=" + lastVoiceAccumulatorChunks + "/" + lastVoiceAccumulatorTotalChunks);
                });
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    setDebug("Play Saved Voice failed", ex.toString());
                    appendVoiceDebug("Nyxari could not replay the saved voice", ex.toString());
                });
            }
        });
    }

    private void appendVoiceDebug(String title, String details) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(() -> appendVoiceDebug(title, details));
            return;
        }
        if (voiceDebugText == null) return;
        String now = now();
        String line = "[" + now + "] ✨ " + title;
        if (details != null && details.length() > 0) line += " — " + details.replace('\n', ' ').replace('\r', ' ');
        String old = voiceDebugText.getText() == null ? "" : voiceDebugText.getText().toString();
        String next = line + "\n" + old;
        if (next.length() > 12000) next = next.substring(0, 12000);
        voiceDebugText.setText(next);
    }

    private static String now() {
        return new SimpleDateFormat("HH:mm:ss.SSS", Locale.US).format(new Date());
    }

    private static String sha256Hex(byte[] bytes) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        byte[] hash = digest.digest(bytes == null ? new byte[0] : bytes);
        StringBuilder builder = new StringBuilder(hash.length * 2);
        for (byte b : hash) {
            builder.append(String.format(Locale.US, "%02x", b & 0xff));
        }
        return builder.toString();
    }

    private static String shortHash(String hash) {
        if (hash == null || hash.length() == 0) return "none";
        return hash.length() <= 12 ? hash : hash.substring(0, 12);
    }

    private byte[] wavFromPcm16(byte[] pcm, int sampleRate, int channels) throws Exception {
        int byteRate = sampleRate * channels * 2;
        int dataSize = pcm.length;
        ByteBuffer header = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
        header.put(new byte[]{'R', 'I', 'F', 'F'});
        header.putInt(36 + dataSize);
        header.put(new byte[]{'W', 'A', 'V', 'E'});
        header.put(new byte[]{'f', 'm', 't', ' '});
        header.putInt(16);
        header.putShort((short) 1);
        header.putShort((short) channels);
        header.putInt(sampleRate);
        header.putInt(byteRate);
        header.putShort((short) (channels * 2));
        header.putShort((short) 16);
        header.put(new byte[]{'d', 'a', 't', 'a'});
        header.putInt(dataSize);
        ByteArrayOutputStream wav = new ByteArrayOutputStream(44 + dataSize);
        wav.write(header.array());
        wav.write(pcm);
        return wav.toByteArray();
    }

    private static String optStringEither(JSONObject json, String camelName, String pascalName, String fallback) {
        String value = json.optString(camelName, null);
        if (value == null || value.length() == 0) value = json.optString(pascalName, null);
        return value == null ? fallback : value;
    }

    private static int optIntEither(JSONObject json, String camelName, String pascalName, int fallback) {
        if (json.has(camelName)) return json.optInt(camelName, fallback);
        if (json.has(pascalName)) return json.optInt(pascalName, fallback);
        return fallback;
    }

    private static byte[] createTestToneWav(int sampleRate, double seconds, double frequencyHz) throws Exception {
        int sampleCount = Math.max(1, (int)(sampleRate * seconds));
        byte[] pcm = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++) {
            double t = (double)i / sampleRate;
            double attack = Math.min(1.0, i / (sampleRate * 0.04));
            double release = Math.min(1.0, (sampleCount - i) / (sampleRate * 0.08));
            double envelope = Math.max(0.0, Math.min(attack, release));
            short sample = (short)(Math.sin(2.0 * Math.PI * frequencyHz * t) * 0.22 * envelope * Short.MAX_VALUE);
            pcm[i * 2] = (byte)(sample & 0xff);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }
        ByteBuffer header = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN);
        header.put(new byte[]{'R', 'I', 'F', 'F'});
        header.putInt(36 + pcm.length);
        header.put(new byte[]{'W', 'A', 'V', 'E'});
        header.put(new byte[]{'f', 'm', 't', ' '});
        header.putInt(16);
        header.putShort((short)1);
        header.putShort((short)1);
        header.putInt(sampleRate);
        header.putInt(sampleRate * 2);
        header.putShort((short)2);
        header.putShort((short)16);
        header.put(new byte[]{'d', 'a', 't', 'a'});
        header.putInt(pcm.length);
        ByteArrayOutputStream wav = new ByteArrayOutputStream(44 + pcm.length);
        wav.write(header.array());
        wav.write(pcm);
        return wav.toByteArray();
    }

    private static int readIntBigEndian(byte[] bytes, int offset) {
        return ((bytes[offset] & 0xff) << 24)
                | ((bytes[offset + 1] & 0xff) << 16)
                | ((bytes[offset + 2] & 0xff) << 8)
                | (bytes[offset + 3] & 0xff);
    }

    private void setDebug(String title, String details) {
        String now = new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date());
        debugText.setText("[" + now + "] " + title + "\n" + clip(details, 1200));
    }

    private static String clip(String text, int max) {
        if (text == null) return "";
        if (text.length() <= max) return text;
        return text.substring(0, Math.max(0, max - 3)) + "...";
    }

    private final class WavAudioQueue {
        private final BlockingQueue<PhoneVoiceClip> queue = new LinkedBlockingQueue<>();
        private volatile boolean running;
        private Thread thread;

        void start() {
            if (running) return;
            running = true;
            thread = new Thread(this::loop, "six-stars-wav-player");
            thread.start();
        }

        void stop() {
            running = false;
            if (thread != null) thread.interrupt();
            queue.clear();
        }

        void enqueue(PhoneVoiceClip clip) {
            if (clip != null && clip.wav != null && clip.wav.length > 0) queue.offer(clip);
        }

        private void loop() {
            while (running) {
                try {
                    PhoneVoiceClip clip = queue.take();
                    playWav(clip);
                } catch (InterruptedException ex) {
                    // shutting down or clearing queue
                } catch (Exception ex) {
                    runOnUiThread(() -> appendVoiceDebug("Maera heard a playback failure", ex.toString()));
                }
            }
        }

        private void playWav(PhoneVoiceClip clip) throws Exception {
            ParsedWav parsed = ParsedWav.parse(clip.wav);
            int channelConfig = parsed.channels == 1 ? AudioFormat.CHANNEL_OUT_MONO : AudioFormat.CHANNEL_OUT_STEREO;
            int minBuffer = AudioTrack.getMinBufferSize(parsed.sampleRate, channelConfig, AudioFormat.ENCODING_PCM_16BIT);
            int bufferSize = Math.max(minBuffer, Math.min(parsed.pcm.length, parsed.sampleRate * parsed.channels * 2));
            long durationMs = (long) parsed.pcm.length * 1000L / Math.max(1, parsed.sampleRate * parsed.channels * 2);
            runOnUiThread(() -> {
                audioStatusText.setText("Playing " + clip.label + " — " + parsed.sampleRate + " Hz — sha256 " + shortHash(clip.sha256));
                appendVoiceDebug("Elusiaa started playing audio", clip.label + ", durationMs=" + durationMs + ", bytes=" + clip.wav.length + ", sha256=" + shortHash(clip.sha256));
            });
            AudioTrack track = new AudioTrack(AudioManager.STREAM_MUSIC, parsed.sampleRate, channelConfig,
                    AudioFormat.ENCODING_PCM_16BIT, bufferSize, AudioTrack.MODE_STREAM);
            try {
                track.play();
                int offset = 0;
                while (offset < parsed.pcm.length) {
                    int toWrite = Math.min(parsed.pcm.length - offset, Math.max(1024, bufferSize));
                    int wrote = track.write(parsed.pcm, offset, toWrite);
                    if (wrote <= 0) break;
                    offset += wrote;
                }
                try { Thread.sleep(Math.min(durationMs + 80L, 30000L)); } catch (InterruptedException ignored) { }
                track.stop();
                runOnUiThread(() -> appendVoiceDebug("Elusiaa finished playing audio", clip.label + ", sha256=" + shortHash(clip.sha256)));
            } finally {
                track.release();
            }
        }
    }

    private static final class PhoneVoiceClip {
        final byte[] wav;
        final String label;
        final String sha256;
        final boolean fromDesktop;

        PhoneVoiceClip(byte[] wav, String label, String sha256, boolean fromDesktop) {
            this.wav = wav;
            this.label = label == null ? "phone voice" : label;
            this.sha256 = sha256 == null ? "" : sha256;
            this.fromDesktop = fromDesktop;
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

        static ParsedWav parse(byte[] wav) throws Exception {
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
                    if (audioFormat != 1 || bits != 16) throw new RuntimeException("Only PCM16 WAV is supported. format=" + audioFormat + " bits=" + bits);
                } else if ("data".equals(id)) {
                    data = Arrays.copyOfRange(wav, offset, offset + size);
                }
                offset += size + (size % 2);
            }
            if (data == null) data = Arrays.copyOfRange(wav, 44, wav.length);
            return new ParsedWav(sampleRate, channels <= 0 ? 1 : channels, data);
        }
    }
}
