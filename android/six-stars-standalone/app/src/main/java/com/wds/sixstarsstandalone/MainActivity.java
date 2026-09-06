package com.wds.sixstarsstandalone;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.drawable.ColorDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.LayoutInflater;
import android.view.View;
import android.view.Window;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.File;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Standalone companion shell using the exact production layout from six-stars-mobile.
 * The service/runtime is local-to-device; only provider calls leave the phone.
 */
public final class MainActivity extends Activity {
    private static final int RECORD_AUDIO_REQUEST = 41;
    private static final String PREFS_NAME = "SixStarsStandalonePrefs";
    private static final String PREF_ECHO_MIC_WHILE_RECORDING = "echoMicWhileRecording";
    private final ExecutorService initExecutor = Executors.newSingleThreadExecutor();

    private StandaloneRuntime runtime;
    private VoiceCaptureService voiceCapture;
    private String selectedStarId = "Elusiaa";
    private boolean recording;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private StarAnimationCoordinator starAnimationCoordinator;
    private String activePlaybackStarId = "";

    private TextView starNameText;
    private TextView starSubtitleText;
    private TextView relationshipText;
    private TextView preparedIndicatorText;
    private TextView talkText;
    private TextView conversationText;
    private ImageView talkIcon;
    private PixelStarAnimationView spriteView;
    private ScrollView conversationScroll;
    private EditText messageEdit;
    private View talkButton;
    private View listenButton;
    private View listenSubmenu;
    private View photoButton;
    private View voiceActions;
    private Button cancelVoiceButton;
    private Button sendVoiceButton;
    private View textToggleButton;
    private View textComposer;
    private Button sendTextButton;

    private final Map<String, StringBuilder> sessionConversationByStar = new HashMap<>();
    private final Map<String, Integer> assistantContentStartByStar = new HashMap<>();

    private final StandaloneRuntime.Callbacks callbacks = new StandaloneRuntime.Callbacks() {
        @Override public void onStatus(String text) {
            ui(() -> setPreparedStatus(text));
        }

        @Override public void onUserText(String starId, String text) {
            ui(() -> appendConversation(starId, "You", text));
        }

        @Override public void onAssistantStart(String starId) {
            ui(() -> appendConversationPrefix(starId, displayName(starId)));
        }

        @Override public void onAssistantDelta(String starId, String delta) {
            ui(() -> appendConversationDelta(starId, delta));
        }

        @Override public void onAssistantComplete(String starId, String text) {
            ui(() -> finishAssistantConversation(starId, text));
        }

        @Override public void onStateChanged(String starId, JSONObject snapshot) {
            if (starId.equalsIgnoreCase(selectedStarId)) ui(() -> showState(snapshot));
        }

        @Override public void onError(String operation, Throwable error) {
            ui(() -> {
                String message = operation + " failed: " + friendly(error);
                setPreparedStatus(message);
                Toast.makeText(MainActivity.this, operation + " failed", Toast.LENGTH_LONG).show();
                if (runtime != null) runtime.trace().log("ui.error", message);
            });
        }
    };

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        bindProductionViews();
        starAnimationCoordinator = new StarAnimationCoordinator(mainHandler, this::setStarAnimation);
        wireProductionActions();
        updateRecordingUi();
        refreshConversationSurface();
        setStarAnimation("idle");
        initExecutor.submit(this::initializeRuntime);
    }

    private void bindProductionViews() {
        starNameText = findViewById(R.id.prod_star_name);
        starSubtitleText = findViewById(R.id.prod_star_subtitle);
        relationshipText = findViewById(R.id.prod_relationship);
        preparedIndicatorText = findViewById(R.id.prod_prepared_speech_indicator);
        talkText = findViewById(R.id.prod_talk_text);
        talkIcon = findViewById(R.id.prod_talk_icon);
        spriteView = findViewById(R.id.prod_sprite_view);
        talkButton = findViewById(R.id.prod_talk_button);
        listenButton = findViewById(R.id.prod_listen_button);
        listenSubmenu = findViewById(R.id.prod_listen_submenu);
        photoButton = findViewById(R.id.prod_photo_button);
        voiceActions = findViewById(R.id.prod_voice_actions);
        cancelVoiceButton = findViewById(R.id.prod_cancel_voice_button);
        sendVoiceButton = findViewById(R.id.prod_send_voice_button);
        textToggleButton = findViewById(R.id.prod_text_toggle_button);
        textComposer = findViewById(R.id.prod_text_composer);
        messageEdit = findViewById(R.id.prod_message_edit);
        sendTextButton = findViewById(R.id.prod_send_text_button);
        conversationScroll = findViewById(R.id.prod_conversation_scroll);
        conversationText = findViewById(R.id.prod_conversation_text);
        conversationText.setTextIsSelectable(true);
    }

    private void wireProductionActions() {
        View titlePanel = findViewById(R.id.prod_star_title_panel);
        View avatarFrame = findViewById(R.id.prod_avatar_frame);
        View catAvatar = findViewById(R.id.prod_cat_avatar);
        View stage = findViewById(R.id.prod_stage);

        View.OnClickListener chooseStar = v -> showStarSelectionDialog();
        titlePanel.setOnClickListener(chooseStar);
        starNameText.setOnClickListener(chooseStar);
        starSubtitleText.setOnClickListener(chooseStar);
        stage.setOnClickListener(chooseStar);
        spriteView.setOnClickListener(chooseStar);
        avatarFrame.setOnClickListener(v -> showCompanionConsole());
        catAvatar.setOnClickListener(v -> showCompanionConsole());

        talkButton.setOnClickListener(v -> {
            if (recording) sendVoiceRecording(); else toggleTalk();
        });
        cancelVoiceButton.setOnClickListener(v -> cancelVoiceRecording());
        sendVoiceButton.setOnClickListener(v -> sendVoiceRecording());
        textToggleButton.setOnClickListener(v -> toggleTextComposer());
        sendTextButton.setOnClickListener(v -> sendTyped());

        listenButton.setOnClickListener(v -> {
            boolean show = listenSubmenu.getVisibility() != View.VISIBLE;
            listenSubmenu.setVisibility(show ? View.VISIBLE : View.GONE);
        });
        findViewById(R.id.prod_listen_thought_option).setOnClickListener(v -> {
            listenSubmenu.setVisibility(View.GONE);
            Toast.makeText(this, "Standalone spontaneous Thought is not ported yet.", Toast.LENGTH_SHORT).show();
        });
        findViewById(R.id.prod_listen_spark_option).setOnClickListener(v -> {
            listenSubmenu.setVisibility(View.GONE);
            if (runtime != null) runtime.playRoughSpark(selectedStarId, callbacks);
        });
        findViewById(R.id.prod_listen_constellation_option).setOnClickListener(v -> {
            listenSubmenu.setVisibility(View.GONE);
            if (runtime != null) runtime.playRoughStory(selectedStarId, callbacks);
        });
        photoButton.setOnClickListener(v -> Toast.makeText(this, "Photo conversation will be ported after the core voice path is stable.", Toast.LENGTH_SHORT).show());
    }

    private void initializeRuntime() {
        try {
            StandaloneRuntime r = new StandaloneRuntime(this);
            VoiceCaptureService capture = new VoiceCaptureService(this);
            capture.setMonitorEnabled(getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getBoolean(PREF_ECHO_MIC_WHILE_RECORDING, false));
            runtime = r;
            voiceCapture = capture;
            r.setPlaybackObserver(new StandaloneRuntime.PlaybackObserver() {
                @Override public void onPlaybackStarted(String starId, long durationMs, long playbackElapsedMs, int[] speechActivitySegmentsMs) {
                    ui(() -> {
                        activePlaybackStarId = starId == null ? "" : starId;
                        if (selectedStarId.equalsIgnoreCase(activePlaybackStarId) && starAnimationCoordinator != null) {
                            starAnimationCoordinator.beginVoice(durationMs, speechActivitySegmentsMs, playbackElapsedMs);
                        }
                    });
                }
                @Override public void onPlaybackFinished(String starId, boolean completedNormally) {
                    ui(() -> {
                        if (starId != null && starId.equalsIgnoreCase(activePlaybackStarId)) activePlaybackStarId = "";
                        if (selectedStarId.equalsIgnoreCase(starId) && starAnimationCoordinator != null) starAnimationCoordinator.endVoice();
                    });
                }
            });
            r.onAppResumed();
            ui(() -> {
                selectStar(selectedStarId);
                setPreparedStatus("Standalone ready • " + r.settings().configurationSummary());
            });
        } catch (Throwable e) {
            ui(() -> setPreparedStatus("Standalone runtime initialization failed: " + friendly(e)));
        }
    }

    private void selectStar(String id) {
        selectedStarId = id;
        starNameText.setText(displayName(id));
        starSubtitleText.setText(starSubtitle(id));
        if (starAnimationCoordinator != null) starAnimationCoordinator.cancel();
        else setStarAnimation("idle");
        refreshConversationSurface();
        if (runtime == null) {
            relationshipText.setText("♥ Heart 0   Trust 0   Romance 0   0.0%");
            return;
        }
        try {
            showState(runtime.stateSnapshot(id));
        } catch (Exception e) {
            relationshipText.setText("State error: " + friendly(e));
        }
    }

    private void showState(JSONObject state) {
        String suffix = state.optBoolean("IsGirlfriend", false) ? "   ♥ Girlfriend"
                : state.optBoolean("IsGirlfriendReady", false) ? "   ♥ Ready" : "";
        relationshipText.setText("♥ Heart " + state.optInt("Heart", 0)
                + "   Trust " + state.optInt("Trust", 0)
                + "   Romance " + state.optInt("Romance", 0)
                + "   " + String.format(Locale.US, "%.1f", state.optDouble("EmotionalConnectionPercent", 0.0)) + "%" + suffix);
    }

    private String starSubtitle(String id) {
        if (runtime != null) {
            try {
                JSONObject p = runtime.profiles().get(id);
                String element = p.optString("element", "Star");
                String domain = p.optString("domain", "Companion");
                return element + " Star • " + domain;
            } catch (Exception ignored) { }
        }
        switch (id.toLowerCase(Locale.ROOT)) {
            case "kaelia": return "Void Star • Mystery";
            case "maera": return "Blood Star • Possession";
            case "khavia": return "Water Star • Community";
            case "auerlis": return "Fire Star • Zeal";
            case "nyxari": return "Shadow Star • Solitude";
            default: return "Light Star • Mercy";
        }
    }

    private void toggleTextComposer() {
        boolean show = textComposer.getVisibility() != View.VISIBLE;
        textComposer.setVisibility(show ? View.VISIBLE : View.GONE);
        if (show) messageEdit.requestFocus();
    }

    private void sendTyped() {
        StandaloneRuntime r = runtime;
        if (r == null) return;
        String text = messageEdit.getText().toString().trim();
        if (text.isEmpty()) {
            Toast.makeText(this, "Type a message first.", Toast.LENGTH_SHORT).show();
            return;
        }
        messageEdit.setText("");
        r.submitText(selectedStarId, text, callbacks);
    }

    private void toggleTalk() {
        if (runtime == null || voiceCapture == null) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, RECORD_AUDIO_REQUEST);
            return;
        }
        startTalk();
    }

    private void startTalk() {
        if (recording || voiceCapture == null) return;
        try {
            voiceCapture.start();
            recording = true;
            updateRecordingUi();
            setPreparedStatus("Recording locally... press Send Voice or Cancel.");
            if (runtime != null) runtime.trace().log("voice.recording-start", "star=" + selectedStarId);
        } catch (Exception e) {
            recording = false;
            updateRecordingUi();
            callbacks.onError("Microphone", e);
        }
    }

    private void cancelVoiceRecording() {
        if (!recording || voiceCapture == null) return;
        try {
            File wav = voiceCapture.stop();
            if (wav != null) wav.delete();
            if (runtime != null) runtime.trace().log("voice.recording-cancel", "star=" + selectedStarId);
            setPreparedStatus("Voice cancelled.");
        } catch (Exception e) {
            callbacks.onError("Cancel voice", e);
        } finally {
            recording = false;
            updateRecordingUi();
        }
    }

    private void sendVoiceRecording() {
        if (!recording || voiceCapture == null || runtime == null) return;
        try {
            File wav = voiceCapture.stop();
            recording = false;
            updateRecordingUi();
            if (wav == null || wav.length() <= 44) {
                if (wav != null) wav.delete();
                setPreparedStatus("No microphone audio was captured.");
                return;
            }
            runtime.trace().log("voice.recording-send", "star=" + selectedStarId + " bytes=" + wav.length());
            runtime.transcribeAndSubmit(selectedStarId, wav, callbacks);
        } catch (Exception e) {
            recording = false;
            updateRecordingUi();
            callbacks.onError("Voice", e);
        }
    }

    private void updateRecordingUi() {
        if (talkText != null) talkText.setText(recording ? "Send Voice" : "Talk");
        if (voiceActions != null) voiceActions.setVisibility(recording ? View.VISIBLE : View.GONE);
        if (talkIcon != null) talkIcon.setImageResource(recording ? R.drawable.ui_send_pixel : R.drawable.ui_mic_pixel);
    }

    @Override public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RECORD_AUDIO_REQUEST) return;
        if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) startTalk();
        else Toast.makeText(this, "Microphone permission is required for Talk.", Toast.LENGTH_LONG).show();
    }

    private void setStarAnimation(String state) {
        if (spriteView == null) return;
        spriteView.setStarAnimation(selectedStarId, state, fallbackFrames(selectedStarId));
    }

    private int[] fallbackFrames(String id) {
        switch (id.toLowerCase(Locale.ROOT)) {
            case "kaelia": return new int[]{R.drawable.star_kaelia_idle_0};
            case "maera": return new int[]{R.drawable.star_maera_idle_0};
            case "khavia": return new int[]{R.drawable.star_khavia_idle_0};
            case "auerlis": return new int[]{R.drawable.star_auerlis_idle_0};
            case "nyxari": return new int[]{R.drawable.star_nyxari_idle_0};
            default: return new int[]{R.drawable.star_elusiaa_idle_0};
        }
    }

    private void showStarSelectionDialog() {
        View view = LayoutInflater.from(this).inflate(R.layout.dialog_star_selection, null, false);
        AlertDialog dialog = new AlertDialog.Builder(this).setView(view).setNegativeButton("Close", null).create();
        bindStarChoice(view, R.id.select_elusiaa, "Elusiaa", dialog);
        bindStarChoice(view, R.id.select_kaelia, "Kaelia", dialog);
        bindStarChoice(view, R.id.select_maera, "Maera", dialog);
        bindStarChoice(view, R.id.select_khavia, "Khavia", dialog);
        bindStarChoice(view, R.id.select_auerlis, "Auerlis", dialog);
        bindStarChoice(view, R.id.select_nyxari, "Nyxari", dialog);
        dialog.setOnShowListener(x -> styleDialog(dialog));
        dialog.show();
    }

    private void bindStarChoice(View root, int id, String starId, AlertDialog dialog) {
        root.findViewById(id).setOnClickListener(v -> {
            selectStar(starId);
            dialog.dismiss();
        });
    }

    private void showCompanionConsole() {
        View view = LayoutInflater.from(this).inflate(R.layout.dialog_companion_console, null, false);
        TextView summary = view.findViewById(R.id.console_summary);
        TextView log = view.findViewById(R.id.console_log);
        ScrollView logScroll = view.findViewById(R.id.console_log_scroll);
        CheckBox echoMic = view.findViewById(R.id.console_echo_mic);
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        echoMic.setChecked(prefs.getBoolean(PREF_ECHO_MIC_WHILE_RECORDING, false));
        echoMic.setOnCheckedChangeListener((buttonView, isChecked) -> {
            prefs.edit().putBoolean(PREF_ECHO_MIC_WHILE_RECORDING, isChecked).apply();
            if (voiceCapture != null) voiceCapture.setMonitorEnabled(isChecked);
            refreshConsole(summary, log, logScroll);
        });
        Runnable refresh = () -> refreshConsole(summary, log, logScroll);
        view.findViewById(R.id.console_refresh).setOnClickListener(v -> refresh.run());
        view.findViewById(R.id.console_retry_review).setOnClickListener(v -> {
            if (runtime == null) return;
            runtime.retryPendingPrimaryReviews(callbacks);
            refresh.run();
        });
        refresh.run();
        AlertDialog dialog = new AlertDialog.Builder(this).setView(view).setNegativeButton("Close", null).create();
        dialog.setOnShowListener(x -> styleDialog(dialog));
        dialog.show();
    }

    private void refreshConsole(TextView summary, TextView log, ScrollView scroll) {
        if (runtime == null) {
            summary.setText("Runtime is still starting.");
            log.setText("No runtime trace yet.");
            return;
        }
        String text = "Star: " + selectedStarId + "\n"
                + runtime.settings().configurationSummary() + "\n\n"
                + "State: " + runtime.stateStore().file().getAbsolutePath() + "\n"
                + "Content: " + runtime.content().root().getAbsolutePath() + "\n"
                + "Trace: " + runtime.trace().fileForStar(selectedStarId).getAbsolutePath() + "\n"
                + "Pending primary review: " + runtime.hasPendingPrimaryReview() + "\n"
                + "Microphone echo while recording: " + (getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getBoolean(PREF_ECHO_MIC_WHILE_RECORDING, false) ? "enabled" : "disabled");
        summary.setText(text);
        log.setText(runtime.trace().readTailForStar(selectedStarId, 96 * 1024));
        scroll.post(() -> scroll.fullScroll(View.FOCUS_DOWN));
    }

    private void styleDialog(AlertDialog dialog) {
        Window window = dialog.getWindow();
        if (window != null) window.setBackgroundDrawable(new ColorDrawable(0xF00B1220));
        Button negative = dialog.getButton(AlertDialog.BUTTON_NEGATIVE);
        if (negative != null) negative.setTextColor(0xFFEAF7FF);
    }

    private StringBuilder conversationFor(String starId) {
        String key = starId == null || starId.trim().isEmpty() ? selectedStarId : starId;
        return sessionConversationByStar.computeIfAbsent(key, ignored -> new StringBuilder());
    }

    private void appendConversation(String starId, String speaker, String text) {
        StringBuilder conversation = conversationFor(starId);
        if (conversation.length() > 0) conversation.append("\n\n");
        conversation.append(speaker).append(": ").append(text == null ? "" : SpeechTextFormatter.normalize(text));
        if (starId != null && starId.equalsIgnoreCase(selectedStarId)) refreshConversationSurface();
    }

    private void appendConversationPrefix(String starId, String speaker) {
        StringBuilder conversation = conversationFor(starId);
        if (conversation.length() > 0) conversation.append("\n\n");
        conversation.append(speaker).append(": ");
        assistantContentStartByStar.put(starId, conversation.length());
        if (starId != null && starId.equalsIgnoreCase(selectedStarId)) refreshConversationSurface();
    }

    private void appendConversationDelta(String starId, String delta) {
        if (delta == null || delta.isEmpty()) return;
        conversationFor(starId).append(delta);
        if (starId != null && starId.equalsIgnoreCase(selectedStarId)) refreshConversationSurface();
    }

    private void finishAssistantConversation(String starId, String finalText) {
        StringBuilder conversation = conversationFor(starId);
        Integer contentStart = assistantContentStartByStar.remove(starId);
        String clean = SpeechTextFormatter.normalize(finalText);
        if (contentStart != null && contentStart >= 0 && contentStart <= conversation.length()) {
            conversation.replace(contentStart, conversation.length(), clean);
        } else if (!clean.isEmpty()) {
            if (conversation.length() > 0) conversation.append("\n\n");
            conversation.append(displayName(starId)).append(": ").append(clean);
        }
        if (starId != null && starId.equalsIgnoreCase(selectedStarId)) refreshConversationSurface();
    }

    private void refreshConversationSurface() {
        if (conversationText == null) return;
        StringBuilder conversation = conversationFor(selectedStarId);
        conversationText.setText(conversation.length() == 0 ? "No conversation yet." : conversation.toString());
        conversationScroll.post(() -> conversationScroll.fullScroll(View.FOCUS_DOWN));
    }

    private void setPreparedStatus(String text) {
        if (preparedIndicatorText == null) return;
        String value = text == null ? "" : text.trim();
        preparedIndicatorText.setText(value);
        preparedIndicatorText.setVisibility(value.isEmpty() ? View.GONE : View.VISIBLE);
    }

    private String displayName(String id) {
        try { return runtime == null ? id : runtime.profiles().displayName(id); }
        catch (Exception e) { return id; }
    }

    private void ui(Runnable r) { runOnUiThread(r); }

    private static String friendly(Throwable e) {
        if (e == null) return "unknown error";
        Throwable x = e;
        while (x.getCause() != null && x.getCause() != x) x = x.getCause();
        String m = x.getMessage();
        return x.getClass().getSimpleName() + (m == null ? "" : ": " + m);
    }

    @Override protected void onResume() {
        super.onResume();
        StandaloneRuntime r = runtime;
        if (r != null) r.onAppResumed();
    }

    @Override protected void onDestroy() {
        super.onDestroy();
        try {
            if (voiceCapture != null && voiceCapture.isRecording()) {
                File wav = voiceCapture.stop();
                if (wav != null) wav.delete();
            }
        } catch (Exception ignored) { }
        if (runtime != null) runtime.close();
        initExecutor.shutdownNow();
    }
}
