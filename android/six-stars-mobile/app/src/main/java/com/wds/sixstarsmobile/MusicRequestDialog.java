package com.wds.sixstarsmobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.text.InputType;
import android.widget.*;
import org.json.JSONObject;
import java.io.*;
import java.net.HttpURLConnection;
import java.util.concurrent.*;

/** A text music request to the existing desktop bridge; successful PCM uses the voice queue. */
final class MusicRequestDialog implements AutoCloseable {
    private final Activity activity;
    private final String bridge;
    private final PhoneVoicePlaybackService audio;
    private volatile MusicStreamClient musicClient;
    private final VoiceCaptureService capture;
    private final boolean echo;
    private boolean ownsRecording;
    private Button speech, submit, cancelVoice, sendVoice;
    private LinearLayout recordingActions;
    private int recordingEpoch;
    private final android.os.Handler timer = new android.os.Handler(android.os.Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private volatile HttpURLConnection connection;
    private volatile boolean closed;
    private AlertDialog dialog;
    private Future<?> pending;
    MusicRequestDialog(Activity activity, String bridge, VoiceCaptureService capture, boolean echo, PhoneVoicePlaybackService audio) {
        this.activity = activity; this.bridge = bridge; this.audio = audio; this.capture = capture; this.echo = echo;
    }
    void show() {
        LinearLayout form = new LinearLayout(activity); form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(20), dp(20), dp(20), dp(16));
        TextView eyebrow = text("THE SIX STARS  /  MUSIC", 11, 0xFF00D5D7);
        form.addView(eyebrow);
        TextView title = text("♫  Ask for Music", 24, 0xFFEAF7FF); title.setPadding(0, dp(8), 0, dp(8)); form.addView(title);
        TextView subtitle = text("Give a feeling a melody.", 13, 0xFFB6B3D4); form.addView(subtitle);
        EditText brief = field(form, "YOUR MUSICAL IDEA", "");
        brief.setHint("A warm hello, a tender moment, then something epic…");
        brief.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE);
        brief.setMinLines(3); brief.setMaxLines(6); brief.setGravity(android.view.Gravity.TOP);
        LinearLayout settings = new LinearLayout(activity); form.addView(settings);
        LinearLayout barsColumn = column(settings, true), tempoColumn = column(settings, true), meterColumn = column(settings, false);
        EditText bars = field(barsColumn, "BARS", "8"); bars.setInputType(InputType.TYPE_CLASS_NUMBER); bars.setSingleLine(true);
        EditText tempo = field(tempoColumn, "TEMPO", "96"); tempo.setInputType(InputType.TYPE_CLASS_NUMBER); tempo.setSingleLine(true);
        EditText meterInput = field(meterColumn, "METER", "4/4"); meterInput.setSingleLine(true);
        meterInput.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        meterInput.setContentDescription("Meter, numerator slash denominator");
        CheckBox ahd = new CheckBox(activity); ahd.setText("Anchored harmonic divergence"); ahd.setChecked(true);
        ahd.setTextColor(0xFFD7D4FF); ahd.setTextSize(12); ahd.setTypeface(android.graphics.Typeface.MONOSPACE);
        ahd.setButtonTintList(android.content.res.ColorStateList.valueOf(0xFF00D5D7));
        ahd.setPadding(0, dp(12), 0, dp(8)); form.addView(ahd);
        TextView status = text("Type your idea or record it below.", 12, 0xFFB6E6F2);
        status.setAccessibilityLiveRegion(android.view.View.ACCESSIBILITY_LIVE_REGION_POLITE);
        status.setPadding(0, dp(6), 0, dp(12)); form.addView(status);
        speech = button("●  Voice input", false); form.addView(speech);
        speech.setContentDescription("Record a music request");
        speech.setOnClickListener(v -> toggleSpeech(brief, status));
        recordingActions = new LinearLayout(activity); recordingActions.setVisibility(android.view.View.GONE); form.addView(recordingActions);
        cancelVoice = button("Cancel", false); sendVoice = button("Send Voice", true);
        LinearLayout.LayoutParams left = new LinearLayout.LayoutParams(0, dp(48), 1); left.setMarginEnd(dp(8));
        recordingActions.addView(cancelVoice, left); recordingActions.addView(sendVoice, new LinearLayout.LayoutParams(0, dp(48), 1));
        cancelVoice.setOnClickListener(v -> cancelRecording(status));
        sendVoice.setOnClickListener(v -> toggleSpeech(brief, status));
        LinearLayout footer = new LinearLayout(activity); footer.setPadding(0, dp(16), 0, 0); form.addView(footer);
        Button close = button("Close", false); submit = button("Create Music", true);
        footer.addView(close, left); footer.addView(submit, new LinearLayout.LayoutParams(0, dp(48), 1));
        close.setOnClickListener(v -> close());
        ScrollView scroll = new ScrollView(activity); scroll.setFillViewport(true); scroll.addView(form);
        dialog = new AlertDialog.Builder(activity).setView(scroll).create();
        dialog.setOnDismissListener(d -> close()); dialog.show();
        if (dialog.getWindow() != null) {
            dialog.getWindow().setBackgroundDrawable(panel(0xFF0B1220, 0xFF00D5D7, 18));
            dialog.getWindow().setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
            int width = Math.min(activity.getResources().getDisplayMetrics().widthPixels - dp(24), dp(480));
            dialog.getWindow().setLayout(width, android.view.WindowManager.LayoutParams.WRAP_CONTENT);
        }
        submit.setOnClickListener(v -> {
            try {
                String description = brief.getText().toString().trim();
                int count = Integer.parseInt(bars.getText().toString().trim()); int bpm = Integer.parseInt(tempo.getText().toString().trim());
                if (description.isEmpty() || description.length() > 12000) throw new IllegalArgumentException("Enter a description (up to 12,000 characters).");
                if (count < 1 || count > 64 || bpm < 30 || bpm > 240) throw new IllegalArgumentException("Use 1–64 bars and 30–240 BPM.");
                String meter = meterInput.getText().toString().trim();
                if (!meter.matches("(?:[1-9]|1[0-2])/(?:2|4|8|16)")) throw new IllegalArgumentException("Meter must use 1–12 over 2, 4, 8 or 16, such as 4/4 or 6/8.");
                String[] parts = meter.split("/");
                double beats = count * Integer.parseInt(parts[0]) * 4.0 / Integer.parseInt(parts[1]);
                if (beats * 60 / bpm > 175) throw new IllegalArgumentException("Choose a duration under three minutes for phone preview.");
                JSONObject body = new JSONObject().put("description", description).put("bars", count).put("tempo", bpm).put("meter", meter).put("useAhd", ahd.isChecked());
                submit.setEnabled(false); speech.setEnabled(false);
                status.setText("Composing " + beats + " beats, then rendering piano…");
                pending = worker.submit(() -> request(body, status, null));
            } catch (NumberFormatException error) { status.setText("Enter whole numbers for bars and tempo."); }
            catch (Exception error) { status.setText(error.getMessage() == null ? "Check the music settings." : error.getMessage()); }
        });
    }
    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }
    private android.graphics.drawable.GradientDrawable panel(int fill, int border, int radius) {
        android.graphics.drawable.GradientDrawable shape = new android.graphics.drawable.GradientDrawable();
        shape.setColor(fill); shape.setStroke(dp(1), border); shape.setCornerRadius(dp(radius)); return shape;
    }
    private TextView text(String value, int size, int color) {
        TextView view = new TextView(activity); view.setText(value); view.setTextSize(size); view.setTextColor(color);
        view.setTypeface(android.graphics.Typeface.MONOSPACE); return view;
    }
    private LinearLayout column(LinearLayout parent, boolean gap) {
        LinearLayout column = new LinearLayout(activity); column.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        if (gap) params.setMarginEnd(dp(8)); parent.addView(column, params); return column;
    }
    private Button button(String label, boolean primary) {
        Button button = new Button(activity); button.setText(label); button.setAllCaps(false); button.setTextSize(13);
        button.setTypeface(android.graphics.Typeface.MONOSPACE, android.graphics.Typeface.BOLD);
        button.setTextColor(new android.content.res.ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{0xFF617785, primary ? 0xFF9FFAFF : 0xFFD7D4FF}));
        button.setBackground(new android.graphics.drawable.RippleDrawable(android.content.res.ColorStateList.valueOf(0x4430E3E8), panel(primary ? 0xFF153B48 : 0xFF172033, primary ? 0xFF00D5D7 : 0xFF566180, 12), null));
        button.setMinimumHeight(dp(48)); button.setPadding(dp(8), 0, dp(8), 0);
        button.setLayoutParams(new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(48))); return button;
    }
    private EditText field(LinearLayout form, String label, String value) {
        TextView caption = text(label, 10, 0xFF9EE8F0); caption.setPadding(0, dp(16), 0, dp(7)); form.addView(caption);
        EditText edit = new EditText(activity); edit.setText(value); edit.setTextColor(0xFFEAF7FF); edit.setHintTextColor(0xFF8D9CB1);
        edit.setTextSize(15); edit.setTypeface(android.graphics.Typeface.MONOSPACE);
        edit.setBackground(panel(0xFF172033, 0xFF566180, 10)); edit.setPadding(dp(12), dp(12), dp(12), dp(12));
        edit.setMinimumHeight(dp(48)); form.addView(edit, new LinearLayout.LayoutParams(-1, -2)); return edit;
    }
    private void recordingControls(boolean recording) {
        speech.setVisibility(recording ? android.view.View.GONE : android.view.View.VISIBLE);
        recordingActions.setVisibility(recording ? android.view.View.VISIBLE : android.view.View.GONE);
    }
    private void cancelRecording(TextView status) {
        if (!ownsRecording || capture == null) return;
        ownsRecording = false; recordingEpoch++; timer.removeCallbacksAndMessages(null);
        recordingControls(false); speech.setEnabled(false); status.setText("Discarding recording…");
        capture.stop().whenComplete((ignored, error) -> activity.runOnUiThread(() -> {
            if (closed) return;
            speech.setEnabled(true); submit.setEnabled(true); status.setText("Recording cancelled. Your typed idea is unchanged.");
        }));
    }
    private void request(JSONObject body, TextView status, EditText transcriptTarget) {
        if (transcriptTarget == null) {
            streamMusic(body, status);
            return;
        }
        HttpURLConnection local = null;
        try {
            if (closed) return;
            local = new LocalNetworkBindingService(activity).openHttpConnection(new java.net.URL(bridge.replaceAll("/+$", "") + (transcriptTarget == null ? "/api/mobile/music" : "/api/mobile/music/transcribe")));
            connection = local;
            if (closed) return;
            local.setConnectTimeout(15000); local.setReadTimeout(210000);
            local.setRequestMethod("POST"); local.setDoOutput(true); local.setRequestProperty("Content-Type", "application/json");
            try (OutputStream out = local.getOutputStream()) { out.write(body.toString().getBytes("UTF-8")); }
            int code = local.getResponseCode();
            byte[] data;
            try (InputStream in = code == 200 ? local.getInputStream() : local.getErrorStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                if (in == null) throw new IOException("Empty response from desktop.");
                byte[] buffer = new byte[8192]; int n;
                int limit = code == 200 ? 10 * 1024 * 1024 : 16000;
                while ((n = in.read(buffer)) != -1) {
                    if (closed) return;
                    if (out.size() + n > limit) throw new IOException("Desktop response exceeds preview limit.");
                    out.write(buffer, 0, n);
                }
                data = out.toByteArray();
            }
            if (code != 200) throw new IOException("HTTP " + code + ": " + new String(data, "UTF-8"));
            if (transcriptTarget != null) {
                String transcript = new JSONObject(new String(data, "UTF-8")).getString("transcript").trim();
                if (transcript.isEmpty()) throw new IOException("No speech was recognized. Please try again.");
                activity.runOnUiThread(() -> { if (!closed && !activity.isFinishing()) {
                    transcriptTarget.setText(transcript);
                    speech.setEnabled(true); submit.setEnabled(true);
                    // Use the same validation/settings/submission path as a typed request.
                    // This runs in the same UI callback, so there is no extra confirmation step.
                    submit.performClick();
                } });
                return;
            }

        } catch (Exception error) {
            activity.runOnUiThread(() -> { if (!closed && !activity.isFinishing()) { speech.setEnabled(true); status.setText("Music failed: " + error.getMessage()); submit.setEnabled(true); } });
        } finally { if (local != null) local.disconnect(); connection = null; }
    }
    private void streamMusic(JSONObject body, TextView status) {
        MusicStreamClient client = new MusicStreamClient(activity, bridge, audio);
        musicClient = client;
        try {
            if (closed) { client.close(); return; }
            client.run(body, message -> activity.runOnUiThread(() -> {
                if (!closed && !activity.isFinishing()) status.setText(message);
            }));
            activity.runOnUiThread(() -> { if (!closed && !activity.isFinishing()) dialog.dismiss(); });
        } catch (Exception error) {
            activity.runOnUiThread(() -> { if (!closed && !activity.isFinishing()) {
                speech.setEnabled(true); submit.setEnabled(true); status.setText("Music failed: " + error.getMessage());
            } });
        } finally { musicClient = null; }
    }

    private void toggleSpeech(EditText brief, TextView status) {
        if (capture == null) { status.setText("Microphone capture is unavailable."); return; }
        if (ownsRecording) {
            ownsRecording = false; timer.removeCallbacksAndMessages(null);
            recordingControls(false); speech.setEnabled(false);
            status.setText("Transcribing…");
            capture.stop().whenComplete((recording, error) -> activity.runOnUiThread(() -> {
                if (closed) return;
                if (error != null || recording == null || recording.wav.length < 1000) {
                    status.setText("No recording captured. Try again."); speech.setEnabled(true); submit.setEnabled(true); return;
                }
                try {
                    JSONObject body = new JSONObject().put("audioBase64", android.util.Base64.encodeToString(recording.wav, android.util.Base64.NO_WRAP));
                    pending = worker.submit(() -> request(body, status, brief));
                } catch (Exception e) { status.setText(e.getMessage()); speech.setEnabled(true); submit.setEnabled(true); }
            }));
            return;
        }
        if (!capture.hasPermission()) {
            activity.requestPermissions(new String[] {android.Manifest.permission.RECORD_AUDIO}, 4821);
            status.setText("Allow microphone access, then tap Speech input again."); return;
        }
        if (capture.isRecording()) { status.setText("Finish the other recording first."); return; }
        try {
            capture.setMonitorEnabled(echo); // Exactly the main Talk button's live mic echo setting.
            final int epoch = ++recordingEpoch;
            capture.start(new VoiceCaptureService.Listener() {
                public void onStarted() { }
                public void onLevel(double percent) { }
                public void onStopped() { }
                public void onError(Throwable error) { activity.runOnUiThread(() -> { if (!closed && epoch == recordingEpoch) {
                    ownsRecording = false; timer.removeCallbacksAndMessages(null); recordingControls(false);
                    speech.setEnabled(true); submit.setEnabled(true); status.setText("Recording failed: " + error.getMessage());
                } }); }
            });
            ownsRecording = true; recordingControls(true); submit.setEnabled(false);
            status.setText("● Recording… Send Voice to use it, or Cancel to discard.");
            timer.postDelayed(() -> { if (!closed && ownsRecording) toggleSpeech(brief, status); }, 120000);
        } catch (Exception e) { status.setText(e.getMessage()); }
    }

    @Override public void close() {
        if (closed) return;
        closed = true;
        MusicStreamClient client = musicClient; if (client != null) client.close();
        timer.removeCallbacksAndMessages(null);
        if (ownsRecording && capture != null) { ownsRecording = false; capture.cancel(); }
        if (pending != null) pending.cancel(true);
        HttpURLConnection local = connection;
        // Disconnect off the main thread: an in-flight network read may hold connection locks.
        if (local != null) new Thread(local::disconnect, "cancel-music").start();
        worker.shutdownNow();
        if (dialog != null && dialog.isShowing()) dialog.dismiss();
    }
}
