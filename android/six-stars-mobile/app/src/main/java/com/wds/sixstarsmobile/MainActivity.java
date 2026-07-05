package com.wds.sixstarsmobile;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.Base64;
import android.view.Gravity;
import android.view.View;
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
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int REQUEST_RECORD_AUDIO = 301;
    private static final int SAMPLE_RATE = 16000;

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    private EditText serverUrlEdit;
    private Spinner starSpinner;
    private Spinner requestTypeSpinner;
    private EditText messageEdit;
    private EditText areaEdit;
    private EditText questEdit;
    private CheckBox romanceCheck;
    private CheckBox speakOnDesktopCheck;
    private TextView connectionStatusText;
    private TextView responseText;
    private TextView debugText;
    private Button recordButton;

    private volatile boolean destroyed;
    private volatile boolean recording;
    private AudioRecord audioRecord;
    private Thread recordingThread;
    private ByteArrayOutputStream recordedPcm;

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
        buildUi();
        requestAudioPermissionIfNeeded();
        mainHandler.postDelayed(healthLoop, 600);
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        stopRecording(false);
        executor.shutdownNow();
        super.onDestroy();
    }

    private void buildUi() {
        ScrollView scrollView = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(14), dp(14), dp(14), dp(24));
        scrollView.addView(root);

        TextView title = new TextView(this);
        title.setText("Six Stars Mobile Debug Client");
        title.setTextSize(22);
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        title.setPadding(0, 0, 0, dp(10));
        root.addView(title);

        connectionStatusText = label("Connecting to desktop host...");
        connectionStatusText.setTextSize(14);
        root.addView(connectionStatusText);

        root.addView(label("Desktop bridge URL"));
        serverUrlEdit = input("http://192.168.1.217:8066", false);
        root.addView(serverUrlEdit);

        LinearLayout connectionButtons = row();
        Button testButton = button("Test Connection");
        testButton.setOnClickListener(v -> testConnection(true));
        connectionButtons.addView(testButton, weightParams(1));
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

        speakOnDesktopCheck = new CheckBox(this);
        speakOnDesktopCheck.setText("Play Star voice on desktop after response");
        speakOnDesktopCheck.setChecked(true);
        root.addView(speakOnDesktopCheck);

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

        root.addView(label("Debug window"));
        debugText = output("The app pings /health every 5 seconds and posts JSON to /api/mobile/message or /api/mobile/audio.", 14);
        debugText.setTextIsSelectable(true);
        root.addView(debugText);

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
        json.put("speakOnDesktop", speakOnDesktopCheck.isChecked());
        json.put("clientName", "SixStarsMobileAndroid");
        return json;
    }

    private void testConnection(boolean manual) {
        String url = baseUrl() + "/health";
        executor.execute(() -> {
            try {
                String body = get(url, 3500);
                JSONObject json = new JSONObject(body);
                runOnUiThread(() -> {
                    connectionStatusText.setText("Connected: " + json.optString("machineName", "desktop") + " at " + baseUrl());
                    if (manual) setDebug("Health OK", body);
                });
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    connectionStatusText.setText("Not connected to " + baseUrl() + " — " + ex.getMessage());
                    if (manual) setDebug("Health failed", ex.toString());
                });
            }
        });
    }

    private void sendMessage() {
        executor.execute(() -> {
            try {
                JSONObject json = baseRequestJson();
                json.put("playerText", messageEdit.getText().toString());
                String body = postJson(baseUrl() + "/api/mobile/message", json.toString(), 180000);
                handleConversationResponse("Text request complete", body);
            } catch (Exception ex) {
                runOnUiThread(() -> setDebug("Text request failed", ex.toString()));
            }
        });
    }

    private void toggleRecording() {
        if (recording) {
            stopRecording(true);
        } else {
            startRecording();
        }
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
        executor.execute(() -> {
            try {
                byte[] wav = wavFromPcm16(pcm16, SAMPLE_RATE, 1);
                JSONObject json = baseRequestJson();
                json.put("audioFormat", "wav");
                json.put("sampleRate", SAMPLE_RATE);
                json.put("channels", 1);
                json.put("bitsPerSample", 16);
                json.put("audioBase64", Base64.encodeToString(wav, Base64.NO_WRAP));
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
                String transcript = json.optString("transcript", "");
                String spoken = json.optString("spokenText", "");
                String starOutput = json.optString("starOutput", spoken);
                String header = title;
                if (transcript.length() > 0) header += "\nTranscript: " + transcript;
                responseText.setText(header + "\n\n" + (starOutput.length() > 0 ? starOutput : spoken));
                setDebug(title, body);
            } catch (Exception ex) {
                responseText.setText(body);
                setDebug(title + " (raw)", body);
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
            while ((read = inputStream.read(chunk)) >= 0) {
                buffer.write(chunk, 0, read);
            }
        }
        String body = buffer.toString("UTF-8");
        if (code < 200 || code >= 300) {
            throw new RuntimeException("HTTP " + code + ": " + body);
        }
        return body;
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

    private void setDebug(String title, String details) {
        String now = new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date());
        debugText.setText("[" + now + "] " + title + "\n" + details);
    }
}
