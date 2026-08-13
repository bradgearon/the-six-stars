package com.wds.sixstarsmobile;

import android.Manifest;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.IntentFilter;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.content.pm.PackageManager;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTimestamp;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.net.Uri;
import android.provider.MediaStore;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.InputType;
import android.util.Base64;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.AdapterView;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.ImageView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import androidx.activity.ComponentActivity;
import androidx.activity.result.ActivityResult;
import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.core.content.ContextCompat;
import androidx.lifecycle.ViewModelProvider;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.FileInputStream;
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
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.TreeMap;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingDeque;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

public final class MainActivity extends ComponentActivity {
    private MainViewModel viewModel;
    private MobileSettingsService settingsService;
    private BridgeConnectionService bridgeConnectionService;
    private ConversationService conversationService;
    private VoiceCaptureService voiceCaptureService;
    private PhoneVoicePlaybackService phoneVoiceService;
    private PhotoAttachmentService photoAttachmentService;
    private PreparedSpeechCoordinator preparedSpeechCoordinator;
    private StarInitiativeCoordinator starInitiativeCoordinator;
    private StarAnimationCoordinator starAnimationCoordinator;
    private static final String PREF_BRIDGE_URL = "bridgeUrl";
    private static final String PREF_SELECTED_STAR_ID = "selectedStarId";
    private static final String PREF_PREPARED_LISTEN_NOT_BEFORE_MS = "preparedListenNotBeforeMs";
    private static final String DEFAULT_BRIDGE_URL = "http://192.168.1.179:8066";
    private static final String LEGACY_BRIDGE_URL = "http://192.168.1.217:8066";
    private static final String ACTION_STAR_INITIATIVE_SERVICE_RESPONSE = "com.wds.sixstarsmobile.STAR_INITIATIVE_SERVICE_RESPONSE";
    private static final String ACTION_STAR_INITIATIVE_SERVICE_STATUS = "com.wds.sixstarsmobile.STAR_INITIATIVE_SERVICE_STATUS";
    private static final String[] DEFAULT_STAR_IDS = new String[]{"Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari"};
    private final List<String> activeStarIds = new ArrayList<>(Arrays.asList(DEFAULT_STAR_IDS));
    private static final int SAMPLE_RATE = 16000;
    private static final int FRAME_STATUS = 1;
    private static final int FRAME_AUDIO_WAV = 2;
    private static final int FRAME_ERROR = 3;
    private static final String[] STANDARD_EMOTION_IDS = new String[]{
            "Happy", "Amused", "Playful", "Comforted", "Safe", "Protected", "Trusting", "Respected",
            "Admiring", "Affectionate", "Vulnerable", "Hopeful", "Wondrous", "Intrigued", "Impassioned",
            "Determined", "Concerned", "Confused", "Unsure", "Frustrated", "Angry", "Hurt", "Sad",
            "Melancholy", "Lonely", "Guilty", "Suspicious", "Flattered", "Attracted", "Tender", "Yearning", "Jealous"
    };
    private static final String[] NEGATIVE_EMOTION_IDS = new String[]{
            "Irritated", "Disrespected", "Pressured", "CreepedOut", "Bored", "Hurt", "Unsafe"
    };

    private static final int KEEP_AWAKE_REASON_REQUEST = 1;
    private static final int KEEP_AWAKE_REASON_VOICE_STREAM = 2;
    private static final int KEEP_AWAKE_REASON_AUDIO_PLAYBACK = 4;

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService httpExecutor = Executors.newFixedThreadPool(2);
    // Only media encoding uses these workers. Text, state, command, and initiative messages are
    // tiny non-blocking WebSocket sends and leave the UI thread immediately without another queue.
    private final ExecutorService foregroundRequestExecutor = Executors.newFixedThreadPool(2);
    private boolean startRecordingAfterAudioPermission;
    private boolean openPhotoPickerAfterCameraPermission;
    // Referenced by the permission result callback below, so this field must be
    // declared before that callback's field initializer to avoid an illegal
    // forward reference in Java.
    private volatile boolean recording;

    private final ActivityResultLauncher<String> recordAudioPermissionLauncher = registerForActivityResult(
            new ActivityResultContracts.RequestPermission(),
            granted -> {
                boolean shouldStart = startRecordingAfterAudioPermission;
                startRecordingAfterAudioPermission = false;
                if (granted && shouldStart && !recording) {
                    startRecording();
                } else if (!granted && shouldStart) {
                    setDebug("Microphone permission denied", "RECORD_AUDIO is required to send a voice message.");
                }
            });

    private final ActivityResultLauncher<String> cameraPermissionLauncher = registerForActivityResult(
            new ActivityResultContracts.RequestPermission(),
            granted -> {
                boolean shouldOpen = openPhotoPickerAfterCameraPermission;
                openPhotoPickerAfterCameraPermission = false;
                if (shouldOpen) launchImageChooser(granted);
            });

    private final ActivityResultLauncher<Intent> imagePickerLauncher = registerForActivityResult(
            new ActivityResultContracts.StartActivityForResult(),
            this::handleImagePickerResult);

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
    private TextView emotionVectorsText;
    private TextView interstarStatusText;
    private TextView prodStarNameText;
    private TextView prodStarSubtitleText;
    private TextView prodRelationshipText;
    private TextView prodPreparedSpeechIndicatorText;
    private TextView prodMiniStatusText;
    private PixelStarAnimationView prodSpriteView;
    private TextView prodTalkButtonText;
    private ImageView prodTalkIcon;
    private View prodTalkButton;
    private View prodListenButton;
    private View prodListenSubmenu;
    private View prodListenThoughtOption;
    private View prodListenSparkOption;
    private View prodListenConstellationOption;
    private TextView prodConstellationChoiceIcon;
    private View prodPhotoButton;
    private View prodVoiceActions;
    private Button prodCancelVoiceButton;
    private Button prodSendVoiceButton;
    private Button prodSendTextButton;
    private Button prodGenerateWritingButton;
    private Button prodTellWritingButton;
    private CheckBox prodBackgroundWritingLoopCheck;
    private CheckBox prodXPostingCheck;
    private Button prodPostToXNowButton;
    private boolean desktopBackgroundWritingLoopEnabled;
    private boolean suppressBackgroundWritingLoopToggleDispatch;
    private boolean desktopXPostingEnabled;
    private boolean suppressXPostingToggleDispatch;
    private String desktopXPostingStatus = "Automatic X posting status has not been loaded.";
    private int selectedStarPendingWritingCount;
    private int selectedStarPersistedWritingCount;
    private int selectedStarUnpostedWritingCount;
    private boolean starWritingRequestRunning;
    private View prodTextToggleButton;
    private View prodTextComposer;
    private TextView prodConversationText;
    private ScrollView prodConversationScroll;
    private String prodAnimationState = "idle";
    private boolean starVoicePlaybackActive;
    private PhoneVoicePlaybackService.PhoneVoiceClip activeAnimatedVoiceClip;
    private boolean pendingShirtOffAnimation;
    private String pendingShirtOffStarId = "";
    private boolean shirtOffAnimationActive;
    private AlertDialog activeStarSelectionDialog;
    private boolean prodMode = true;
    private boolean starInitiativeEnabled;
    private boolean starInitiativeBackgroundEnabled;
    private long lastStarInitiativeRequestMs;
    private long lastManualListenRequestMs;
    private volatile PrefetchedInitiativeStore.CachedInitiative prefetchedInitiative;
    private volatile PreparedSpeechStore.SyncResult preparedSpeechSyncResult = new PreparedSpeechStore.SyncResult(new ArrayList<>(), 0, 0.0, "low");
    private volatile boolean preparedSpeechSyncRunning;
    private volatile boolean preparedSpeechPlaybackActive;
    private volatile PreparedSpeechStore.CachedItem activePreparedSpeechItem;
    private long lastPreparedSpeechSyncMs;
    private long selectedStarVisibleSinceMs = SystemClock.elapsedRealtime();
    private long lastPreparedAutoSpeakMs;
    private double selectedStarConnectionPercent;
    private volatile boolean activityResumed;
    private boolean starInitiativeServiceReceiverRegistered;
    private Button recordButton;
    private Button photoButton;
    private Button toggleMetButton;
    private Button toggleAllyButton;
    private Button clearHistoryButton;
    private Button clearAllDataButton;
    private TextView starStateText;
    private boolean suppressStarSelectionDispatch;
    private boolean selectedStarIsMet;
    private boolean selectedStarIsAlly;
    private boolean selectedStarIsGirlfriend;
    private boolean selectedStarCuteModeEnabled = true;
    private boolean selectedStarCuteModeActive;
    private String selectedStarCuteModeSignature = "";
    private String currentTopAffectedEmotions = "Top emotions: none yet.";
    private String currentLastEmotionVectors = "Last emotional vectors: none yet.";
    private String currentInterstarStatus = "Interstar state: idle.";
    private String currentDailyStarState = "Today: no daily Star state yet.";
    private long lastBridgeEventSequence;
    private boolean bridgeEventsPrimed;

    private volatile boolean destroyed;

    private final TreeMap<String, ArrayList<String>> prodConversationByStar = new TreeMap<>();
    private int keepScreenAwakeReasonMask;

    private final BroadcastReceiver starInitiativeServiceReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            handleStarInitiativeServiceIntent(intent);
        }
    };

    private final Runnable healthLoop = new Runnable() {
        @Override
        public void run() {
            if (destroyed) return;
            // OkHttp pings and reconnect callbacks own liveness. This low-frequency guard only
            // restarts a missing connection; it does not issue a maintenance request or poll state.
            ensureBridgeWebSocket();
            mainHandler.postDelayed(this, 60000L);
        }
    };

    private final Runnable preparedSpeechLoop = new Runnable() {
        @Override
        public void run() {
            if (destroyed) return;
            syncPreparedSpeech("periodic reconciliation", false);
            maybeAutoSpeakPreparedSpeech();
            mainHandler.postDelayed(this, 2500L);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        viewModel = new ViewModelProvider(this).get(MainViewModel.class);
        settingsService = viewModel.settings();
        bridgeConnectionService = viewModel.bridge();
        conversationService = viewModel.conversations();
        voiceCaptureService = viewModel.voiceCapture();
        photoAttachmentService = viewModel.photos();
        preparedSpeechCoordinator = viewModel.preparedSpeech();
        starInitiativeCoordinator = viewModel.initiatives();
        starAnimationCoordinator = viewModel.animations();
        prodMode = settingsService.isProductionMode();
        starInitiativeEnabled = settingsService.isStarInitiativeEnabled();
        starInitiativeBackgroundEnabled = starInitiativeEnabled;
        buildUi();
        // Restore the durable phone selection before syncStarInitiativeRuntimePrefs() copies the
        // spinner value back into preferences. Without this, a newly built spinner defaults to the
        // first Star and silently overwrites the saved selection during startup.
        selectStarInSpinner(settingsService.selectedStarId());
        initializeViewModelServices();
        registerStarInitiativeServiceReceiver();
        syncStarInitiativeRuntimePrefs();
        requestAudioPermissionIfNeeded();
        loadPrefetchedInitiativeIntoMemory("Android activity created");
        syncStarInitiativeBackgroundService("Android activity started", true);
        mainHandler.postDelayed(healthLoop, 600);
        mainHandler.postDelayed(preparedSpeechLoop, 1200L);
    }

    private void initializeViewModelServices() {
        starAnimationCoordinator.setListener(this::setProdAnimationState);
        phoneVoiceService = viewModel.phoneVoice();
        phoneVoiceService.setListener(new PhoneVoicePlaybackService.Listener() {
            @Override public void onSocketStatus(String message) {
                runOnUiThread(() -> {
                    if (audioStatusText != null) audioStatusText.setText(message);
                    appendVoiceDebug("Phone voice socket", message);
                });
            }

            @Override public void onDebug(String title, String details) {
                runOnUiThread(() -> appendVoiceDebug(title, details));
            }

            @Override public void onError(String title, Throwable error) {
                runOnUiThread(() -> {
                    if (audioStatusText != null) audioStatusText.setText(title + ": " + (error == null ? "unknown error" : error.getMessage()));
                    appendVoiceDebug(title, error == null ? "unknown error" : error.toString());
                });
            }

            @Override public void onStreamState(boolean active, String starId, String action) {
                runOnUiThread(() -> {
                    setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, active,
                            active ? (starId + " voice stream is active.") : ("Voice stream " + action + "."));
                    if (active) {
                        starAnimationCoordinator.thinking();
                    } else if (!starVoicePlaybackActive) {
                        starAnimationCoordinator.idle();
                        maybeResumePreparedListenAfterAudio("Desktop voice stream ended.");
                    }
                    if (viewModel != null) viewModel.setVoicePlaying(active || starVoicePlaybackActive);
                });
            }

            @Override public void onPlaybackStarted(PhoneVoicePlaybackService.PhoneVoiceClip clip, long durationMs, long playbackElapsedMs) {
                runOnUiThread(() -> {
                    starVoicePlaybackActive = true;
                    activeAnimatedVoiceClip = clip;
                    setKeepScreenAwake(KEEP_AWAKE_REASON_AUDIO_PLAYBACK, true, clip.label);
                    startStarVoiceAnimationMarkers(clip, durationMs, playbackElapsedMs);
                    if (viewModel != null) viewModel.setVoicePlaying(true);
                });
            }

            @Override public void onPlaybackFinished(PhoneVoicePlaybackService.PhoneVoiceClip clip, boolean completedNormally) {
                runOnUiThread(() -> {
                    finishStarVoiceAnimationMarkers(clip);
                    starVoicePlaybackActive = false;
                    setKeepScreenAwake(KEEP_AWAKE_REASON_AUDIO_PLAYBACK, false,
                            completedNormally ? "Phone voice playback completed." : "Phone voice playback stopped.");
                    if (viewModel != null) viewModel.setVoicePlaying(isDesktopVoiceStreamActive());
                    maybeResumePreparedListenAfterAudio("Phone voice playback completed.");
                });
            }

            @Override public void onQueueIdle() {
                runOnUiThread(() -> {
                    if (!isDesktopVoiceStreamActive()
                            && (phoneVoiceService == null || phoneVoiceService.isIdle())) {
                        starVoicePlaybackActive = false;
                        setKeepScreenAwake(KEEP_AWAKE_REASON_AUDIO_PLAYBACK, false, "Phone voice queue is idle.");
                        maybePlayPendingShirtOffAnimation();
                        maybeResumePreparedListenAfterAudio("Phone voice queue is idle.");
                    }
                });
            }
        });

        viewModel.uiState().observe(this, state -> {
            if (state == null) return;
            recording = state.recording;
            if (state.selectedStarId != null && !state.selectedStarId.isEmpty()) {
                selectStarInSpinner(state.selectedStarId);
            }
            if (connectionStatusText != null && state.connectionStatus != null && !state.connectionStatus.isEmpty()) {
                connectionStatusText.setText(state.connectionStatus);
            }
            if (prodMiniStatusText != null && state.statusMessage != null && !state.statusMessage.isEmpty()) {
                prodMiniStatusText.setText(state.statusMessage);
            }
        });
        viewModel.bridgeEvents().observe(this, event -> {
            if (event != null) handleBridgeEvent(event.payload);
        });
        viewModel.mobileStatePayload().observe(this, payload -> {
            if (payload != null) applyMobileStatePayload(payload);
        });
        viewModel.protocolDebug().observe(this, message -> {
            if (message != null && !message.isEmpty()) appendVoiceDebug("Bridge protocol", message);
        });
        viewModel.initialize();
    }

    @Override
    protected void onResume() {
        super.onResume();
        activityResumed = true;
        registerStarInitiativeServiceReceiver();
        ensureBridgeWebSocket();
        loadPrefetchedInitiativeIntoMemory("Android activity resumed");
        selectedStarVisibleSinceMs = SystemClock.elapsedRealtime();
        syncPreparedSpeech("Android activity resumed", true);
        mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("Android activity resumed", true), 1800L);
    }

    @Override
    protected void onPause() {
        activityResumed = false;
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        stopRecording(false);
        mainHandler.removeCallbacks(preparedSpeechLoop);
        if (phoneVoiceService != null) phoneVoiceService.setListener(null);
        if (starAnimationCoordinator != null) starAnimationCoordinator.setListener(null);
        httpExecutor.shutdownNow();
        foregroundRequestExecutor.shutdownNow();
        unregisterStarInitiativeServiceReceiver();
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST | KEEP_AWAKE_REASON_VOICE_STREAM | KEEP_AWAKE_REASON_AUDIO_PLAYBACK, false, "Activity destroyed");
        super.onDestroy();
    }

    private void setKeepScreenAwake(int reason, boolean enabled, String note) {
        if (Looper.myLooper() != Looper.getMainLooper()) {
            mainHandler.post(() -> setKeepScreenAwake(reason, enabled, note));
            return;
        }

        int before = keepScreenAwakeReasonMask;
        if (enabled) {
            keepScreenAwakeReasonMask |= reason;
        } else {
            keepScreenAwakeReasonMask &= ~reason;
        }

        if ((before == 0) == (keepScreenAwakeReasonMask == 0)) return;

        if (keepScreenAwakeReasonMask != 0) {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            appendVoiceDebug("Auerlis kept the phone awake", note == null || note.length() == 0 ? "Star answer in progress." : note);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            appendVoiceDebug("Auerlis let the phone rest", note == null || note.length() == 0 ? "Star answer finished." : note);
        }
    }

    private boolean isDesktopVoiceStreamActive() {
        return phoneVoiceService != null && phoneVoiceService.isDesktopStreamActive();
    }

    private void buildUi() {
        if (prodMode) {
            buildProdUi();
            return;
        }
        buildDevUi();
    }

    private void buildDevUi() {
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

        Button prodToggleButton = button("Switch to Prod Mode");
        prodToggleButton.setOnClickListener(v -> switchUiMode(true));
        root.addView(prodToggleButton);

        connectionStatusText = label("Connecting to desktop host...");
        connectionStatusText.setTextSize(14);
        root.addView(connectionStatusText);

        audioStatusText = label("Phone voice socket not connected yet.");
        audioStatusText.setTextSize(14);
        root.addView(audioStatusText);

        root.addView(label("Desktop bridge URL"));
        serverUrlEdit = input(savedBridgeUrl(), false);
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
                activeStarIds));
        requestTypeSpinner = new Spinner(this);
        requestTypeSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Chat", "Story", "Photo", "Letter", "BattleUpdate", "Debug"}));
        selectors.addView(starSpinner, weightParams(1));
        selectors.addView(requestTypeSpinner, weightParams(1));
        root.addView(selectors);

        starSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (suppressStarSelectionDispatch) return;
                invalidatePrefetchedInitiative("Selected Star changed.");
                onSelectedStarPageChanged();
                syncStarInitiativeBackgroundService("Selected Star changed", true);
                dispatchSelectStar(starSpinner.getSelectedItem().toString());
            }

            @Override
            public void onNothingSelected(AdapterView<?> parent) { }
        });

        starStateText = label("Elusiaa is met and allied by default.");
        root.addView(starStateText);

        root.addView(label("Last emotional vectors"));
        emotionVectorsText = output(currentLastEmotionVectors, 4);
        emotionVectorsText.setTextIsSelectable(true);
        root.addView(emotionVectorsText);

        root.addView(label("Interstar status"));
        interstarStatusText = output(currentInterstarStatus, 2);
        interstarStatusText.setTextIsSelectable(true);
        root.addView(interstarStatusText);

        LinearLayout starControlRow = row();
        toggleMetButton = button("Mark Met");
        toggleMetButton.setOnClickListener(v -> dispatchToggleMet());
        toggleAllyButton = button("Mark Ally");
        toggleAllyButton.setOnClickListener(v -> dispatchToggleAlly());
        clearHistoryButton = button("Clear History");
        clearHistoryButton.setOnClickListener(v -> dispatchClearHistory());
        starControlRow.addView(toggleMetButton, weightParams(1));
        starControlRow.addView(toggleAllyButton, weightParams(1));
        starControlRow.addView(clearHistoryButton, weightParams(1));
        root.addView(starControlRow);
        clearAllDataButton = button("Clear All Data");
        clearAllDataButton.setOnClickListener(v -> dispatchClearAllData());
        root.addView(clearAllDataButton);
        updateStarStateUi("Elusiaa", true, true, 0, 0, 0, 0.0, false, false, "Ready");

        LinearLayout contextRow = row();
        areaEdit = input("VirethornCaves", false);
        questEdit = input("AndroidPhoneBridge", false);
        contextRow.addView(areaEdit, weightParams(1));
        contextRow.addView(questEdit, weightParams(1));
        root.addView(contextRow);

        romanceCheck = new CheckBox(this);
        romanceCheck.setText("Romance Mode");
        romanceCheck.setChecked(settingsService.romanceMode());
        romanceCheck.setOnCheckedChangeListener((buttonView, isChecked) -> syncStarInitiativeRuntimePrefs());
        root.addView(romanceCheck);

        playOnPhoneCheck = new CheckBox(this);
        playOnPhoneCheck.setText("Play Star voice on this phone");
        playOnPhoneCheck.setChecked(settingsService.playOnPhone());
        playOnPhoneCheck.setOnCheckedChangeListener((buttonView, isChecked) -> {
            syncStarInitiativeRuntimePrefs();
            if (isChecked) {
                testConnection(false);
            } else {
                closeAudioSocket();
                audioStatusText.setText("Phone voice socket disabled.");
            }
        });
        root.addView(playOnPhoneCheck);

        CheckBox initiativeCheck = new CheckBox(this);
        initiativeCheck.setText("Let selected Star speak at rest");
        initiativeCheck.setChecked(starInitiativeEnabled);
        initiativeCheck.setOnCheckedChangeListener((buttonView, isChecked) -> {
            starInitiativeEnabled = isChecked;
            starInitiativeBackgroundEnabled = isChecked;
            settingsService.setStarInitiativeEnabled(starInitiativeEnabled);
            syncStarInitiativeBackgroundService("Dev at-rest initiative toggle changed", true);
        });
        root.addView(initiativeCheck);

        root.addView(label("Message to selected Star"));
        messageEdit = input("", true);
        messageEdit.setHint("Say something to the selected Star. The box clears after Send.");
        messageEdit.setMinLines(4);
        root.addView(messageEdit);

        LinearLayout actionRow = row();
        Button sendButton = button("Send Message");
        sendButton.setOnClickListener(v -> sendMessage());
        recordButton = button("Record Voice");
        recordButton.setOnClickListener(v -> toggleRecording());
        photoButton = button("Share Photo");
        photoButton.setOnClickListener(v -> startPhotoCapture());
        actionRow.addView(sendButton, weightParams(1));
        actionRow.addView(recordButton, weightParams(1));
        actionRow.addView(photoButton, weightParams(1));
        root.addView(actionRow);

        root.addView(label("Star response"));
        responseText = output("No response yet.", 6);
        responseText.setTextIsSelectable(true);
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


    private void buildProdUi() {
        prodStarNameText = null;
        prodStarSubtitleText = null;
        prodRelationshipText = null;
        prodPreparedSpeechIndicatorText = null;
        prodMiniStatusText = null;
        prodSpriteView = null;
        prodTalkButtonText = null;
        prodTalkIcon = null;
        prodTalkButton = null;
        prodListenButton = null;
        prodListenSubmenu = null;
        prodListenThoughtOption = null;
        prodListenSparkOption = null;
        prodListenConstellationOption = null;
        prodConstellationChoiceIcon = null;
        prodPhotoButton = null;
        prodVoiceActions = null;
        prodCancelVoiceButton = null;
        prodSendVoiceButton = null;
        prodSendTextButton = null;
        prodGenerateWritingButton = null;
        prodTellWritingButton = null;
        prodBackgroundWritingLoopCheck = null;
        prodXPostingCheck = null;
        prodPostToXNowButton = null;
        prodTextToggleButton = null;
        prodTextComposer = null;
        prodConversationText = null;
        prodConversationScroll = null;

        createProdHiddenControls();
        setContentView(R.layout.activity_prod_companion);

        prodStarNameText = findViewById(R.id.prod_star_name);
        prodStarSubtitleText = findViewById(R.id.prod_star_subtitle);
        prodRelationshipText = findViewById(R.id.prod_relationship);
        prodPreparedSpeechIndicatorText = findViewById(R.id.prod_prepared_speech_indicator);
        prodTalkButtonText = findViewById(R.id.prod_talk_text);
        prodTalkIcon = findViewById(R.id.prod_talk_icon);
        prodTalkButton = findViewById(R.id.prod_talk_button);
        prodListenButton = findViewById(R.id.prod_listen_button);
        prodListenSubmenu = findViewById(R.id.prod_listen_submenu);
        prodListenThoughtOption = findViewById(R.id.prod_listen_thought_option);
        prodListenSparkOption = findViewById(R.id.prod_listen_spark_option);
        prodListenConstellationOption = findViewById(R.id.prod_listen_constellation_option);
        prodConstellationChoiceIcon = findViewById(R.id.prod_constellation_choice_icon);
        prodPhotoButton = findViewById(R.id.prod_photo_button);
        prodVoiceActions = findViewById(R.id.prod_voice_actions);
        prodCancelVoiceButton = findViewById(R.id.prod_cancel_voice_button);
        prodSendVoiceButton = findViewById(R.id.prod_send_voice_button);
        prodSendTextButton = findViewById(R.id.prod_send_text_button);
        prodTextToggleButton = findViewById(R.id.prod_text_toggle_button);
        prodTextComposer = findViewById(R.id.prod_text_composer);
        prodConversationText = findViewById(R.id.prod_conversation_text);
        if (prodConversationText != null) prodConversationText.setTextIsSelectable(true);
        prodConversationScroll = findViewById(R.id.prod_conversation_scroll);
        messageEdit = findViewById(R.id.prod_message_edit);

        View titlePanel = findViewById(R.id.prod_star_title_panel);
        View avatarFrame = findViewById(R.id.prod_avatar_frame);
        View catAvatar = findViewById(R.id.prod_cat_avatar);
        View prodStage = findViewById(R.id.prod_stage);
        View.OnClickListener openStars = v -> showStarSelectionDialog();
        View.OnClickListener openCompanionMenu = v -> showProdCompanionMenu();
        View.OnClickListener openStarTapPanel = v -> openSelectedStarTapPanel();
        titlePanel.setOnClickListener(openStars);
        prodStarNameText.setOnClickListener(openStars);
        prodStarSubtitleText.setOnClickListener(openStars);
        avatarFrame.setOnClickListener(openCompanionMenu);
        catAvatar.setOnClickListener(openCompanionMenu);
        if (prodStage != null) prodStage.setOnClickListener(openStarTapPanel);
        titlePanel.setContentDescription("Open Star selector");
        prodStarNameText.setContentDescription("Open Star selector");
        prodStarSubtitleText.setContentDescription("Open Star selector");
        avatarFrame.setContentDescription("Open companion menu and status");
        catAvatar.setContentDescription("Open companion menu and status");
        if (prodListenButton != null) prodListenButton.setContentDescription("Open Listen choices: prepared thought, Spark shelf, or Constellation Story");

        prodSpriteView = findViewById(R.id.prod_sprite_view);
        if (prodSpriteView != null) {
            prodSpriteView.setClickable(true);
            prodSpriteView.setFocusable(true);
            prodSpriteView.setOnClickListener(openStarTapPanel);
        }

        prodTalkButton.setOnClickListener(v -> prodTalkButtonTapped());
        if (prodListenButton != null) prodListenButton.setOnClickListener(v -> toggleProdListenSubmenu());
        if (prodListenThoughtOption != null) prodListenThoughtOption.setOnClickListener(v -> {
            hideProdListenSubmenu();
            requestStarInitiativeFromListenButton();
        });
        if (prodListenSparkOption != null) prodListenSparkOption.setOnClickListener(v -> {
            hideProdListenSubmenu();
            dispatchStarWritingCommand("TellStarWriting", playOnPhoneCheck == null || playOnPhoneCheck.isChecked(), "spark");
        });
        if (prodListenConstellationOption != null) prodListenConstellationOption.setOnClickListener(v -> {
            hideProdListenSubmenu();
            dispatchStarWritingCommand("TellStarWriting", playOnPhoneCheck == null || playOnPhoneCheck.isChecked(), "constellation-story");
        });
        if (prodPhotoButton != null) prodPhotoButton.setOnClickListener(v -> startPhotoCapture());
        prodCancelVoiceButton.setOnClickListener(v -> cancelProdVoiceRecording());
        prodSendVoiceButton.setOnClickListener(v -> sendProdVoiceRecording());
        prodTextToggleButton.setOnClickListener(v -> toggleProdTextComposer());
        prodSendTextButton.setOnClickListener(v -> sendMessage());

        messageEdit.setHint("Type a message to the selected Star...");
        messageEdit.setSingleLine(false);
        messageEdit.setMinLines(2);
        messageEdit.setMaxLines(4);

        refreshProdStarUi(selectedStarName());
        refreshProdConversationWindow();
        updateProdRecordingUi();
        updateStarStateUi(selectedStarName(), true, true, 0, 0, 0, 0.0, false, false, "Prod ready");
        updatePreparedSpeechIndicator();
        syncPreparedSpeech("Production UI built", true);
    }

    private void createProdHiddenControls() {
        serverUrlEdit = input(savedBridgeUrl(), false);
        starSpinner = new Spinner(this);
        starSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, activeStarIds));
        starSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (suppressStarSelectionDispatch) return;
                refreshProdStarUi(starSpinner.getSelectedItem().toString());
                invalidatePrefetchedInitiative("Selected Star changed.");
                onSelectedStarPageChanged();
                syncStarInitiativeBackgroundService("Selected Star changed", true);
                dispatchSelectStar(starSpinner.getSelectedItem().toString());
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        requestTypeSpinner = new Spinner(this);
        requestTypeSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Chat", "Story", "Photo", "Letter", "BattleUpdate", "Debug"}));
        areaEdit = input(settingsService.area(), false);
        questEdit = input(settingsService.quest(), false);
        romanceCheck = new CheckBox(this);
        romanceCheck.setText("Romance Mode");
        romanceCheck.setChecked(settingsService.romanceMode());
        romanceCheck.setOnCheckedChangeListener((buttonView, isChecked) -> syncStarInitiativeRuntimePrefs());
        playOnPhoneCheck = new CheckBox(this);
        playOnPhoneCheck.setText("Play Star voice on this phone");
        playOnPhoneCheck.setChecked(settingsService.playOnPhone());
        playOnPhoneCheck.setOnCheckedChangeListener((buttonView, isChecked) -> {
            syncStarInitiativeRuntimePrefs();
            if (isChecked) testConnection(false); else closeAudioSocket();
        });
        connectionStatusText = new TextView(this);
        audioStatusText = new TextView(this);
        responseText = new TextView(this);
        responseText.setTextIsSelectable(true);
        debugText = new TextView(this);
        voiceDebugText = new TextView(this);
        starStateText = new TextView(this);
        emotionVectorsText = new TextView(this);
        interstarStatusText = new TextView(this);
        responseText.setText("");
        debugText.setText("Prod status ready.");
        voiceDebugText.setText("Prod voice journal ready.");
    }


    private void openSelectedStarTapPanel() {
        if (selectedStarIsGirlfriend) {
            showGirlfriendSettingsDialog();
        } else {
            showStarSelectionDialog();
        }
    }

    private void showGirlfriendSettingsDialog() {
        final String starName = selectedStarName();
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        outer.setPadding(dp(14), dp(14), dp(14), dp(8));

        TextView title = prodText(starName + " Settings", 22, 0xFFFFF4FA, true);
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(title);

        TextView sub = prodText("Girlfriend controls for the selected Star. Tap the Star sprite to open this again.", 12, 0xFFFFD6EA, false);
        sub.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(sub);

        TextView status = prodOutput(buildGirlfriendSettingsStatusText(), 6);
        status.setTextIsSelectable(true);
        outer.addView(status, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        CheckBox cuteMode = new CheckBox(this);
        cuteMode.setText("Allow cute mode");
        cuteMode.setTextColor(0xFFEAF7FF);
        cuteMode.setChecked(selectedStarCuteModeEnabled);
        outer.addView(cuteMode);

        TextView hint = prodText("When enabled, the desktop runtime may activate her Star-specific cute mode during girlfriend hourly state updates. When disabled, she stays affectionate but avoids the special cute-mode behavior pattern.", 12, 0xFFB6E6F2, false);
        outer.addView(hint);

        LinearLayout buttons = row();
        Button save = prodButton("Save");
        Button cuteNow = prodButton("Cute Now");
        Button breakup = prodButton("Break up");
        buttons.addView(save, weightParams(1));
        buttons.addView(cuteNow, weightParams(1));
        buttons.addView(breakup, weightParams(1));
        outer.addView(buttons);

        Button postX = prodButton("Post 5 to X");
        LinearLayout.LayoutParams postXParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        postXParams.setMargins(0, dp(8), 0, 0);
        outer.addView(postX, postXParams);
        TextView quickXStatus = prodText("Queue an immediate five-post X set for " + starName + ".", 11, 0xFF9DEBFF, false);
        quickXStatus.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(quickXStatus);

        AlertDialog dialog = new AlertDialog.Builder(this)
                .setView(outer)
                .setNegativeButton("Close", null)
                .create();
        dialog.setOnShowListener(d -> {
            dialog.getWindow().setBackgroundDrawable(makeProdPanel(0xF0141024, 0xFFFF6DB3, 18));
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(0xFFEAF7FF);
            save.setOnClickListener(v -> {
                selectedStarCuteModeEnabled = cuteMode.isChecked();
                if (!selectedStarCuteModeEnabled) selectedStarCuteModeActive = false;
                dispatchGirlfriendCuteMode(selectedStarCuteModeEnabled);
                status.setText(buildGirlfriendSettingsStatusText());
            });
            cuteNow.setOnClickListener(v -> {
                selectedStarCuteModeEnabled = true;
                selectedStarCuteModeActive = true;
                cuteMode.setChecked(true);
                dispatchCuteModeNow();
                status.setText(buildGirlfriendSettingsStatusText());
            });
            breakup.setOnClickListener(v -> confirmBreakupFromGirlfriendSettings(dialog, starName));
            postX.setOnClickListener(v -> {
                quickXStatus.setText("Queuing a five-post X set for " + starName + "…");
                dispatchXPostingNowForStar(starName, postX, quickXStatus, false);
            });
        });
        dialog.show();
    }

    private String buildGirlfriendSettingsStatusText() {
        StringBuilder sb = new StringBuilder();
        sb.append("Star: ").append(selectedStarName()).append('\n');
        sb.append(selectedStarIsGirlfriend ? "Relationship: girlfriend" : "Relationship: not girlfriend").append('\n');
        sb.append("Cute mode setting: ").append(selectedStarCuteModeEnabled ? "enabled" : "disabled").append('\n');
        sb.append("Cute mode right now: ").append(selectedStarCuteModeActive ? "active" : "not active").append('\n');
        if (selectedStarCuteModeSignature != null && selectedStarCuteModeSignature.trim().length() > 0) {
            sb.append("Signature: ").append(selectedStarCuteModeSignature.trim()).append('\n');
        }
        sb.append('\n').append("This only controls the girlfriend cute-mode style layer. It does not turn off normal affection or romance.");
        return sb.toString();
    }

    private void confirmBreakupFromGirlfriendSettings(AlertDialog parentDialog, String starName) {
        AlertDialog confirm = new AlertDialog.Builder(this)
                .setTitle("Break up with " + starName + "?")
                .setMessage("This ends the exclusive girlfriend path for this Star and clears girlfriend-path cute/needs/wants history.")
                .setNegativeButton("Cancel", null)
                .setPositiveButton("Break up", (d, which) -> {
                    if (parentDialog != null) parentDialog.dismiss();
                    dispatchBreakupGirlfriend();
                })
                .create();
        confirm.setOnShowListener(d -> {
            confirm.getWindow().setBackgroundDrawable(makeProdPanel(0xF0141024, 0xFFFF6DB3, 18));
            confirm.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(0xFFEAF7FF);
            confirm.getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(0xFFFFB4D6);
        });
        confirm.show();
    }

    private void showProdCompanionMenu() {
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        outer.setPadding(dp(14), dp(14), dp(14), dp(8));

        TextView title = prodText("Last Cat Console", 22, 0xFFEAF7FF, true);
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(title);

        TextView sub = prodText("Production view stays clean. Status, vectors, bridge controls, and Dev Mode live here.", 12, 0xFFB6E6F2, false);
        sub.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(sub);

        LinearLayout controls = new LinearLayout(this);
        controls.setOrientation(LinearLayout.VERTICAL);
        controls.setPadding(0, dp(10), 0, dp(4));

        TextView bridgeLabel = prodText("Desktop bridge URL", 12, 0xFF9EE8F0, true);
        controls.addView(bridgeLabel);
        EditText bridgeUrl = input(baseUrl(), false);
        bridgeUrl.setSingleLine(true);
        bridgeUrl.setText(serverUrlEdit == null ? "" : serverUrlEdit.getText().toString());
        controls.addView(bridgeUrl, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));

        TextView ipv6Label = prodText("Preferred local IPv6 address", 12, 0xFF9EE8F0, true);
        controls.addView(ipv6Label);
        EditText preferredIpv6 = input(settingsService == null ? "" : settingsService.preferredIpv6Address(), false);
        preferredIpv6.setSingleLine(true);
        preferredIpv6.setHint("Auto-detected from the desktop socket adapter");
        controls.addView(preferredIpv6, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(46)));

        CheckBox preferIpv6 = new CheckBox(this);
        preferIpv6.setText("Use saved IPv6 first for local connections");
        preferIpv6.setTextColor(0xFFEAF7FF);
        preferIpv6.setChecked(settingsService == null || settingsService.useIpv6Primary());
        controls.addView(preferIpv6);

        TextView ipv6Hint = prodText("The desktop reports the routable IPv6 address on the same adapter carrying the working socket. Link-local addresses are not saved because their interface scope cannot be reused safely on Android.", 11, 0xFF9CB8C5, false);
        controls.addView(ipv6Hint);

        CheckBox playPhone = new CheckBox(this);
        playPhone.setText("Play Star voice on this phone");
        playPhone.setTextColor(0xFFEAF7FF);
        playPhone.setChecked(playOnPhoneCheck == null || playOnPhoneCheck.isChecked());
        controls.addView(playPhone);

        CheckBox romance = new CheckBox(this);
        romance.setText("Romance Mode");
        romance.setTextColor(0xFFEAF7FF);
        romance.setChecked(romanceCheck == null || romanceCheck.isChecked());
        controls.addView(romance);

        CheckBox initiative = new CheckBox(this);
        initiative.setText("Let the selected Star speak at rest");
        initiative.setTextColor(0xFFEAF7FF);
        initiative.setChecked(starInitiativeEnabled);
        controls.addView(initiative);

        LinearLayout buttons = row();
        Button save = prodButton("Save Settings");
        Button test = prodButton("Test Bridge");
        buttons.addView(save, weightParams(1));
        buttons.addView(test, weightParams(1));
        controls.addView(buttons);

        Button updateIpv6 = prodButton("Update IPv6 from Desktop");
        controls.addView(updateIpv6, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout buttons2 = row();
        Button audio = prodButton("Test Audio");
        Button pauseVoice = prodButton((phoneVoiceService != null && phoneVoiceService.isPaused()) ? "Resume Voice" : "Pause Voice");
        Button dev = prodButton("Dev Mode");
        buttons2.addView(audio, weightParams(1));
        buttons2.addView(pauseVoice, weightParams(1));
        buttons2.addView(dev, weightParams(1));
        controls.addView(buttons2);

        TextView writingLabel = prodText("Star Writing Shelf", 13, 0xFFFFC8F0, true);
        writingLabel.setPadding(0, dp(10), 0, dp(4));
        controls.addView(writingLabel);

        prodBackgroundWritingLoopCheck = new CheckBox(this);
        prodBackgroundWritingLoopCheck.setText("Generate Star writings automatically on the desktop while idle");
        prodBackgroundWritingLoopCheck.setTextColor(0xFFEAF7FF);
        prodBackgroundWritingLoopCheck.setChecked(desktopBackgroundWritingLoopEnabled);
        controls.addView(prodBackgroundWritingLoopCheck);

        TextView autoWritingHint = prodText("Turn this off before recording a quote when you want the most reliable voice playback. Disabling it immediately cancels the desktop's current background writing pass; manual writing buttons still work.", 11, 0xFFB6A7C8, false);
        controls.addView(autoWritingHint);

        LinearLayout writingButtons = row();
        prodGenerateWritingButton = prodButton("Create Shelf Work");
        prodTellWritingButton = prodButton("Tell Top Sparks (" + selectedStarPendingWritingCount + ")");
        writingButtons.addView(prodGenerateWritingButton, weightParams(1));
        writingButtons.addView(prodTellWritingButton, weightParams(1));
        controls.addView(writingButtons);
        TextView writingHint = prodText("Create Shelf Work uses the configured shelf generator. Tell Top Sparks builds a 25-item weighted-IU listening set from the top 100 untold Sparks. The main Listen → Constellation choice deterministically plays the highest-Overall-IU unplayed story and queues its full narration in spoken chunks.", 11, 0xFFB6A7C8, false);
        controls.addView(writingHint);
        updateProdWritingButtons();

        TextView xPostingLabel = prodText("Star X Publishing", 13, 0xFF9DEBFF, true);
        xPostingLabel.setPadding(0, dp(10), 0, dp(4));
        controls.addView(xPostingLabel);

        prodXPostingCheck = new CheckBox(this);
        prodXPostingCheck.setText("Post five selected writings at each of the four daily slots");
        prodXPostingCheck.setTextColor(0xFFEAF7FF);
        prodXPostingCheck.setChecked(desktopXPostingEnabled);
        controls.addView(prodXPostingCheck);

        prodPostToXNowButton = prodButton("Post 5 to X for All Stars");
        controls.addView(prodPostToXNowButton, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        TextView xPostingHint = prodText("Queues one five-post batch for every enabled/configured Star account. The desktop runs them in the background through the serialized X posting lane, chooses five unposted shelf items for each Star, and marks each writing posted only after X accepts it.", 11, 0xFFB6A7C8, false);
        controls.addView(xPostingHint);

        TextView status = prodOutput(buildProdMenuStatusText(), 12);
        status.setTextIsSelectable(true);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(status);
        controls.addView(scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(280)));
        outer.addView(controls);

        ScrollView dialogScroll = new ScrollView(this);
        dialogScroll.addView(outer);
        AlertDialog dialog = new AlertDialog.Builder(this)
                .setView(dialogScroll)
                .setNegativeButton("Close", null)
                .create();
        dialog.setOnDismissListener(d -> {
            prodBackgroundWritingLoopCheck = null;
            prodXPostingCheck = null;
            prodPostToXNowButton = null;
        });
        dialog.setOnShowListener(d -> {
            dialog.getWindow().setBackgroundDrawable(makeProdPanel(0xF00B1220, 0xFF00D5D7, 18));
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(0xFFEAF7FF);
            prodBackgroundWritingLoopCheck.setOnCheckedChangeListener((buttonView, isChecked) -> {
                if (suppressBackgroundWritingLoopToggleDispatch) return;
                dispatchBackgroundWritingLoopToggle(isChecked, prodBackgroundWritingLoopCheck, status);
            });
            prodXPostingCheck.setOnCheckedChangeListener((buttonView, isChecked) -> {
                if (suppressXPostingToggleDispatch) return;
                dispatchXPostingToggle(isChecked, prodXPostingCheck, status);
            });
            prodPostToXNowButton.setOnClickListener(v -> dispatchAllStarsXPostingNow(prodPostToXNowButton, status));
            fetchMobileState(false);
            save.setOnClickListener(v -> {
                if (serverUrlEdit != null) serverUrlEdit.setText(bridgeUrl.getText().toString().trim());
                baseUrl(); // Persist the manually editable IPv4/hostname fallback before reconnecting.
                if (viewModel != null && !viewModel.updatePreferredIpv6Settings(
                        preferredIpv6.getText().toString(), preferIpv6.isChecked())) {
                    setDebug("Invalid IPv6 address", "Enter a routable IPv6 address, clear the field, or use Update IPv6 from Desktop.");
                    return;
                }
                if (playOnPhoneCheck != null) playOnPhoneCheck.setChecked(playPhone.isChecked());
                if (romanceCheck != null) romanceCheck.setChecked(romance.isChecked());
                starInitiativeEnabled = initiative.isChecked();
                starInitiativeBackgroundEnabled = starInitiativeEnabled;
                settingsService.setStarInitiativeEnabled(starInitiativeEnabled);
                invalidatePrefetchedInitiative("Android Star/voice/romance settings changed.");
                syncStarInitiativeBackgroundService("Prod settings saved", true);
                setDebug("Prod settings saved", "Bridge=" + baseUrl() + ", phoneVoice=" + (playOnPhoneCheck == null || playOnPhoneCheck.isChecked()) + ", romance=" + (romanceCheck == null || romanceCheck.isChecked()) + ", starInitiative=" + starInitiativeEnabled + ", autoSpeak=" + starInitiativeEnabled);
                status.setText(buildProdMenuStatusText());
                testConnection(false);
            });
            test.setOnClickListener(v -> {
                if (serverUrlEdit != null) serverUrlEdit.setText(bridgeUrl.getText().toString().trim());
                baseUrl();
                testConnection(true);
                mainHandler.postDelayed(() -> status.setText(buildProdMenuStatusText()), 900);
            });
            updateIpv6.setOnClickListener(v -> {
                if (serverUrlEdit != null) serverUrlEdit.setText(bridgeUrl.getText().toString().trim());
                baseUrl();
                updateIpv6.setEnabled(false);
                updateIpv6.setText("Checking Desktop…");
                viewModel.refreshPreferredIpv6(false).whenComplete((address, error) -> runOnUiThread(() -> {
                    updateIpv6.setEnabled(true);
                    updateIpv6.setText("Update IPv6 from Desktop");
                    if (error != null) {
                        setDebug("IPv6 discovery failed", error.toString());
                    } else {
                        preferredIpv6.setText(address);
                        preferIpv6.setChecked(true);
                        viewModel.updatePreferredIpv6Settings(address, true);
                        setDebug("IPv6 saved", "The app will use http://[" + address + "]:"
                                + BridgeAddressService.bridgePort(baseUrl()) + " first and fall back to " + baseUrl() + ".");
                    }
                    status.setText(buildProdMenuStatusText());
                }));
            });
            audio.setOnClickListener(v -> testPhoneAudio());
            prodGenerateWritingButton.setOnClickListener(v -> dispatchStarWritingCommand("GenerateStarWriting", false));
            prodTellWritingButton.setOnClickListener(v -> dispatchStarWritingCommand("TellStarWriting", playOnPhoneCheck == null || playOnPhoneCheck.isChecked()));
            pauseVoice.setOnClickListener(v -> {
                if ((phoneVoiceService != null && phoneVoiceService.isPaused())) phoneVoiceService.resume(); else phoneVoiceService.pause();
                pauseVoice.setText((phoneVoiceService != null && phoneVoiceService.isPaused()) ? "Resume Voice" : "Pause Voice");
            });
            dev.setOnClickListener(v -> { dialog.dismiss(); switchUiMode(false); });
        });
        dialog.show();
    }

    private String buildProdMenuStatusText() {
        StringBuilder sb = new StringBuilder();
        sb.append("Star: ").append(selectedStarName()).append('\n');
        sb.append(prodRelationshipText == null ? "Relationship: unknown" : prodRelationshipText.getText()).append("\n\n");
        sb.append(nonEmptyText(connectionStatusText, "Connection: not checked yet.")).append('\n');
        sb.append("Configured fallback: ").append(settingsService == null ? baseUrl() : settingsService.bridgeUrl()).append('\n');
        sb.append("Saved IPv6: ").append(settingsService == null || settingsService.preferredIpv6Address().isEmpty()
                ? "none discovered" : settingsService.preferredIpv6Address()).append('\n');
        sb.append("IPv6 primary: ").append(settingsService == null || settingsService.useIpv6Primary() ? "enabled" : "disabled").append('\n');
        sb.append("Active bridge: ").append(viewModel == null ? baseUrl() : viewModel.activeBridgeUrl()).append('\n');
        sb.append(nonEmptyText(audioStatusText, "Audio: phone voice socket not connected yet.")).append("\n\n");
        sb.append("Desktop auto writing: ").append(desktopBackgroundWritingLoopEnabled ? "enabled" : "disabled").append('\n');
        sb.append("Automatic X posting: ").append(desktopXPostingEnabled ? "enabled" : "disabled").append('\n');
        sb.append("X status: ").append(desktopXPostingStatus == null ? "unknown" : desktopXPostingStatus).append('\n');
        sb.append("Writing shelf: ").append(Math.max(0, selectedStarPendingWritingCount)).append(" new, ")
                .append(Math.max(0, selectedStarPersistedWritingCount)).append(" saved, ")
                .append(Math.max(0, selectedStarUnpostedWritingCount)).append(" not posted.\n\n");
        sb.append(currentDailyStarState == null ? "Today: no daily Star state yet." : currentDailyStarState).append("\n\n");
        sb.append(currentInterstarStatus == null ? "Interstar state: idle." : currentInterstarStatus).append("\n\n");
        sb.append(currentLastEmotionVectors == null ? "Last emotional vectors: none yet." : currentLastEmotionVectors).append("\n");
        sb.append(currentTopAffectedEmotions == null ? "Top emotions: none yet." : currentTopAffectedEmotions).append("\n\n");
        sb.append("Last Star response:\n").append(nonEmptyText(responseText, "No response yet.")).append("\n\n");
        sb.append("Debug:\n").append(nonEmptyText(debugText, "No debug status yet.")).append("\n\n");
        sb.append("Voice journal:\n").append(nonEmptyText(voiceDebugText, "No voice journal yet."));
        return sb.toString();
    }

    private String nonEmptyText(TextView view, String fallback) {
        if (view == null || view.getText() == null) return fallback;
        String text = view.getText().toString().trim();
        return text.length() == 0 ? fallback : text;
    }

    private void showStarSelectionDialog() {
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        outer.setPadding(dp(12), dp(12), dp(12), dp(8));
        TextView title = prodText("Choose a Star", 22, 0xFFEAF7FF, true);
        title.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(title);
        TextView sub = prodText("Choose a companion.", 12, 0xFFB6E6F2, false);
        sub.setGravity(Gravity.CENTER_HORIZONTAL);
        sub.setPadding(0, dp(2), 0, dp(4));
        outer.addView(sub);

        TextView quickXStatus = prodText("Quick X posting: choose any Star below.", 11, 0xFF9DEBFF, false);
        quickXStatus.setGravity(Gravity.CENTER_HORIZONTAL);
        quickXStatus.setPadding(0, dp(6), 0, 0);
        outer.addView(quickXStatus);

        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(2);
        grid.setPadding(0, dp(8), 0, dp(6));
        for (String starId : new ArrayList<>(activeStarIds)) {
            StarMeta meta = starMeta(starId);
            LinearLayout card = starSelectionCard(meta, quickXStatus);
            GridLayout.LayoutParams lp = new GridLayout.LayoutParams();
            lp.width = 0;
            lp.height = GridLayout.LayoutParams.WRAP_CONTENT;
            lp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
            lp.setMargins(dp(5), dp(5), dp(5), dp(5));
            grid.addView(card, lp);
        }
        outer.addView(grid, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        ScrollView dialogScroll = new ScrollView(this);
        dialogScroll.addView(outer);

        AlertDialog dialog = new AlertDialog.Builder(this)
                .setView(dialogScroll)
                .setNegativeButton("Close", null)
                .create();
        activeStarSelectionDialog = dialog;
        dialog.setOnDismissListener(d -> { if (activeStarSelectionDialog == dialog) activeStarSelectionDialog = null; });
        dialog.setOnShowListener(d -> {
            dialog.getWindow().setBackgroundDrawable(makeProdPanel(0xF00B1220, 0xFF00D5D7, 18));
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(0xFFEAF7FF);
        });
        dialog.show();
    }

    private LinearLayout starSelectionCard(StarMeta meta, TextView quickXStatus) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setGravity(Gravity.CENTER_HORIZONTAL);
        card.setPadding(dp(10), dp(10), dp(10), dp(12));
        card.setMinimumHeight(dp(224));
        card.setBackground(makeProdPanel(0xE0111828, meta.accentColor, 18));

        FrameLayout portraitFrame = new FrameLayout(this);
        portraitFrame.setBackground(makeProdPanel(0xAA0A1220, meta.accentColor, 16));
        portraitFrame.setPadding(dp(6), dp(6), dp(6), dp(6));

        ImageView portrait = new ImageView(this);
        portrait.setImageResource(meta.previewResId());
        portrait.setScaleType(ImageView.ScaleType.CENTER_CROP);
        portrait.setAdjustViewBounds(false);
        portraitFrame.addView(portrait, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        LinearLayout.LayoutParams portraitParams = new LinearLayout.LayoutParams(dp(132), dp(140));
        card.addView(portraitFrame, portraitParams);

        TextView name = prodText(meta.id, 17, 0xFFFFFFFF, true);
        name.setGravity(Gravity.CENTER_HORIZONTAL);
        name.setPadding(0, dp(10), 0, 0);
        card.addView(name);

        TextView sub = prodText(meta.element + " • " + meta.domain, 12, 0xFFCFFAFE, false);
        sub.setGravity(Gravity.CENTER_HORIZONTAL);
        sub.setPadding(0, dp(2), 0, 0);
        card.addView(sub);

        card.setOnClickListener(v -> {
            selectStarInSpinner(meta.id);
            refreshProdStarUi(meta.id);
            invalidatePrefetchedInitiative("Selected Star changed.");
            onSelectedStarPageChanged();
            syncStarInitiativeBackgroundService("Selected Star changed", true);
            dispatchSelectStar(meta.id);
            if (activeStarSelectionDialog != null) activeStarSelectionDialog.dismiss();
        });
        return card;
    }

    private void switchUiMode(boolean useProd) {
        prodMode = useProd;
        settingsService.setProductionMode(useProd);
        invalidatePrefetchedInitiative("Android session mode changed.");
        buildUi();
        syncStarInitiativeBackgroundService("Android UI mode switched", true);
        testConnection(false);
    }

    private void prodTalkButtonTapped() {
        if (recording) {
            stopRecording(true);
        } else {
            startRecording();
        }
    }

    private void cancelProdVoiceRecording() {
        stopRecording(false);
        appendProdConversationLine("System", "Voice cancelled.");
    }

    private void sendProdVoiceRecording() {
        stopRecording(true);
    }

    private void toggleProdTextComposer() {
        if (prodTextComposer == null) return;
        boolean show = prodTextComposer.getVisibility() != View.VISIBLE;
        prodTextComposer.setVisibility(show ? View.VISIBLE : View.GONE);
        if (show && messageEdit != null) messageEdit.requestFocus();
    }

    private void updateProdRecordingUi() {
        if (prodTalkButtonText != null) prodTalkButtonText.setText(recording ? "Send Voice" : "Talk");
        if (prodVoiceActions != null) prodVoiceActions.setVisibility(recording ? View.VISIBLE : View.GONE);
        refreshProdTalkButtonDecor();
    }

    private void setProdAnimationState(String state) {
        prodAnimationState = (state == null || state.length() == 0) ? "idle" : state.toLowerCase(Locale.US);
        if (shirtOffAnimationActive) return;
        if (prodSpriteView != null) {
            prodSpriteView.setStarAnimation(selectedStarName(), prodAnimationState, starMeta(selectedStarName()).frameResIds);
        }
    }

    private void cancelStarVoiceAnimationMarkers(String reason) {
        activeAnimatedVoiceClip = null;
        starVoicePlaybackActive = false;
        starAnimationCoordinator.cancel(reason);
        maybePlayPendingShirtOffAnimation();
    }

    private void finishStarVoiceAnimationMarkers(PhoneVoicePlaybackService.PhoneVoiceClip clip) {
        if (clip == null || activeAnimatedVoiceClip != clip) return;
        activeAnimatedVoiceClip = null;
        starVoicePlaybackActive = false;
        starAnimationCoordinator.endVoice();
        maybePlayPendingShirtOffAnimation();
    }

    private void queueShirtOffAnimation(String starId) {
        if (!prodMode || starId == null || !starId.equalsIgnoreCase(selectedStarName())) return;
        pendingShirtOffAnimation = true;
        pendingShirtOffStarId = starId;
        appendVoiceDebug("Shirt-off reaction queued", "Waiting for " + starId + "'s current voice queue to finish.");
        maybePlayPendingShirtOffAnimation();
    }

    private void maybePlayPendingShirtOffAnimation() {
        if (!pendingShirtOffAnimation
                || shirtOffAnimationActive
                || starVoicePlaybackActive
                || isDesktopVoiceStreamActive()
                || !(phoneVoiceService == null || phoneVoiceService.isIdle())
                || prodSpriteView == null) return;

        final String starId = pendingShirtOffStarId;
        pendingShirtOffAnimation = false;
        pendingShirtOffStarId = "";
        if (starId.length() == 0 || !starId.equalsIgnoreCase(selectedStarName())) return;

        // No voice marker should be able to replace the one-shot visual after it begins.
        starAnimationCoordinator.cancel("Shirt-off one-shot started.");
        activeAnimatedVoiceClip = null;
        starVoicePlaybackActive = false;
        shirtOffAnimationActive = true;

        boolean started = prodSpriteView.playOneShotAnimation(starId, "shirt-off", 2000L, () -> {
            shirtOffAnimationActive = false;
            setProdAnimationState(starVoicePlaybackActive ? "talking" : "idle");
            appendVoiceDebug("Shirt-off reaction finished", starId + " returned to the current voice/idle state after the two-second final-frame hold.");
            maybePlayPendingShirtOffAnimation();
        });

        if (!started) {
            shirtOffAnimationActive = false;
            setProdAnimationState(starVoicePlaybackActive ? "talking" : "idle");
            appendVoiceDebug("Shirt-off reaction skipped", "No star_animations/" + starId.toLowerCase(Locale.US) + "/shirt-off.gif asset was found.");
        } else {
            appendVoiceDebug("Shirt-off reaction playing", starId + " is playing shirt-off.gif once.");
        }
    }

    private void startStarVoiceAnimationMarkers(PhoneVoicePlaybackService.PhoneVoiceClip clip, long fallbackDurationMs, long playbackElapsedMs) {
        if (!prodMode || clip == null || !clip.fromDesktop) return;
        activeAnimatedVoiceClip = clip;
        starVoicePlaybackActive = true;
        starAnimationCoordinator.beginVoice(fallbackDurationMs, clip.speechActivitySegmentsMs, playbackElapsedMs);
    }

    private void appendProdConversationLine(String speaker, String text) {
        if (!prodMode || text == null || text.trim().length() == 0) return;
        String star = selectedStarName();
        ArrayList<String> lines = prodConversationByStar.get(star);
        if (lines == null) {
            lines = new ArrayList<>();
            prodConversationByStar.put(star, lines);
        }
        String line = (speaker == null || speaker.length() == 0 ? "System" : speaker) + ": " + text.trim().replace('\n', ' ');
        // Newest-first means the Star response should appear above the player line once it arrives.
        lines.add(0, line);
        while (lines.size() > 12) lines.remove(lines.size() - 1);
        refreshProdConversationWindow();
    }

    private boolean recentProdConversationAlreadyHas(String speaker, String text) {
        if (!prodMode || text == null || text.trim().length() == 0) return false;
        ArrayList<String> lines = prodConversationByStar.get(selectedStarName());
        if (lines == null || lines.size() == 0) return false;
        String expected = (speaker == null || speaker.length() == 0 ? "System" : speaker) + ": " + text.trim().replace('\n', ' ');
        for (int i = 0; i < Math.min(lines.size(), 6); i++) {
            if (lines.get(i).equals(expected)) return true;
        }
        return false;
    }

    private void refreshProdConversationWindow() {
        if (prodConversationText == null) return;
        ArrayList<String> lines = prodConversationByStar.get(selectedStarName());
        if (lines == null || lines.size() == 0) {
            prodConversationText.setText("No conversation yet.");
            if (prodConversationScroll != null) {
                prodConversationScroll.post(() -> prodConversationScroll.scrollTo(0, 0));
            }
            return;
        }
        StringBuilder sb = new StringBuilder();
        int end = Math.min(lines.size(), 10);
        for (int i = 0; i < end; i++) {
            if (sb.length() > 0) sb.append('\n');
            sb.append(lines.get(i));
        }
        prodConversationText.setText(sb.toString());
        if (prodConversationScroll != null) {
            prodConversationScroll.post(() -> prodConversationScroll.scrollTo(0, 0));
        }
    }

    private void refreshProdStarUi(String starId) {
        if (prodStarNameText == null) return;
        StarMeta meta = starMeta(starId);
        prodStarNameText.setText(meta.id);
        prodStarSubtitleText.setText(meta.element + " Star • " + meta.domain);
        prodStarSubtitleText.setContentDescription("Current Star: " + meta.id + ". Open Star selector.");
        if (prodSpriteView != null && !shirtOffAnimationActive) prodSpriteView.setStarAnimation(meta.id, prodAnimationState, meta.frameResIds);
        refreshProdConversationWindow();
    }

    private void updateProdStarStateUi(String starId, boolean met, boolean ally, int heart, int trust, int romance, double connection, boolean ready, boolean girlfriend, String status) {
        refreshProdStarUi(starId);
        if (prodRelationshipText != null) {
            String gfSuffix = girlfriend ? "   Girlfriend" + (selectedStarCuteModeEnabled ? (selectedStarCuteModeActive ? "   Cute" : "") : "   Cute off") : (ready ? "   Ready" : "");
            prodRelationshipText.setText("♥ Heart " + heart + "   Trust " + trust + "   Romance " + romance + "   " + String.format(Locale.US, "%.1f", connection) + "%" + gfSuffix);
        }
        if (prodMiniStatusText != null) prodMiniStatusText.setText(status);
    }

    private LinearLayout.LayoutParams prodBlockParams() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, dp(7), 0, dp(7));
        return lp;
    }

    private TextView prodText(String text, int sp, int color, boolean bold) {
        TextView tv = new TextView(this);
        tv.setText(text);
        tv.setTextSize(sp);
        tv.setTextColor(color);
        tv.setTypeface(Typeface.MONOSPACE, bold ? Typeface.BOLD : Typeface.NORMAL);
        tv.setIncludeFontPadding(false);
        return tv;
    }

    private TextView prodOutput(String text, int minLines) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(0xFFE5E7EB);
        view.setTextSize(12);
        view.setTypeface(Typeface.MONOSPACE, Typeface.NORMAL);
        view.setIncludeFontPadding(false);
        view.setLineSpacing(0f, 1.08f);
        view.setMinLines(minLines);
        view.setPadding(dp(10), dp(8), dp(10), dp(8));
        view.setBackground(makeProdPanel(0xB90B1220, 0x6656D3E8, 14));
        return view;
    }

    private Button prodButton(String text) {
        Button button = new Button(this);
        button.setText(text);
        button.setTextColor(0xFFEAF7FF);
        button.setTextSize(14);
        button.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        button.setAllCaps(false);
        button.setBackground(makeProdPanel(0xCC172033, 0xFF00D5D7, 14));
        return button;
    }

    private GradientDrawable makeProdPanel(int fillColor, int strokeColor, int radiusDp) {
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(fillColor);
        bg.setCornerRadius(dp(radiusDp));
        bg.setStroke(dp(1), strokeColor);
        return bg;
    }

    private StarMeta starMeta(String starId) {
        String id = starId == null ? "Elusiaa" : starId;
        if (id.equalsIgnoreCase("Kaelia")) return new StarMeta("Kaelia", "Void", "Fatalism", 0xFF8B5CF6, R.drawable.portrait_kaelia, new int[]{R.drawable.star_kaelia_idle_0});
        if (id.equalsIgnoreCase("Maera")) return new StarMeta("Maera", "Blood", "Possession", 0xFFFF4D6D, R.drawable.portrait_maera, new int[]{R.drawable.star_maera_idle_0});
        if (id.equalsIgnoreCase("Khavia")) return new StarMeta("Khavia", "Water", "Friendship", 0xFF22D3EE, R.drawable.portrait_khavia, new int[]{R.drawable.star_khavia_idle_0});
        if (id.equalsIgnoreCase("Auerlis")) return new StarMeta("Auerlis", "Fire", "Joy", 0xFFF97316, R.drawable.portrait_auerlis, new int[]{R.drawable.star_auerlis_idle_0});
        if (id.equalsIgnoreCase("Nyxari")) return new StarMeta("Nyxari", "Shadow", "Fragmentation", 0xFF60A5FA, R.drawable.portrait_nyxari, new int[]{R.drawable.star_nyxari_idle_0});
        return new StarMeta("Elusiaa", "Light", "Mercy", 0xFF38E8FF, R.drawable.portrait_elusiaa, new int[]{R.drawable.star_elusiaa_idle_0});
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
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            recordAudioPermissionLauncher.launch(Manifest.permission.RECORD_AUDIO);
        }
    }

    private boolean requestCameraPermissionIfNeeded() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            openPhotoPickerAfterCameraPermission = true;
            cameraPermissionLauncher.launch(Manifest.permission.CAMERA);
            return false;
        }
        return true;
    }

    private String savedBridgeUrl() {
        return settingsService == null ? DEFAULT_BRIDGE_URL : settingsService.bridgeUrl();
    }

    private String baseUrl() {
        String value = serverUrlEdit == null ? savedBridgeUrl() : serverUrlEdit.getText().toString().trim();
        if (value.endsWith("/")) value = value.substring(0, value.length() - 1);
        value = value.length() == 0 ? DEFAULT_BRIDGE_URL : value;
        if (settingsService != null) settingsService.setBridgeUrl(value);
        return value;
    }

    private String currentSessionId() {
        return prodMode ? "android-phone-prod" : "android-phone-dev";
    }

    private JSONObject baseRequestJson() throws Exception {
        JSONObject json = new JSONObject();
        json.put("sessionId", currentSessionId());
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

    private void initializeBridgeWebSocket() {
        // Lifecycle and reconnect ownership moved to MainViewModel/BridgeConnectionService.
        if (viewModel != null) viewModel.ensureConnected();
    }

    private void ensureBridgeWebSocket() {
        if (destroyed || bridgeConnectionService == null) return;

        // Do not force a reconnect for every request. Prepared-speech reconciliation and other
        // foreground/background calls pass through this method frequently; reconnecting here used
        // to churn both the control WebSocket and the separate phone-audio socket every few seconds.
        // A phone-audio reconnect clears transport state and could cut a playing sentence short.
        if (viewModel != null) viewModel.ensureConnected();
        else bridgeConnectionService.ensureConnected();
    }

    private void sendSocketRequest(String operation, JSONObject payload, long timeoutMs, Consumer<JSONObject> onSuccess, Consumer<Throwable> onFailure) {
        ensureBridgeWebSocket();
        if (bridgeConnectionService == null) {
            onFailure.accept(new IllegalStateException("Mobile bridge service is not initialized."));
            return;
        }
        CompletableFuture<JSONObject> future;
        if (conversationService == null) {
            future = bridgeConnectionService.request(operation, payload, timeoutMs);
        } else if ("message".equalsIgnoreCase(operation)) {
            future = conversationService.sendText(payload, timeoutMs);
        } else if ("audio".equalsIgnoreCase(operation)) {
            future = conversationService.sendAudio(payload, timeoutMs);
        } else if ("state".equalsIgnoreCase(operation)) {
            future = conversationService.fetchState(payload, timeoutMs);
        } else if ("initiative".equalsIgnoreCase(operation)) {
            future = conversationService.requestInitiative(payload, timeoutMs);
        } else if ("test-audio".equalsIgnoreCase(operation)) {
            future = conversationService.testAudio(payload, timeoutMs);
        } else if ("dispatch".equalsIgnoreCase(operation)
                && "Photo".equalsIgnoreCase(payload.optString("requestType", ""))) {
            future = conversationService.sendPhoto(payload, timeoutMs);
        } else if ("command".equalsIgnoreCase(operation) || "dispatch".equalsIgnoreCase(operation)) {
            future = conversationService.sendCommand(payload, timeoutMs);
        } else {
            future = bridgeConnectionService.request(operation, payload, timeoutMs);
        }
        future.whenComplete((response, error) -> {
            if (error == null) {
                onSuccess.accept(response == null ? new JSONObject() : response);
                return;
            }
            Throwable cause = error instanceof CompletionException && error.getCause() != null ? error.getCause() : error;
            onFailure.accept(cause);
        });
    }

    private void testConnection(boolean manual) {
        syncStarInitiativeRuntimePrefs();
        if (manual && bridgeConnectionService != null) {
            // Manual testing is the one place where the editable fallback URL should be applied
            // immediately. Routine requests only ensure the existing socket is alive.
            bridgeConnectionService.reconnect(baseUrl());
        }
        sendSocketRequest("health", new JSONObject(), 10000L, json -> {
            try {
                boolean promoted = viewModel != null && viewModel.acceptHealthNetworkInfo(json, true);
                String activeUrl = viewModel == null ? baseUrl() : viewModel.activeBridgeUrl();
                String host = BridgeAddressService.hostFromBaseUrl(activeUrl);
                int audioPort = json.optInt("audioPort", BridgeAddressService.audioPort(activeUrl));
                runOnUiThread(() -> {
                    if (connectionStatusText != null) {
                        connectionStatusText.setText("Connected: " + json.optString("machineName", "desktop")
                                + " at " + activeUrl + (BridgeAddressService.isIpv6Url(activeUrl) ? " (IPv6 WebSocket)" : " (WebSocket)"));
                    }
                    if (manual) {
                        setDebug("Health OK", "Active=" + activeUrl
                                + ", adapter=" + json.optString("localAdapterName", "unknown")
                                + ", discoveredIPv6=" + json.optString("preferredIpv6Address", "none")
                                + ", HTTP port " + json.optInt("port")
                                + ", WebSocket clients " + json.optInt("webSocketClientCount")
                                + ", audio port " + audioPort
                                + ", audio clients " + json.optInt("audioClientCount"));
                    }
                });
                if (!promoted && (playOnPhoneCheck == null || playOnPhoneCheck.isChecked())) ensureAudioSocket(host, audioPort);
                refreshActiveStarRoster();
                fetchMobileState(false);
            } catch (Exception ex) {
                if (manual) runOnUiThread(() -> setDebug("Health parse failed", ex.toString()));
            }
        }, error -> runOnUiThread(() -> {
            if (connectionStatusText != null) connectionStatusText.setText("Connecting to " + (viewModel == null ? baseUrl() : viewModel.activeBridgeUrl()) + " — " + error.getMessage());
            if (manual) setDebug("WebSocket health failed", error.toString());
        }));
    }

    private void refreshActiveStarRoster() {
        String activeBase = viewModel == null ? baseUrl() : viewModel.activeBridgeUrl();
        httpExecutor.execute(() -> {
            try {
                JSONObject response = new JSONObject(get(activeBase + "/api/mobile/stars", 10000));
                JSONArray stars = response.optJSONArray("stars");
                if (stars == null || stars.length() == 0) return;

                ArrayList<String> discovered = new ArrayList<>();
                for (int i = 0; i < stars.length(); i++) {
                    String id = stars.optString(i, "").trim();
                    if (id.length() == 0) continue;
                    boolean duplicate = false;
                    for (String existing : discovered) {
                        if (existing.equalsIgnoreCase(id)) { duplicate = true; break; }
                    }
                    if (!duplicate) discovered.add(id);
                }
                if (discovered.isEmpty()) return;

                runOnUiThread(() -> applyActiveStarRoster(discovered));
            } catch (Exception ex) {
                // The desktop JSON roster is authoritative when reachable; offline startup keeps
                // the built-in visual fallback list so the phone remains usable.
                appendVoiceDebug("Star roster refresh", "Using current phone roster: " + ex.getMessage());
            }
        });
    }

    private void applyActiveStarRoster(List<String> discovered) {
        if (discovered == null || discovered.isEmpty()) return;
        String selected = selectedStarName();

        activeStarIds.clear();
        activeStarIds.addAll(discovered);

        if (starSpinner != null) {
            suppressStarSelectionDispatch = true;
            try {
                ArrayAdapter<String> adapter = new ArrayAdapter<>(
                        this,
                        android.R.layout.simple_spinner_dropdown_item,
                        new ArrayList<>(activeStarIds));
                starSpinner.setAdapter(adapter);
                int position = -1;
                for (int i = 0; i < adapter.getCount(); i++) {
                    String item = adapter.getItem(i);
                    if (item != null && item.equalsIgnoreCase(selected)) { position = i; break; }
                }
                if (position < 0) position = 0;
                starSpinner.setSelection(position);
            } finally {
                suppressStarSelectionDispatch = false;
            }
            String current = selectedStarName();
            settingsService.setSelectedStarId(current);
            refreshProdStarUi(current);
        }
    }

    private int defaultAudioPort() {
        String active = viewModel == null ? baseUrl() : viewModel.activeBridgeUrl();
        return BridgeAddressService.audioPort(active);
    }

    private String selectedStarName() {
        Object selected = starSpinner == null ? null : starSpinner.getSelectedItem();
        return selected == null ? "Elusiaa" : selected.toString();
    }

    private void dispatchSelectStar(String starId) {
        dispatchBridgeCommand("SelectStar", starId, null, null, "Android Star dropdown changed.");
    }

    private void registerStarInitiativeServiceReceiver() {
        if (starInitiativeServiceReceiverRegistered) return;
        IntentFilter filter = new IntentFilter();
        filter.addAction(ACTION_STAR_INITIATIVE_SERVICE_RESPONSE);
        filter.addAction(ACTION_STAR_INITIATIVE_SERVICE_STATUS);
        ContextCompat.registerReceiver(
                this,
                starInitiativeServiceReceiver,
                filter,
                ContextCompat.RECEIVER_NOT_EXPORTED);
        starInitiativeServiceReceiverRegistered = true;
    }

    private void unregisterStarInitiativeServiceReceiver() {
        if (!starInitiativeServiceReceiverRegistered) return;
        try {
            unregisterReceiver(starInitiativeServiceReceiver);
        } catch (Exception ignored) {
        } finally {
            starInitiativeServiceReceiverRegistered = false;
        }
    }

    private void syncStarInitiativeRuntimePrefs() {
        starInitiativeBackgroundEnabled = starInitiativeEnabled;
        if (settingsService == null) return;
        settingsService.setProductionMode(prodMode);
        settingsService.setStarInitiativeEnabled(starInitiativeEnabled);
        settingsService.setBridgeUrl(baseUrl());
        settingsService.setSelectedStarId(selectedStarName());
        settingsService.setRomanceMode(romanceCheck == null || romanceCheck.isChecked());
        settingsService.setPlayOnPhone(playOnPhoneCheck == null || playOnPhoneCheck.isChecked());
        if (areaEdit != null) settingsService.setArea(areaEdit.getText().toString());
        if (questEdit != null) settingsService.setQuest(questEdit.getText().toString());
        if (viewModel != null) viewModel.selectStar(selectedStarName());
    }

    private void syncStarInitiativeBackgroundService(String reason, boolean checkSoon) {
        syncStarInitiativeRuntimePrefs();
        try {
            if (starInitiativeCoordinator == null) return;
            if (starInitiativeEnabled) starInitiativeCoordinator.start(reason == null ? "Android foreground sync" : reason, checkSoon);
            else starInitiativeCoordinator.stop(reason == null ? "At-rest initiative disabled" : reason);
        } catch (Exception ex) {
            appendVoiceDebug("Nyxari could not sync the at-rest service", ex.toString());
        }
    }

    private void pauseStarInitiativeBackgroundService(String reason) {
        blockPreparedListenFor(5L * 60L * 1000L);
        try {
            if (starInitiativeCoordinator != null) starInitiativeCoordinator.stop(reason);
            appendVoiceDebug("Background at-rest service", "Paused while a foreground player request is being sent. " + (reason == null ? "" : reason));
        } catch (Exception ex) {
            appendVoiceDebug("Nyxari could not pause the at-rest service", ex.toString());
        }
    }

    private void handleStarInitiativeServiceIntent(Intent intent) {
        if (intent == null) return;
        String action = intent.getAction();
        if (ACTION_STAR_INITIATIVE_SERVICE_STATUS.equals(action)) {
            String status = intent.getStringExtra("status");
            String message = intent.getStringExtra("message");
            String starId = intent.getStringExtra("starId");
            if ("requestStarted".equalsIgnoreCase(status)) {
                if (prodMiniStatusText != null) {
                    prodMiniStatusText.setText((starId == null || starId.length() == 0 ? selectedStarName() : starId) + " is quietly preparing something to say...");
                }
            } else if ("cacheReady".equalsIgnoreCase(status)) {
                loadPrefetchedInitiativeIntoMemory("background service prepared a Listen response");
            }
            setDebug("Background at-rest service", clip(message == null ? status : message, 1200));
            appendVoiceDebug("Background at-rest service", clip(message == null ? status : message, 220));
            return;
        }

        if (!ACTION_STAR_INITIATIVE_SERVICE_RESPONSE.equals(action)) return;

        boolean ok = intent.getBooleanExtra("ok", false);
        boolean cacheReady = intent.getBooleanExtra("cacheReady", false);
        String eventJson = intent.getStringExtra("eventJson");
        String body = intent.getStringExtra("body");
        String message = intent.getStringExtra("message");
        try {
            if (ok && cacheReady) {
                loadPrefetchedInitiativeIntoMemory("background service response");
            } else if (ok && eventJson != null && eventJson.trim().length() > 0) {
                // Backward compatibility with service responses created before the prepared cache.
                handleBridgeEvent(new JSONObject(eventJson));
            } else if (ok && body != null && body.trim().length() > 0) {
                handleConversationResponse("Background at-rest Star initiative complete", body);
            } else {
                setDebug("Background at-rest service", message == null ? "No prepared at-rest response yet." : message);
                appendVoiceDebug("Background at-rest service", message == null ? "No prepared at-rest response yet." : message);
            }
        } catch (Exception ex) {
            setDebug("Background at-rest service parse failed", ex.toString());
            appendVoiceDebug("Background at-rest service parse failed", ex.toString());
        }
    }

    private void blockPreparedListenFor(long delayMs) {
        if (preparedSpeechCoordinator != null) preparedSpeechCoordinator.blockFor(delayMs);
        else if (settingsService != null) {
            long requested = System.currentTimeMillis() + Math.max(0L, delayMs);
            if (requested > settingsService.preparedListenNotBeforeMs()) settingsService.setPreparedListenNotBeforeMs(requested);
        }
    }

    private void allowPreparedListenAfter(long delayMs, String reason) {
        if (preparedSpeechCoordinator != null) preparedSpeechCoordinator.allowAfter(delayMs);
        else if (settingsService != null) settingsService.setPreparedListenNotBeforeMs(System.currentTimeMillis() + Math.max(0L, delayMs));
        appendVoiceDebug("Prepared Listen idle window", (reason == null ? "The app is quiet again." : reason) + " Next cache check may run after " + Math.max(0L, delayMs) + " ms.");
    }

    private void maybeResumePreparedListenAfterAudio(String reason) {
        if (destroyed || isDesktopVoiceStreamActive() || !(phoneVoiceService == null || phoneVoiceService.isIdle())) return;
        allowPreparedListenAfter(2500L, reason);
        mainHandler.postDelayed(
                () -> syncStarInitiativeBackgroundService("Audio is idle; prepare the next Listen response", true),
                2800L);
    }

    private void loadPrefetchedInitiativeIntoMemory(String reason) {
        if (destroyed) return;
        final String expectedSessionId = currentSessionId();
        final String expectedStarId = selectedStarName();
        foregroundRequestExecutor.execute(() -> {
            PrefetchedInitiativeStore.CachedInitiative loaded = starInitiativeCoordinator.loadPrefetched(
                    expectedSessionId,
                    expectedStarId);
            if (destroyed) return;
            runOnUiThread(() -> {
                if (!expectedSessionId.equalsIgnoreCase(currentSessionId())
                        || !expectedStarId.equalsIgnoreCase(selectedStarName())) {
                    return;
                }
                prefetchedInitiative = loaded;
                if (loaded != null) {
                    if (prodMiniStatusText != null) {
                        prodMiniStatusText.setText(expectedStarId + " already has something ready to say.");
                    }
                    appendVoiceDebug(
                            "Prepared Listen cache ready",
                            expectedStarId + " has " + loaded.chunks.size() + " audio chunk(s) in memory. " + (reason == null ? "" : reason));
                }
            });
        });
    }

    private void invalidatePrefetchedInitiative(String reason) {
        prefetchedInitiative = null;
        long nextGeneration = settingsService.preparedListenGeneration() + 1L;
        settingsService.setPreparedListenGeneration(nextGeneration);
        settingsService.setLastBackgroundRequestMs(0L);
        starInitiativeCoordinator.clearPrefetched();
        appendVoiceDebug("Prepared Listen cache cleared", reason == null ? "The cached at-rest line is no longer current." : reason);
    }

    private boolean playPrefetchedInitiativeIfAvailable() {
        if (isDesktopVoiceStreamActive() || (phoneVoiceService != null && !phoneVoiceService.isIdle())) {
            appendVoiceDebug("Prepared Listen held",
                    "Existing Star speech is still playing, so the prepared at-rest line remains queued instead of clearing the voice queue.");
            return false;
        }
        String sessionId = currentSessionId();
        String starId = selectedStarName();
        PrefetchedInitiativeStore.CachedInitiative prepared = prefetchedInitiative;
        if (prepared == null
                || !sessionId.equalsIgnoreCase(prepared.sessionId)
                || !starId.equalsIgnoreCase(prepared.starId)) {
            prepared = starInitiativeCoordinator.loadPrefetched(sessionId, starId);
        }
        if (prepared == null || prepared.chunks == null || prepared.chunks.isEmpty()) return false;

        prefetchedInitiative = null;
        invalidatePrefetchedInitiative("The player consumed " + starId + "'s prepared at-rest line.");
        blockPreparedListenFor(5L * 60L * 1000L);
        phoneVoiceService.clearForNewStream();

        boolean playOnPhone = playOnPhoneCheck != null
                ? playOnPhoneCheck.isChecked()
                : settingsService.playOnPhone();
        int queued = 0;
        if (playOnPhone) {
            for (PrefetchedInitiativeStore.CachedVoiceChunk chunk : prepared.chunks) {
                try {
                    String checksum = chunk.sha256.length() > 0 ? chunk.sha256 : sha256Hex(chunk.wav);
                    int[] markers = PhoneVoicePlaybackService.parseSpeechActivitySegments(chunk.speechActivitySegmentsMs);
                    phoneVoiceService.enqueue(new PhoneVoicePlaybackService.PhoneVoiceClip(
                            chunk.wav,
                            starId + " prepared Listen chunk " + (chunk.chunkIndex + 1) + "/" + Math.max(1, chunk.totalChunks),
                            checksum,
                            false,
                            "",
                            0L,
                            chunk.chunkIndex,
                            markers));
                    queued++;
                } catch (Exception ex) {
                    appendVoiceDebug("Prepared Listen audio chunk failed", ex.toString());
                }
            }
        }

        try {
            JSONObject response = new JSONObject(prepared.response.toString());
            response.put("voiceChunksQueued", queued);
            response.put("voiceMessage", playOnPhone
                    ? "Played immediately from Android's prepared at-rest voice cache."
                    : "Prepared at-rest text was consumed; Play on Phone is disabled.");
            response.put("prefetchForListen", true);
            response.put("prefetchedAtUtc", prepared.createdAtMs);
            handleConversationResponse("Prepared Listen response", response.toString());
        } catch (Exception ex) {
            setDebug("Prepared Listen response failed", ex.toString());
            appendVoiceDebug("Prepared Listen response failed", ex.toString());
        }

        if (prodMiniStatusText != null) {
            prodMiniStatusText.setText(queued > 0 ? "Playing " + starId + "'s prepared thought..." : starId + "'s prepared thought opened.");
        }
        appendVoiceDebug("Listen played instantly", starId + " consumed " + prepared.chunks.size() + " prepared audio chunk(s); queued=" + queued + ".");
        if (!playOnPhone || queued == 0) {
            allowPreparedListenAfter(4500L, "The prepared text was consumed without local audio playback.");
        }
        mainHandler.postDelayed(
                () -> syncStarInitiativeBackgroundService("Prepared Listen cache consumed; prepare the next one", true),
                5000L);
        return true;
    }

    private void dispatchToggleMet() {
        dispatchBridgeCommand("SetMet", selectedStarName(), !selectedStarIsMet, null, "Android toggled met flag.");
    }

    private void dispatchToggleAlly() {
        dispatchBridgeCommand("SetAlly", selectedStarName(), null, !selectedStarIsAlly, "Android toggled ally flag.");
    }

    private void dispatchClearHistory() {
        dispatchBridgeCommand("ClearHistory", selectedStarName(), null, null, "Android cleared selected Star history.");
    }

    private void dispatchClearAllData() {
        dispatchBridgeCommand("ClearAllData", selectedStarName(), null, null, "Android cleared all local Star data.");
    }

    private void dispatchBreakupGirlfriend() {
        dispatchBridgeCommand("BreakupGirlfriend", selectedStarName(), null, null, "Android girlfriend settings ended girlfriend mode for this Star.");
    }

    private void dispatchGirlfriendCuteMode(boolean enabled) {
        dispatchBridgeCommand("SetGirlfriendCuteMode", selectedStarName(), null, null, "Android girlfriend settings cute mode toggle.", Boolean.valueOf(enabled));
    }

    private void dispatchCuteModeNow() {
        dispatchBridgeCommand("ActivateCuteModeNow", selectedStarName(), null, null, "Android Cute Now button requested immediate girlfriend cute mode.");
    }

    private void maybeRequestStarInitiative(String reason) {
        if (!prodMode || !starInitiativeEnabled || destroyed) return;
        long now = System.currentTimeMillis();
        if (now - lastStarInitiativeRequestMs < 120000L) return;
        lastStarInitiativeRequestMs = now;
        syncStarInitiativeRuntimePrefs();
        String starName = selectedStarName();
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "At-rest Star initiative request sent; keeping the screen on while she answers.");

        try {
            JSONObject json = createInitiativeRequestJson(
                    starName,
                    "android-initiative-" + System.currentTimeMillis(),
                    "SixStarsMobileAndroid",
                    "AndroidProdStarInitiative",
                    "SixStarsMobileAndroid-StarInitiative",
                    reason == null ? "Android at-rest initiative" : reason);
            setDebug("At-rest Star initiative", "Asking " + starName + " if she wants to say something. Reason: " + reason);
            appendVoiceDebug("At-rest Star initiative", "WebSocket request sent immediately for " + starName + ": " + reason);
            sendSocketRequest("dispatch", json, 240000L, response -> {
                JSONObject event = response.optJSONObject("event");
                runOnUiThread(() -> {
                    if (event != null) handleBridgeEvent(event);
                    else handleConversationResponse("At-rest Star initiative complete", response.toString());
                });
            }, error -> runOnUiThread(() -> {
                setDebug("At-rest Star initiative failed", error.toString());
                appendVoiceDebug("At-rest Star initiative failed", error.toString());
                setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "At-rest Star initiative failed.");
            }));
        } catch (Exception ex) {
            setDebug("At-rest Star initiative failed", ex.toString());
            appendVoiceDebug("At-rest Star initiative failed", ex.toString());
            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "At-rest Star initiative failed.");
        }
    }

    private void toggleProdListenSubmenu() {
        if (prodListenSubmenu == null) {
            requestStarInitiativeFromListenButton();
            return;
        }
        boolean opening = prodListenSubmenu.getVisibility() != View.VISIBLE;
        if (!opening) {
            hideProdListenSubmenu();
            return;
        }

        if (prodConstellationChoiceIcon != null) {
            String[] constellations = new String[]{
                    "\u2726 \u00B7 \u2727",
                    "\u22C6  \u2726  \u22C6",
                    "\u2727 \u00B7 \u22C6",
                    "\u2726  \u02DA  \u2727",
                    "\u22C6 \u00B7 \u2736"
            };
            int constellationIndex = (int) Math.floorMod(System.nanoTime(), (long) constellations.length);
            prodConstellationChoiceIcon.setText(constellations[constellationIndex]);
        }
        prodListenSubmenu.setVisibility(View.VISIBLE);
        if (prodListenButton != null) {
            prodListenButton.setContentDescription("Close Listen choices");
        }
    }

    private void hideProdListenSubmenu() {
        if (prodListenSubmenu != null) prodListenSubmenu.setVisibility(View.GONE);
        if (prodListenButton != null) {
            prodListenButton.setContentDescription("Open Listen choices: prepared thought, Spark shelf, or Constellation Story");
        }
    }

    private void requestStarInitiativeFromListenButton() {
        if (!prodMode || destroyed) return;
        if (recording) {
            setDebug("Listen button waiting", "Finish or cancel the current voice recording before asking the Star if she has something to say.");
            appendVoiceDebug("Listen button", "Voice recording is active, so no initiative request was sent.");
            return;
        }
        if (isDesktopVoiceStreamActive() || (phoneVoiceService != null && !phoneVoiceService.isIdle())) {
            if (prodMiniStatusText != null) prodMiniStatusText.setText("The current Star voice is still speaking.");
            appendVoiceDebug("Listen button waiting",
                    "The existing Star voice queue must finish before a new at-rest response is requested.");
            return;
        }

        long now = System.currentTimeMillis();
        if (now - lastManualListenRequestMs < 2500L) return;
        lastManualListenRequestMs = now;
        lastStarInitiativeRequestMs = now;

        syncStarInitiativeRuntimePrefs();

        PreparedSpeechStore.CachedItem durablePrepared = bestPreparedSpeechForSelectedStar();
        if (durablePrepared != null) {
            pauseStarInitiativeBackgroundService("Manual Listen button is consuming the Star's durable prepared thought.");
            playPreparedSpeech(durablePrepared, "manual Listen button");
            return;
        }

        if (playPrefetchedInitiativeIfAvailable()) return;

        pauseStarInitiativeBackgroundService("Manual Listen button has priority over background at-rest checks.");
        String starName = selectedStarName();
        if (prodMiniStatusText != null) prodMiniStatusText.setText("Listening for " + starName + "...");
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Manual Listen request sent; keeping the screen on while the Star answers.");

        try {
            JSONObject json = createInitiativeRequestJson(
                    starName,
                    "android-listen-" + System.currentTimeMillis(),
                    "SixStarsMobileAndroidListenButton",
                    "AndroidProdListenButton",
                    "SixStarsMobileAndroid-ManualListen",
                    "manual Android Listen button");
            setDebug("Listen", "Asking " + starName + " if she has something to say now.");
            appendVoiceDebug("Listen button", "Manual WebSocket initiative request sent immediately for " + starName + ".");
            sendSocketRequest("dispatch", json, 240000L, response -> {
                JSONObject event = response.optJSONObject("event");
                runOnUiThread(() -> {
                    if (event != null) handleBridgeEvent(event);
                    else handleConversationResponse("Listen request complete", response.toString());
                    mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("listen request finished", false), 30000L);
                });
            }, error -> runOnUiThread(() -> {
                setDebug("Listen request wait interrupted", error.toString() + "\nThe desktop may still finish the Star's at-rest line. Android will keep receiving queued bridge events over the persistent WebSocket.");
                appendVoiceDebug("Listen request wait interrupted", "Still listening on the WebSocket for a queued bridge event: " + error);
                setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Listen WebSocket wait failed; keeping the phone awake briefly while queued bridge events arrive.");
                mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted Listen request."), 20000L);
                mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("listen request recovery grace finished", false), 30000L);
            }));
        } catch (Exception ex) {
            setDebug("Listen request wait interrupted", ex.toString() + "\nThe desktop may still finish the Star's at-rest line. Android will keep receiving queued bridge events over the persistent WebSocket.");
            appendVoiceDebug("Listen request wait interrupted", "Still listening on the WebSocket for a queued bridge event: " + ex);
            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Listen WebSocket wait failed; keeping the phone awake briefly while queued bridge events are checked.");
            mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted Listen request."), 20000L);
            mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("listen request recovery grace finished", false), 30000L);
        }
    }

    private JSONObject createInitiativeRequestJson(
            String starName,
            String commandId,
            String sourceClientId,
            String quest,
            String clientName,
            String reason) throws Exception {
        JSONObject json = new JSONObject();
        json.put("commandId", commandId);
        json.put("commandType", "MaybeStarInitiative");
        json.put("sourceClientId", sourceClientId);
        json.put("sessionId", currentSessionId());
        json.put("starId", starName);
        json.put("requestType", "StarInitiative");
        json.put("playerText", "");
        json.put("area", areaEdit == null ? "VirethornCaves" : areaEdit.getText().toString());
        json.put("quest", quest);
        json.put("romanceMode", romanceCheck == null || romanceCheck.isChecked());
        json.put("speakOnDesktop", false);
        json.put("returnAudio", playOnPhoneCheck == null || playOnPhoneCheck.isChecked());
        json.put("clientName", clientName);
        json.put("reason", reason);
        return json;
    }

    private void dispatchStarWritingCommand(String commandType, boolean returnAudio) {
        dispatchStarWritingCommand(commandType, returnAudio, "spark");
    }

    private void dispatchStarWritingCommand(String commandType, boolean returnAudio, String writingOutputType) {
        if (starWritingRequestRunning) return;
        try {
            final String starId = selectedStarName();
            final boolean constellationPlayback = "constellation-story".equalsIgnoreCase(writingOutputType);
            starWritingRequestRunning = true;
            updateProdWritingButtons();
            if (prodMiniStatusText != null) {
                prodMiniStatusText.setText("GenerateStarWriting".equalsIgnoreCase(commandType)
                        ? starId + " is choosing words, creating, and rating shelf work…"
                        : constellationPlayback
                            ? starId + " is loading her highest-IU unplayed Constellation Story and preparing the complete spoken queue…"
                            : starId + " is reading a weighted set of 25 from her top 100 IU Sparks, then choosing five…");
            }

            JSONObject json = new JSONObject();
            json.put("commandId", "android-writing-" + System.currentTimeMillis());
            json.put("commandType", commandType);
            json.put("sourceClientId", "SixStarsMobileAndroid");
            json.put("sessionId", currentSessionId());
            json.put("starId", starId);
            if ("TellStarWriting".equalsIgnoreCase(commandType)) {
                json.put("writingOutputType", constellationPlayback ? "constellation-story" : "spark");
            }
            json.put("reason", "GenerateStarWriting".equalsIgnoreCase(commandType)
                    ? "Android requested new writing shelf work."
                    : constellationPlayback
                        ? "Android requested the highest-Overall-IU unplayed Constellation Story, formatted into a complete spokenText queue for phone narration."
                        : "Android requested a Spark performance from a 25-item weighted-IU listening set drawn from the top-100 untold Sparks.");
            json.put("returnAudio", returnAudio);
            json.put("returnAudioInline", false);
            json.put("clientName", "SixStarsMobileAndroidWritingShelf");

            appendVoiceDebug("Writing shelf command sent", commandType + " for " + starId + ("TellStarWriting".equalsIgnoreCase(commandType) ? " outputType=" + (constellationPlayback ? "constellation-story" : "spark") : ""));
            sendSocketRequest("dispatch", json, 240000L, response -> {
                JSONObject event = response.optJSONObject("event");
                runOnUiThread(() -> {
                    starWritingRequestRunning = false;
                    updateProdWritingButtons();
                    if (event != null) {
                        handleBridgeEvent(event);
                    } else {
                        handleWritingBridgeEvent(response, commandType, 0L, starId,
                                response.optString("message", commandType));
                    }
                    setDebug("Writing shelf request complete", clip(response.toString(), 5000));
                });
            }, error -> runOnUiThread(() -> {
                starWritingRequestRunning = false;
                updateProdWritingButtons();
                if (prodMiniStatusText != null) prodMiniStatusText.setText("Writing request failed.");
                setDebug("Writing shelf request failed", error.toString());
                appendVoiceDebug("Writing shelf request failed", error.toString());
            }));
        } catch (Exception ex) {
            starWritingRequestRunning = false;
            updateProdWritingButtons();
            setDebug("Writing shelf request failed", ex.toString());
        }
    }

    private void updateProdWritingButtons() {
        if (prodGenerateWritingButton != null) {
            prodGenerateWritingButton.setEnabled(!starWritingRequestRunning);
            prodGenerateWritingButton.setText(starWritingRequestRunning ? "Writing…" : "Create Shelf Work");
        }
        if (prodTellWritingButton != null) {
            prodTellWritingButton.setEnabled(!starWritingRequestRunning && selectedStarPendingWritingCount > 0);
            prodTellWritingButton.setText("Tell Top Sparks (" + Math.max(0, selectedStarPendingWritingCount) + ")");
        }
    }

    private void dispatchBackgroundWritingLoopToggle(boolean enabled, CheckBox checkBox, TextView status) {
        final boolean previous = desktopBackgroundWritingLoopEnabled;
        if (checkBox != null) checkBox.setEnabled(false);
        try {
            JSONObject json = new JSONObject();
            json.put("commandId", "android-auto-writing-" + System.currentTimeMillis());
            json.put("commandType", "SetBackgroundWritingLoopEnabled");
            json.put("sourceClientId", "SixStarsMobileAndroid");
            json.put("sessionId", currentSessionId());
            json.put("starId", selectedStarName());
            json.put("backgroundWritingLoopEnabled", enabled);
            json.put("reason", enabled
                    ? "Android Last Cat Console enabled desktop automatic Star writing generation."
                    : "Android Last Cat Console disabled desktop automatic Star writing generation for reliable voice playback.");

            appendVoiceDebug("Desktop auto writing toggle sent", enabled ? "enable" : "disable");
            sendSocketRequest("dispatch", json, 30000L, response -> {
                JSONObject event = response.optJSONObject("event");
                runOnUiThread(() -> {
                    applyBackgroundWritingLoopState(event == null ? response : event);
                    if (checkBox != null) checkBox.setEnabled(true);
                    if (status != null) status.setText(buildProdMenuStatusText());
                    setDebug("Desktop auto writing updated", response.optString("message", response.toString()));
                });
            }, error -> runOnUiThread(() -> {
                desktopBackgroundWritingLoopEnabled = previous;
                setBackgroundWritingLoopCheckState(previous);
                if (checkBox != null) checkBox.setEnabled(true);
                if (status != null) status.setText(buildProdMenuStatusText());
                setDebug("Desktop auto writing toggle failed", error.toString());
                appendVoiceDebug("Desktop auto writing toggle failed", error.toString());
            }));
        } catch (Exception ex) {
            desktopBackgroundWritingLoopEnabled = previous;
            setBackgroundWritingLoopCheckState(previous);
            if (checkBox != null) checkBox.setEnabled(true);
            if (status != null) status.setText(buildProdMenuStatusText());
            setDebug("Desktop auto writing toggle failed", ex.toString());
        }
    }

    private void setBackgroundWritingLoopCheckState(boolean enabled) {
        desktopBackgroundWritingLoopEnabled = enabled;
        if (prodBackgroundWritingLoopCheck == null) return;
        suppressBackgroundWritingLoopToggleDispatch = true;
        try {
            prodBackgroundWritingLoopCheck.setChecked(enabled);
        } finally {
            suppressBackgroundWritingLoopToggleDispatch = false;
        }
    }

    private void applyBackgroundWritingLoopState(JSONObject json) {
        if (json == null) return;
        if (!json.has("backgroundWritingLoopEnabled") && !json.has("BackgroundWritingLoopEnabled")) return;
        boolean enabled = optBooleanEither(json, "backgroundWritingLoopEnabled", "BackgroundWritingLoopEnabled", desktopBackgroundWritingLoopEnabled);
        setBackgroundWritingLoopCheckState(enabled);
    }

    private void dispatchXPostingToggle(boolean enabled, CheckBox checkBox, TextView status) {
        final boolean previous = desktopXPostingEnabled;
        if (checkBox != null) checkBox.setEnabled(false);
        try {
            JSONObject json = new JSONObject();
            json.put("commandId", "android-x-posting-toggle-" + System.currentTimeMillis());
            json.put("commandType", "SetXPostingEnabled");
            json.put("sourceClientId", "SixStarsMobileAndroid");
            json.put("sessionId", currentSessionId());
            json.put("starId", selectedStarName());
            json.put("xPostingEnabled", enabled);
            json.put("reason", "Android Last Cat Console changed automatic X posting.");
            sendSocketRequest("dispatch", json, 30000L, response -> {
                JSONObject event = response.optJSONObject("event");
                runOnUiThread(() -> {
                    applyXPostingState(event == null ? response : event);
                    if (checkBox != null) checkBox.setEnabled(true);
                    if (status != null) status.setText(buildProdMenuStatusText());
                    setDebug("Automatic X posting updated", response.optString("message", response.toString()));
                });
            }, error -> runOnUiThread(() -> {
                setXPostingCheckState(previous);
                if (checkBox != null) checkBox.setEnabled(true);
                if (status != null) status.setText(buildProdMenuStatusText());
                setDebug("Automatic X posting toggle failed", error.toString());
            }));
        } catch (Exception ex) {
            setXPostingCheckState(previous);
            if (checkBox != null) checkBox.setEnabled(true);
            if (status != null) status.setText(buildProdMenuStatusText());
            setDebug("Automatic X posting toggle failed", ex.toString());
        }
    }

    private void dispatchAllStarsXPostingNow(Button button, TextView status) {
        dispatchXPostingNowForStar(selectedStarName(), button, status, true);
    }

    private void dispatchXPostingNowForStar(String requestedStarId, Button button, TextView status, boolean showFullMenuStatus) {
        final String starId = requestedStarId == null || requestedStarId.trim().isEmpty()
                ? selectedStarName()
                : requestedStarId.trim();
        if (button != null) button.setEnabled(false);
        if (status != null) status.setText("Queuing five X posts for every configured Star…");
        try {
            JSONObject json = new JSONObject();
            json.put("commandId", "android-x-post-all-now-" + System.currentTimeMillis());
            json.put("commandType", "PostAllStarWritingsToXNow");
            json.put("sourceClientId", "SixStarsMobileAndroid");
            json.put("sessionId", currentSessionId());
            json.put("starId", selectedStarName());
            json.put("reason", "Android Last Cat Console requested an immediate five-post X batch for every configured Star.");
            sendSocketRequest("dispatch", json, 30000L, response -> runOnUiThread(() -> {
                JSONObject event = response.optJSONObject("event");
                JSONObject xState = event == null ? response : event;
                applyXPostingState(xState);
                if (button != null) button.setEnabled(true);
                String message = response.optString("message", "Queued X posting batches for all configured Stars.");
                desktopXPostingStatus = message;
                if (status != null) status.setText(buildProdMenuStatusText());
                setDebug("All-Star X posting queued", message);
            }), error -> runOnUiThread(() -> {
                if (button != null) button.setEnabled(true);
                if (status != null) status.setText(buildProdMenuStatusText());
                setDebug("Could not queue all-Star X posting", error.toString());
            }));
        } catch (Exception ex) {
            if (button != null) button.setEnabled(true);
            if (status != null) status.setText(buildProdMenuStatusText());
            setDebug("Could not queue all-Star X posting", ex.toString());
        }
    }

    private void setXPostingCheckState(boolean enabled) {
        desktopXPostingEnabled = enabled;
        if (prodXPostingCheck == null) return;
        suppressXPostingToggleDispatch = true;
        try {
            prodXPostingCheck.setChecked(enabled);
        } finally {
            suppressXPostingToggleDispatch = false;
        }
    }

    private void applyXPostingState(JSONObject json) {
        if (json == null) return;
        if (json.has("xPostingEnabled") || json.has("XPostingEnabled")) {
            setXPostingCheckState(optBooleanEither(json, "xPostingEnabled", "XPostingEnabled", desktopXPostingEnabled));
        }

        String eventStarId = optStringEither(json, "starId", "StarId", "");
        boolean appliesToSelectedStar = eventStarId.length() == 0
                || eventStarId.equalsIgnoreCase(selectedStarName());
        if (!appliesToSelectedStar) return;

        String status = optStringEither(json, "xPostingStatus", "XPostingStatus", "");
        if (status.length() == 0) status = optStringEither(json, "message", "Message", "");
        if (status.length() > 0) desktopXPostingStatus = status;
        selectedStarUnpostedWritingCount = optIntEither(json, "unpostedWritingCount", "UnpostedWritingCount", selectedStarUnpostedWritingCount);
    }

    private void dispatchBridgeCommand(String commandType, String starId, Boolean isMet, Boolean isAlly, String reason) {
        dispatchBridgeCommand(commandType, starId, isMet, isAlly, reason, null);
    }

    private void dispatchBridgeCommand(String commandType, String starId, Boolean isMet, Boolean isAlly, String reason, Boolean cuteModeEnabled) {
        try {
            String resolvedStarId = starId == null || starId.length() == 0 ? selectedStarName() : starId;
            JSONObject json = new JSONObject();
            json.put("commandId", "android-" + System.currentTimeMillis());
            json.put("commandType", commandType);
            json.put("sourceClientId", "SixStarsMobileAndroid");
            json.put("sessionId", currentSessionId());
            json.put("starId", resolvedStarId);
            if (isMet != null) json.put("isMet", isMet.booleanValue());
            if (isAlly != null) json.put("isAlly", isAlly.booleanValue());
            if (cuteModeEnabled != null) json.put("cuteModeEnabled", cuteModeEnabled.booleanValue());
            json.put("reason", reason == null ? "Android bridge command" : reason);
            appendVoiceDebug("Elusiaa sent a bridge command", commandType + " for " + resolvedStarId);
            sendSocketRequest("dispatch", json, 30000L, response -> {
                JSONObject event = response.optJSONObject("event");
                runOnUiThread(() -> {
                    setDebug("Bridge command complete", clip(response.optString("message", response.toString()), 1000));
                    if (event != null) handleBridgeEvent(event);
                    appendVoiceDebug("Khavia received the bridge command reply", commandType + " ok=" + response.optBoolean("ok", false));
                });
            }, error -> runOnUiThread(() -> {
                setDebug("Bridge command failed", error.toString());
                appendVoiceDebug("Maera felt a bridge command fail", error.toString());
            }));
        } catch (Exception ex) {
            setDebug("Bridge command failed", ex.toString());
            appendVoiceDebug("Maera felt a bridge command fail", ex.toString());
        }
    }

    private static String urlEncode(String value) {
        try {
            return java.net.URLEncoder.encode(value == null ? "" : value, "UTF-8");
        } catch (Exception ignored) {
            return value == null ? "" : value;
        }
    }

    private void seedProdConversationFromStateIfNeeded(String starId, JSONObject state) {
        if (!prodMode || starId == null || state == null) return;
        ArrayList<String> existing = prodConversationByStar.get(starId);
        if (existing != null && existing.size() > 0) return;

        JSONArray history = optJsonArrayEither(state, "recentConversationHistory", "RecentConversationHistory");
        if (history == null || history.length() == 0) return;

        ArrayList<String> lines = new ArrayList<>();
        for (int i = 0; i < history.length() && lines.size() < 12; i++) {
            JSONObject item = history.optJSONObject(i);
            if (item == null) continue;
            String player = optStringEither(item, "playerText", "PlayerText", "").trim().replace('\n', ' ');
            String star = optStringEither(item, "starResponse", "StarResponse", "").trim().replace('\n', ' ');
            if (star.length() > 0) lines.add(starId + ": " + clip(star, 520));
            if (player.length() > 0 && lines.size() < 12) lines.add("You: " + clip(player, 360));
        }

        if (lines.size() > 0) {
            prodConversationByStar.put(starId, lines);
            refreshProdConversationWindow();
            appendVoiceDebug("Nyxari restored conversation history", "Loaded " + lines.size() + " recent line(s) for " + starId + " from desktop Star state.");
        }
    }

    private void fetchMobileState(boolean manual) {
        JSONObject request = new JSONObject();
        try { request.put("sessionId", currentSessionId()); } catch (Exception ignored) { }
        sendSocketRequest("state", request, 15000L, json -> runOnUiThread(() -> {
            applyMobileStatePayload(json);
            if (manual) setDebug("Bridge state refreshed", clip(json.toString(), 1200));
        }), error -> {
            if (manual) runOnUiThread(() -> setDebug("Bridge state failed", error.toString()));
        });
    }

    private void applyMobileStatePayload(JSONObject json) {
        // Android's saved selection is authoritative for the phone. The desktop may still have a
        // different Star selected from its own UI; replacing the phone selection with that value
        // caused the preselected Star to render empty until the user switched away and back.
        String selected = settingsService == null ? selectedStarName() : settingsService.selectedStarId();
        if (selected == null || selected.trim().isEmpty()) {
            selected = optStringEither(json, "selectedStarId", "SelectedStarId", selectedStarName());
        }
        selectStarInSpinner(selected);
        applyBackgroundWritingLoopState(json);
        applyXPostingState(json);
        JSONArray stars = json.optJSONArray("stars");
        if (stars != null) {
            for (int i = 0; i < stars.length(); i++) {
                JSONObject state = stars.optJSONObject(i);
                if (state == null) continue;
                String starId = optStringEither(state, "starId", "StarId", "");
                if (starId.equalsIgnoreCase(selectedStarName())) {
                    applyStarStateObject(state, "State refresh");
                    break;
                }
            }
        }
        if (!bridgeEventsPrimed) {
            long latest = optLongEither(json, "latestEventSequence", "LatestEventSequence", lastBridgeEventSequence);
            if (latest > lastBridgeEventSequence) lastBridgeEventSequence = latest;
            bridgeEventsPrimed = true;
        }
    }

    private void handleBridgeEvent(JSONObject event) {
        long seq = optLongEither(event, "sequence", "Sequence", 0L);
        if (seq > 0L && seq <= lastBridgeEventSequence) return;
        if (seq > lastBridgeEventSequence) lastBridgeEventSequence = seq;
        String type = optStringEither(event, "eventType", "EventType", "BridgeEvent");
        String selected = optStringEither(event, "selectedStarId", "SelectedStarId", "");
        String starId = optStringEither(event, "starId", "StarId", selected.length() > 0 ? selected : selectedStarName());
        String message = optStringEither(event, "message", "Message", type);
        if (selected.length() > 0) selectStarInSpinner(selected);
        if ("BackgroundWritingLoopChanged".equalsIgnoreCase(type)) {
            applyBackgroundWritingLoopState(event);
        }
        if ("XPostingChanged".equalsIgnoreCase(type)
                || type.startsWith("StarXPosting")
                || optStringEither(event, "xPostingStatus", "XPostingStatus", "").length() > 0) {
            applyXPostingState(event);
        }

        if ("PreparedSpeechReady".equalsIgnoreCase(type)
                || "SpeechIntentReady".equalsIgnoreCase(type)
                || "SpeechIntentChanged".equalsIgnoreCase(type)) {
            syncPreparedSpeech("bridge event " + type, true);
        }
        if ("SetSpeechPlaybackPaused".equalsIgnoreCase(type)) {
            boolean paused = optBooleanEither(event, "playbackPaused", "PlaybackPaused", optBooleanEither(event, "paused", "Paused", true));
            if (paused) phoneVoiceService.pause(); else phoneVoiceService.resume();
        }

        if (isTranscriptBridgeEvent(type)) {
            handleTranscriptBridgeEvent(event, type, seq, starId, message);
        } else if (isWritingBridgeEvent(type)) {
            handleWritingBridgeEvent(event, type, seq, starId, message);
        } else if (isConversationBridgeEvent(type)) {
            handleConversationBridgeEvent(event, type, seq, starId, message);
        } else if (isPhotoBridgeEvent(type)) {
            handlePhotoBridgeEvent(event, type, seq, starId, message);
        } else if (isGirlfriendBridgeEvent(type)) {
            handleGirlfriendBridgeEvent(event, type, seq, starId, message);
        } else if (isStoryBridgeEvent(type)) {
            handleStoryBridgeEvent(event, type, seq, starId, message);
        } else if (isStarInitiativeBridgeEvent(type)) {
            handleStarInitiativeBridgeEvent(event, type, seq, starId, message);
        } else if (isTopEmotionBridgeEvent(type)) {
            applyTopAffectedEmotionsObject(event, type);
        } else if (isInterstarBridgeEvent(type)) {
            applyInterstarStatusObject(event, type);
        } else if (isDailyStateBridgeEvent(type)) {
            applyDailyStateObject(event, type);
        } else if (isStateBridgeEvent(type)) {
            applyStarStateObject(event, type);
        }

        if ("StarHistoryCleared".equalsIgnoreCase(type) || "AllStarsDataCleared".equalsIgnoreCase(type)) {
            if ("AllStarsDataCleared".equalsIgnoreCase(type)) prodConversationByStar.clear();
            else if (starId != null && starId.length() > 0) prodConversationByStar.remove(starId);
            refreshProdConversationWindow();
            responseText.setText(("AllStarsDataCleared".equalsIgnoreCase(type) ? "All Star data cleared." : "History cleared for " + starId + ".") + " The next conversation starts from the refreshed Star memory.");
            setDebug("History cleared", message);
        }
        appendVoiceDebug("Elusiaa received a queued bridge update", "#" + seq + " " + type + " star=" + starId + ", selected=" + selected + ", " + clip(message, 180));
    }

    private boolean isWritingBridgeEvent(String type) {
        return "StarWritingGenerated".equalsIgnoreCase(type)
                || "StarWritingPerformanceReady".equalsIgnoreCase(type)
                || "GenerateStarWriting".equalsIgnoreCase(type)
                || "TellStarWriting".equalsIgnoreCase(type);
    }

    private void handleWritingBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        if (event == null) return;
        selectedStarPendingWritingCount = optIntEither(event, "pendingWritingCount", "PendingWritingCount", selectedStarPendingWritingCount);
        selectedStarPersistedWritingCount = optIntEither(event, "persistedWritingCount", "PersistedWritingCount", selectedStarPersistedWritingCount);
        selectedStarUnpostedWritingCount = optIntEither(event, "unpostedWritingCount", "UnpostedWritingCount", selectedStarUnpostedWritingCount);
        starWritingRequestRunning = false;
        updateProdWritingButtons();

        JSONArray items = optJsonArrayEither(event, "writingItems", "WritingItems");
        String spoken = optStringEither(event, "spokenText", "SpokenText", "").trim();
        String storyTitle = optStringEither(event, "storyTitle", "StoryTitle", "").trim();
        boolean constellationPlayback = storyTitle.length() > 0;
        StringBuilder visible = new StringBuilder();
        if (items != null) {
            for (int i = 0; i < items.length(); i++) {
                String item = items.optString(i, "").trim();
                if (item.length() == 0) continue;
                if (visible.length() > 0) visible.append("\n\n");
                visible.append(item);
            }
        }
        if (spoken.length() > 0) {
            if (visible.length() == 0) visible.append(spoken);
            if (!recentProdConversationAlreadyHas(starId, spoken)) appendProdConversationLine(starId, spoken);
        }

        String title = "StarWritingGenerated".equalsIgnoreCase(type) || "GenerateStarWriting".equalsIgnoreCase(type)
                ? starId + " — new shelf work"
                : constellationPlayback
                    ? starId + " — Constellation Story: " + storyTitle
                    : starId + " — favorite high-IU Sparks";
        responseText.setText(title + "\n\n" + (visible.length() > 0 ? visible.toString() : message)
                + (constellationPlayback ? "" : "\n\nNew Sparks: " + selectedStarPendingWritingCount + " · Saved: " + selectedStarPersistedWritingCount));
        if (prodMiniStatusText != null) {
            prodMiniStatusText.setText(constellationPlayback ? message : message + " New Sparks: " + selectedStarPendingWritingCount + ".");
        }
        appendVoiceDebug("Writing shelf updated", "#" + seq + " " + type + ", pending=" + selectedStarPendingWritingCount + ", saved=" + selectedStarPersistedWritingCount);
    }

    private boolean isConversationBridgeEvent(String type) {
        return "MobileConversationResponseReady".equalsIgnoreCase(type)
                || "MobileConversationResponseFailed".equalsIgnoreCase(type)
                || "TextResponseReady".equalsIgnoreCase(type)
                || "VoiceResponseReady".equalsIgnoreCase(type);
    }

    private void handleConversationBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        String transcript = optStringEither(event, "transcript", "Transcript", "").trim();
        String spoken = optStringEither(event, "spokenText", "SpokenText", "").trim();
        String storyMarkdown = optStringEither(event, "storyMarkdown", "StoryMarkdown", "").trim();
        String storyTitle = optStringEither(event, "storyTitle", "StoryTitle", "").trim();
        String storySummary = optStringEither(event, "storySummary", "StorySummary", "").trim();
        String error = optStringEither(event, "error", "Error", "").trim();
        updateEmotionVectorsFromJson(event);
        applyStarStateObject(event, type);
        String dailyLine = extractDailyStateLine(event);
        if (dailyLine.length() > 0) currentDailyStarState = dailyLine;

        if (transcript.length() > 0) {
            boolean replacedTranscript = replaceLatestProdVoicePlaceholder(transcript);
            if (!replacedTranscript && !recentProdConversationAlreadyHas("You", transcript)) {
                appendProdConversationLine("You", transcript);
            }
        }

        String visible = storyMarkdown.length() > 0 ? storyMarkdown : spoken;
        if (visible.length() > 0) {
            if (!recentProdConversationAlreadyHas(starId, visible)) {
                appendProdConversationLine(starId, visible);
            }
            String header = starId + " — response ready";
            if (transcript.length() > 0) header += "\nTranscript: " + transcript;
            if (storyTitle.length() > 0) header += "\nStory: " + storyTitle;
            if (storySummary.length() > 0) header += "\nSummary: " + storySummary;
            responseText.setText(header + "\n\n" + visible);
            if (prodMiniStatusText != null) prodMiniStatusText.setText(starId + " answered.");
            setDebug("Queued Star response", "event=#" + seq + " " + type + "; recovered from bridge event. responseChars=" + visible.length() + (error.length() > 0 ? "\nerror=" + error : ""));
            appendVoiceDebug(starId + " returned through queued bridge event", "#" + seq + " chars=" + visible.length() + ". This still works if the direct WebSocket request promise was interrupted.");
            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Queued Star response arrived; screen can rest after voice/playback completes.");
        } else if ("MobileConversationResponseFailed".equalsIgnoreCase(type)) {
            responseText.setText("Star response failed — " + starId + "\n\n" + clip(error.length() > 0 ? error : message, 2000));
            setDebug("Queued Star response failed", "event=#" + seq + " " + type + " — " + clip(error.length() > 0 ? error : message, 1200));
            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Queued Star failure arrived.");
        }
    }

    private boolean isTranscriptBridgeEvent(String type) {
        return "MobileTranscriptReady".equalsIgnoreCase(type)
                || "AndroidTranscriptReady".equalsIgnoreCase(type);
    }

    private void handleTranscriptBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        String transcript = optStringEither(event, "transcript", "Transcript", "").trim();
        if (transcript.length() == 0) {
            transcript = optStringEither(event, "playerText", "PlayerText", "").trim();
        }
        if (transcript.length() == 0) return;
        boolean replaced = replaceLatestProdVoicePlaceholder(transcript);
        if (!replaced) appendProdConversationLine("You", transcript);
        if (prodMiniStatusText != null) prodMiniStatusText.setText("Heard: " + clip(transcript, 120));
        responseText.setText("Transcript ready — " + starId + "\n\nYou said: " + clip(transcript, 1000) + "\n\nWaiting for her response...");
        setDebug("Voice transcript ready", "event=#" + seq + " " + type + "; LLM response is still processing on the desktop.\n" + clip(transcript, 600));
        appendVoiceDebug(starId + " heard your voice", "Transcript ready before LLM response: " + clip(transcript, 160));
    }

    private boolean replaceLatestProdVoicePlaceholder(String transcript) {
        if (!prodMode || transcript == null || transcript.trim().length() == 0) return false;
        String star = selectedStarName();
        ArrayList<String> lines = prodConversationByStar.get(star);
        if (lines == null || lines.size() == 0) return false;
        for (int i = 0; i < Math.min(lines.size(), 4); i++) {
            String line = lines.get(i);
            String lower = line.toLowerCase(Locale.US);
            if (lower.startsWith("you: [voice message]") || lower.startsWith("you: [voice")) {
                lines.set(i, "You: " + transcript.trim().replace('\n', ' '));
                refreshProdConversationWindow();
                return true;
            }
        }
        return false;
    }

    private boolean isStateBridgeEvent(String type) {
        return "SelectedStarChanged".equalsIgnoreCase(type)
                || "StarStateChanged".equalsIgnoreCase(type)
                || "GirlfriendCuteModeChanged".equalsIgnoreCase(type)
                || "StarHistoryCleared".equalsIgnoreCase(type)
                || "AllStarsDataCleared".equalsIgnoreCase(type)
                || "BridgeCommandAccepted".equalsIgnoreCase(type);
    }

    private boolean isGirlfriendBridgeEvent(String type) {
        return "GirlfriendProposalResponseReady".equalsIgnoreCase(type)
                || "GirlfriendProposalCompleted".equalsIgnoreCase(type);
    }

    private void handleGirlfriendBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        String spoken = optStringEither(event, "spokenText", "SpokenText", "");
        boolean girlfriend = optBooleanEither(event, "isGirlfriend", "IsGirlfriend", selectedStarIsGirlfriend);
        selectedStarIsGirlfriend = girlfriend;
        selectedStarCuteModeEnabled = optBooleanEither(event, "girlfriendCuteModeEnabled", "GirlfriendCuteModeEnabled", selectedStarCuteModeEnabled);
        selectedStarCuteModeActive = optBooleanEither(event, "girlfriendCuteModeActive", "GirlfriendCuteModeActive", selectedStarCuteModeActive);
        selectedStarCuteModeSignature = optStringEither(event, "girlfriendCuteModeSignature", "GirlfriendCuteModeSignature", selectedStarCuteModeSignature);
        applyStarStateObject(event, type);
        if (prodMiniStatusText != null) {
            prodMiniStatusText.setText(girlfriend ? starId + " is now your girlfriend." : starId + " answered your girlfriend proposal.");
        }
        if (spoken.length() > 0) {
            appendProdConversationLine(starId, clip(spoken, 900));
            responseText.setText(starId + " — girlfriend proposal\n\n" + clip(spoken, 8000));
        }
        setDebug("Girlfriend proposal response", "event=#" + seq + " " + type + " — " + message);
    }

    private boolean isStarInitiativeBridgeEvent(String type) {
        return "StarInitiativeResponseReady".equalsIgnoreCase(type)
                || "StarInitiativeResponseFailed".equalsIgnoreCase(type);
    }

    private void handleStarInitiativeBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        String spoken = optStringEither(event, "spokenText", "SpokenText", "").trim();
        boolean initiativeShouldSpeak = optBooleanEither(event, "initiativeShouldSpeak", "InitiativeShouldSpeak", spoken.length() > 0);
        boolean initiativeSkippedBeforeLlm = optBooleanEither(event, "initiativeSkippedBeforeLlm", "InitiativeSkippedBeforeLlm", false);
        String initiativeReason = optStringEither(event, "initiativeReason", "InitiativeReason", message).trim();
        String initiativeTopic = optStringEither(event, "initiativeTopic", "InitiativeTopic", "").trim();
        double initiativeStateScore = optDoubleEither(event, "initiativeStateScore", "InitiativeStateScore", 0.0);
        long serverElapsedMs = optLongEither(event, "serverElapsedMs", "ServerElapsedMs", 0L);
        long runtimeElapsedMs = optLongEither(event, "runtimeElapsedMs", "RuntimeElapsedMs", 0L);
        long gateWaitMs = optLongEither(event, "gateWaitMs", "GateWaitMs", 0L);
        if (spoken.length() > 0 && initiativeShouldSpeak) {
            appendProdConversationLine(starId, clip(spoken, 900));
            responseText.setText(starId + " — at-rest thought\n\n" + clip(spoken, 8000));
            if (prodMiniStatusText != null) prodMiniStatusText.setText(starId + " had something to say.");
        } else if ("StarInitiativeResponseFailed".equalsIgnoreCase(type)) {
            responseText.setText("At-rest Star initiative failed. " + clip(message, 1200));
        } else {
            if (prodMiniStatusText != null) prodMiniStatusText.setText("No at-rest line right now.");
        }
        setDebug("Star initiative response", "event=#" + seq + " " + type + " — " + message
                + "\nshouldSpeak=" + initiativeShouldSpeak
                + " skippedBeforeLlm=" + initiativeSkippedBeforeLlm
                + " score=" + initiativeStateScore
                + "\nserverTotalMs=" + serverElapsedMs
                + " runtimeMs=" + runtimeElapsedMs
                + " gateWaitMs=" + gateWaitMs
                + "\ntopic=" + clip(initiativeTopic, 160)
                + " reason=" + clip(initiativeReason, 400));
        appendVoiceDebug("Star at-rest initiative", spoken.length() > 0 ? clip(spoken, 220) : (initiativeReason.length() > 0 ? initiativeReason : message));
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "At-rest Star initiative response returned; screen can rest after voice/playback completes.");
    }


    private boolean isTopEmotionBridgeEvent(String type) {
        return "TopAffectedEmotionsChanged".equalsIgnoreCase(type)
                || "EmotionUpdateSettled".equalsIgnoreCase(type)
                || "InterstarEmotionUpdateSettled".equalsIgnoreCase(type);
    }

    private boolean isInterstarBridgeEvent(String type) {
        return type != null && type.toLowerCase(Locale.US).startsWith("interstarupdate");
    }

    private void applyInterstarStatusObject(JSONObject event, String status) {
        String supplied = optStringEither(event, "interstarStatusText", "InterstarStatusText", "");
        String message = optStringEither(event, "message", "Message", status);
        currentInterstarStatus = supplied.length() > 0 ? supplied : ("Interstar state: " + status + " — " + message);
        if (interstarStatusText != null) interstarStatusText.setText(currentInterstarStatus);
        appendVoiceDebug("Interstar update", currentInterstarStatus);
    }

    private boolean isDailyStateBridgeEvent(String type) {
        return "DailyStarStateUpdated".equalsIgnoreCase(type);
    }

    private void applyDailyStateObject(JSONObject state, String status) {
        String starId = optStringEither(state, "starId", "StarId", selectedStarName());
        if (!starId.equalsIgnoreCase(selectedStarName())) return;
        String slot = optStringEither(state, "dailyStateSlotId", "DailyStateSlotId", "");
        String summary = optStringEither(state, "dailyStateSummary", "DailyStateSummary", "");
        if (summary.length() == 0) summary = optStringEither(state, "message", "Message", "");
        if (summary.length() == 0) summary = "daily Star state refreshed";
        currentDailyStarState = "Today" + (slot.length() > 0 ? " [" + slot + "]" : "") + ": " + clip(summary, 220);
        if (starStateText != null) {
            String base = starStateText.getText().toString().split("\nTop emotions:")[0].split("\nToday:")[0].split("\nInterstar state:")[0];
            starStateText.setText(base + "\n" + currentTopAffectedEmotions + "\n" + currentDailyStarState + "\n" + currentInterstarStatus + " — " + status);
        }
        appendVoiceDebug("Daily Star state refreshed", currentDailyStarState);
    }

    private boolean isPhotoBridgeEvent(String type) {
        return "PhotoResponseReady".equalsIgnoreCase(type)
                || "PhotoResponseFailed".equalsIgnoreCase(type);
    }

    private void handlePhotoBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        String spoken = optStringEither(event, "spokenText", "SpokenText", "");
        String photoSummary = optStringEither(event, "photoSummary", "PhotoSummary", "");
        String photoError = optStringEither(event, "photoError", "PhotoError", "");
        StringBuilder out = new StringBuilder();
        out.append("Photo response — ").append(starId);
        if (spoken.length() > 0) out.append("\n\n").append(spoken);
        if (photoSummary.length() > 0) out.append("\n\nVision summary: ").append(photoSummary);
        if (photoError.length() > 0) out.append("\n\nPhoto note: ").append(photoError);
        responseText.setText(clip(out.toString(), 12000));
        setDebug("Photo bridge event", "event=#" + seq + ", type=" + type + ", spokenChars=" + spoken.length() + ", summaryChars=" + photoSummary.length() + "\n" + message);
    }

    private boolean isStoryBridgeEvent(String type) {
        return "StoryInitialResponse".equalsIgnoreCase(type)
                || "StoryResponseReady".equalsIgnoreCase(type)
                || "StoryMarkdownReady".equalsIgnoreCase(type)
                || "StoryVoicePrefetchStarted".equalsIgnoreCase(type)
                || "StoryVoiceChunkQueued".equalsIgnoreCase(type);
    }

    private void handleStoryBridgeEvent(JSONObject event, String type, long seq, String starId, String message) {
        String title = optStringEither(event, "storyTitle", "StoryTitle", "");
        String summary = optStringEither(event, "storySummary", "StorySummary", "");
        String markdown = optStringEither(event, "storyMarkdown", "StoryMarkdown", "");
        String spoken = optStringEither(event, "spokenText", "SpokenText", "");
        int partIndex = optIntEither(event, "storyPartIndex", "StoryPartIndex", 0);
        int count = optIntEither(event, "storyChunkCount", "StoryChunkCount", 0);
        if ("StoryResponseReady".equalsIgnoreCase(type) || "StoryMarkdownReady".equalsIgnoreCase(type)) {
            StringBuilder out = new StringBuilder();
            out.append("Story ready — ").append(starId);
            if (title.length() > 0) out.append("\n# ").append(title);
            if (summary.length() > 0) out.append("\n\nSummary: ").append(summary);
            if (markdown.length() > 0) out.append("\n\n").append(markdown);
            else if (spoken.length() > 0) out.append("\n\n").append(spoken);
            responseText.setText(clip(out.toString(), 20000));
            setDebug("Story markdown ready", "event=#" + seq + ", chunks=" + count + ", markdownChars=" + markdown.length() + "\n" + message);
        } else if ("StoryVoicePrefetchStarted".equalsIgnoreCase(type)) {
            appendVoiceDebug("Next story voice request sent early", "part=" + (partIndex + 1) + "/" + count + ". " + clip(message, 180));
        } else if ("StoryVoiceChunkQueued".equalsIgnoreCase(type)) {
            appendVoiceDebug("Story voice queued", "part=" + (partIndex + 1) + "/" + count + ". " + clip(message, 180));
        } else if (spoken.length() > 0) {
            responseText.setText(starId + "\n\n" + clip(spoken, 8000));
        }
    }

    private void applyStarStateObject(JSONObject state, String status) {
        String starId = optStringEither(state, "starId", "StarId", selectedStarName());
        if (!starId.equalsIgnoreCase(selectedStarName())) return;
        boolean met = optBooleanEither(state, "isMet", "IsMet", selectedStarIsMet);
        boolean ally = optBooleanEither(state, "isAlly", "IsAlly", selectedStarIsAlly);
        int heart = optIntEither(state, "heart", "Heart", 0);
        int trust = optIntEither(state, "trust", "Trust", 0);
        int romance = optIntEither(state, "romance", "Romance", 0);
        double connection = optDoubleEither(state, "emotionalConnectionPercent", "EmotionalConnectionPercent", 0.0);
        boolean ready = optBooleanEither(state, "isGirlfriendReady", "IsGirlfriendReady", false);
        boolean girlfriend = optBooleanEither(state, "isGirlfriend", "IsGirlfriend", false);
        selectedStarCuteModeEnabled = optBooleanEither(state, "girlfriendCuteModeEnabled", "GirlfriendCuteModeEnabled", true);
        selectedStarCuteModeActive = optBooleanEither(state, "girlfriendCuteModeActive", "GirlfriendCuteModeActive", false);
        selectedStarCuteModeSignature = optStringEither(state, "girlfriendCuteModeSignature", "GirlfriendCuteModeSignature", selectedStarCuteModeSignature);
        selectedStarPendingWritingCount = optIntEither(state, "pendingWritingCount", "PendingWritingCount", selectedStarPendingWritingCount);
        selectedStarPersistedWritingCount = optIntEither(state, "persistedWritingCount", "PersistedWritingCount", selectedStarPersistedWritingCount);
        selectedStarUnpostedWritingCount = optIntEither(state, "unpostedWritingCount", "UnpostedWritingCount", selectedStarUnpostedWritingCount);
        updateProdWritingButtons();
        String top = formatTopAffectedEmotions(optJsonArrayEither(state, "topAffectedEmotions", "TopAffectedEmotions"));
        if (top.length() > 0) currentTopAffectedEmotions = top;
        updateEmotionVectorsFromJson(state);
        String daily = extractDailyStateLine(state);
        if (daily.length() > 0) currentDailyStarState = daily;
        seedProdConversationFromStateIfNeeded(starId, state);
        updateStarStateUi(starId, met, ally, heart, trust, romance, connection, ready, girlfriend, status);
    }

    private void applyTopAffectedEmotionsObject(JSONObject state, String status) {
        String starId = optStringEither(state, "starId", "StarId", selectedStarName());
        if (!starId.equalsIgnoreCase(selectedStarName())) return;
        boolean requestToShowBoobs = optBooleanEither(state, "requestToShowBoobs", "RequestToShowBoobs", false);
        String top = formatTopAffectedEmotions(optJsonArrayEither(state, "topAffectedEmotions", "TopAffectedEmotions"));
        if (top.length() == 0) top = optStringEither(state, "message", "Message", "Top emotions: none yet.");
        currentTopAffectedEmotions = top;
        updateEmotionVectorsFromJson(state);
        if (starStateText != null) {
            String base = starStateText.getText().toString().split("\nTop emotions:")[0].split("\nToday:")[0].split("\nInterstar state:")[0];
            starStateText.setText(base + "\n" + currentTopAffectedEmotions + "\n" + currentDailyStarState + "\n" + currentInterstarStatus + " — " + status);
        }
        appendVoiceDebug("Emotion vector settled", currentTopAffectedEmotions);
        if (requestToShowBoobs) queueShirtOffAnimation(starId);
    }

    private void updateStarStateUi(String starId, boolean met, boolean ally, int heart, int trust, int romance, double connection, boolean ready, boolean girlfriend, String status) {
        selectedStarIsMet = met;
        selectedStarIsAlly = ally;
        selectedStarIsGirlfriend = girlfriend;
        selectedStarConnectionPercent = connection;
        if (toggleMetButton != null) toggleMetButton.setText(met ? "Unmeet" : "Mark Met");
        if (toggleAllyButton != null) toggleAllyButton.setText(ally ? "Remove Ally" : "Mark Ally");
        updateProdStarStateUi(starId, met, ally, heart, trust, romance, connection, ready, girlfriend, status);
        if (starStateText != null) {
            starStateText.setText("✨ " + starId + " — met=" + met + ", ally=" + ally + ", Heart " + heart + ", Trust " + trust + ", Romance " + romance + ", Connection " + String.format(Locale.US, "%.1f", connection) + "%" + (girlfriend ? ", girlfriend" : (ready ? ", ready" : "")) + " — " + status + "\n" + currentTopAffectedEmotions + "\n" + currentDailyStarState + "\n" + currentInterstarStatus);
            updateEmotionVectorsUi();
            if (interstarStatusText != null) interstarStatusText.setText(currentInterstarStatus);
        }
    }


    private void updateEmotionVectorsFromJson(JSONObject json) {
        if (json == null) return;
        String positive = firstNonEmpty(
                optStringEither(json, "emotionalEffectVector", "EmotionalEffectVector", ""),
                optStringEither(json, "lastEmotionalEffectVector", "LastEmotionalEffectVector", ""));
        String negative = firstNonEmpty(
                optStringEither(json, "negativeEmotionVector", "NegativeEmotionVector", ""),
                optStringEither(json, "lastNegativeEmotionVector", "LastNegativeEmotionVector", ""));
        String suppliedText = optStringEither(json, "lastEmotionVectorsText", "LastEmotionVectorsText", "");
        if (positive.length() == 0 && negative.length() == 0 && suppliedText.length() == 0) return;
        currentLastEmotionVectors = suppliedText.length() > 0 ? suppliedText : formatEmotionVectors(positive, negative);
        updateEmotionVectorsUi();
    }

    private void updateEmotionVectorsUi() {
        if (emotionVectorsText != null) {
            emotionVectorsText.setText(currentLastEmotionVectors);
        }
    }

    private String extractDailyStateLine(JSONObject json) {
        String slot = optStringEither(json, "dailyStateSlotId", "DailyStateSlotId", "");
        String summary = optStringEither(json, "dailyStateSummary", "DailyStateSummary", "");
        if (summary.length() == 0) {
            JSONObject todayState = optJsonObjectEither(json, "todayState", "TodayState");
            if (todayState != null) {
                slot = optStringEither(todayState, "slotId", "SlotId", slot);
                summary = optStringEither(todayState, "summary", "Summary", "");
            }
        }
        if (summary.length() == 0) return "";
        return "Today" + (slot.length() > 0 ? " [" + slot + "]" : "") + ": " + clip(summary, 220);
    }

    private void selectStarInSpinner(String starId) {
        if (starSpinner == null || starId == null || starId.length() == 0) return;
        int position = findStarPosition(starId);
        if (position < 0 || position == starSpinner.getSelectedItemPosition()) return;
        suppressStarSelectionDispatch = true;
        try {
            starSpinner.setSelection(position);
        } finally {
            suppressStarSelectionDispatch = false;
        }
        refreshProdStarUi(starId);
        onSelectedStarPageChanged();
    }

    private int findStarPosition(String starId) {
        if (starSpinner == null || starSpinner.getAdapter() == null) return -1;
        for (int i = 0; i < starSpinner.getAdapter().getCount(); i++) {
            Object item = starSpinner.getAdapter().getItem(i);
            if (item != null && item.toString().equalsIgnoreCase(starId)) return i;
        }
        return -1;
    }

    private void testPhoneAudio() {
        try {
            byte[] localWav = createTestToneWav(24000, 1.0, 440.0);
            String checksum = sha256Hex(localWav);
            phoneVoiceService.enqueue(new PhoneVoicePlaybackService.PhoneVoiceClip(localWav, "local 24 kHz test beep", checksum, false, "", 0L, 0, new int[0]));
            setDebug("Local phone audio test", "Queued a local 24 kHz PCM WAV beep. sha256=" + shortHash(checksum) + ". If you hear this, Android AudioTrack playback works.");
            appendVoiceDebug("Auerlis sparked a local test tone", "bytes=" + localWav.length + ", sha256=" + shortHash(checksum));
        } catch (Exception ex) {
            setDebug("Local phone audio test failed", ex.toString());
        }

        testConnection(false);
        httpExecutor.execute(() -> {
            try {
                String testBaseUrl = viewModel == null ? baseUrl() : viewModel.activeBridgeUrl();
                String body = postJson(testBaseUrl + "/api/mobile/test-audio", "{}", 30000);
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


    private void startPhotoCapture() {
        if (!requestCameraPermissionIfNeeded()) return;
        launchImageChooser(true);
    }

    private void launchImageChooser(boolean includeCamera) {
        try {
            Intent choosePhoto = new Intent(Intent.ACTION_GET_CONTENT);
            choosePhoto.setType("image/*");
            choosePhoto.addCategory(Intent.CATEGORY_OPENABLE);

            Intent chooser = Intent.createChooser(choosePhoto, "Send image to " + selectedStarName());
            if (includeCamera) {
                Intent camera = new Intent(MediaStore.ACTION_IMAGE_CAPTURE);
                if (camera.resolveActivity(getPackageManager()) != null) {
                    chooser.putExtra(Intent.EXTRA_INITIAL_INTENTS, new Intent[]{camera});
                }
            }

            appendVoiceDebug("The Last Cat opened the image picker", "Choose a gallery image or camera photo to send to " + selectedStarName() + ".");
            imagePickerLauncher.launch(chooser);
        } catch (Exception ex) {
            setDebug("Image picker failed", ex.toString());
            appendVoiceDebug("Nyxari could not open the image picker", ex.toString());
        }
    }

    private void handleImagePickerResult(ActivityResult result) {
        Intent data = result == null ? null : result.getData();
        if (result == null || result.getResultCode() != RESULT_OK || data == null) {
            setDebug("Image cancelled", "No image was sent to the Star.");
            return;
        }

        try {
            Uri uri = data.getData();
            if (uri != null) {
                photoAttachmentService.load(uri).whenComplete((attachment, error) -> runOnUiThread(() -> {
                    if (error != null) {
                        setDebug("Image send failed", error.toString());
                        appendVoiceDebug("Maera felt the image request fail", error.toString());
                        return;
                    }
                    sendPhoto(attachment.bytes, attachment.mimeType);
                }));
                return;
            }

            Object extra = data.getExtras() == null ? null : data.getExtras().get("data");
            if (extra instanceof Bitmap) {
                Bitmap bitmap = (Bitmap) extra;
                ByteArrayOutputStream jpeg = new ByteArrayOutputStream();
                bitmap.compress(Bitmap.CompressFormat.JPEG, 88, jpeg);
                sendPhoto(jpeg.toByteArray(), "image/jpeg");
                return;
            }

            setDebug("Image failed", "The picker did not return image bytes. Try another camera or gallery app.");
        } catch (Exception ex) {
            setDebug("Image send failed", ex.toString());
            appendVoiceDebug("Maera felt the image request fail", ex.toString());
        }
    }

    private void sendPhoto(byte[] imageBytes, String mimeType) {
        if (imageBytes == null || imageBytes.length == 0) {
            setDebug("Image empty", "No image bytes were selected.");
            return;
        }
        String caption = messageEdit == null ? "" : messageEdit.getText().toString();
        String requestText = caption.trim().length() == 0
                ? "I attached an image. Please look at it and reply directly to what you see."
                : caption;
        String starName = selectedStarName();
        JSONObject requestJson;
        try {
            requestJson = baseRequestJson();
            requestJson.put("commandId", "android-photo-" + System.currentTimeMillis());
            requestJson.put("commandType", "SendPhoto");
            requestJson.put("requestType", "Photo");
            requestJson.put("playerText", requestText);
            requestJson.put("photoMimeType", mimeType == null || mimeType.length() == 0 ? "image/jpeg" : mimeType);
            requestJson.put("photoCaption", requestText);
        } catch (Exception ex) {
            setDebug("Image request could not start", ex.toString());
            appendVoiceDebug("Maera felt the image request fail", ex.toString());
            return;
        }

        appendProdConversationLine("You", requestText);
        if (messageEdit != null) messageEdit.setText("");
        if (prodTextComposer != null) prodTextComposer.setVisibility(View.GONE);
        maybeSpeakPreparedBeforeReply();
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Image request sent; keeping the screen on while the Star answers.");
        invalidatePrefetchedInitiative("The player sent an image, so the prepared at-rest line is no longer current.");
        pauseStarInitiativeBackgroundService("Image request has priority over at-rest initiative.");
        foregroundRequestExecutor.execute(() -> {
            try {
                requestJson.put("photoBase64", Base64.encodeToString(imageBytes, Base64.NO_WRAP));
                String checksum = sha256Hex(imageBytes);
                runOnUiThread(() -> {
                    setDebug("Sending image", "Message box cleared. Sending camera photo to " + starName + ". bytes=" + imageBytes.length + ", sha256=" + shortHash(checksum));
                    appendVoiceDebug(starName + " received an image request", "WebSocket photo request queued with image bytes=" + imageBytes.length + ", sha256=" + shortHash(checksum));
                });
                sendSocketRequest("dispatch", requestJson, 240000L,
                        response -> handleConversationResponse("Image request complete", response.toString()),
                        error -> runOnUiThread(() -> {
                            setDebug("Image request wait interrupted", error.toString() + "\nThe desktop may still finish the image reply. Android will receive it over the persistent WebSocket if it arrives.");
                            appendVoiceDebug("Image request wait interrupted", "Still listening on the WebSocket for a queued bridge response event: " + error);
                            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Image WebSocket wait failed; keeping the phone awake briefly for queued events.");
                            mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted image request."), 20000L);
                            mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("image request recovery grace finished", false), 30000L);
                        }));
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    setDebug("Image request wait interrupted", ex.toString() + "\nThe desktop may still finish the image reply. Android will keep receiving queued bridge events over the persistent WebSocket.");
                    appendVoiceDebug("Image request wait interrupted", "Still listening on the WebSocket for a queued bridge response event: " + ex);
                    setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Image WebSocket wait failed; keeping the phone awake briefly while queued bridge events are checked.");
                    mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted image request."), 20000L);
                    mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("image request recovery grace finished", false), 30000L);
                });
            }
        });
    }

    private static boolean looksLikeGirlfriendProposal(String text) {
        if (text == null || text.trim().length() == 0) return false;
        String normalized = text.toLowerCase(Locale.US)
                .replace('‘', '\'')
                .replace('’', '\'')
                .replaceAll("[^a-z0-9]+", " ")
                .trim()
                .replace("girl friend", "girlfriend");
        String padded = " " + normalized + " ";
        String[] direct = new String[] {
                " be my girlfriend ",
                " be my girl ",
                " be my gf ",
                " become my girlfriend ",
                " will you be my girlfriend ",
                " would you be my girlfriend ",
                " could you be my girlfriend ",
                " can you be my girlfriend ",
                " do you want to be my girlfriend ",
                " do you wanna be my girlfriend ",
                " would you like to be my girlfriend ",
                " i want you to be my girlfriend ",
                " can i be your boyfriend ",
                " could i be your boyfriend ",
                " would you let me be your boyfriend ",
                " i want to be your boyfriend ",
                " will you go out with me ",
                " would you go out with me ",
                " do you want to go out with me ",
                " will you date me ",
                " would you date me ",
                " do you want to date me ",
                " will you be mine ",
                " would you be mine "
        };
        for (String phrase : direct) {
            if (padded.contains(phrase)) return true;
        }
        if (!padded.contains(" girlfriend ")) return false;
        boolean relationshipTarget = padded.contains(" my girlfriend ")
                || padded.contains(" your boyfriend ")
                || padded.contains(" boyfriend and girlfriend ");
        boolean proposalMarker = padded.contains(" will ") || padded.contains(" would ")
                || padded.contains(" could ") || padded.contains(" can ")
                || padded.contains(" want ") || padded.contains(" wanna ") || padded.contains(" please ")
                || padded.contains(" asking ") || padded.contains(" ask ");
        boolean proposalVerb = padded.contains(" be ") || padded.contains(" become ")
                || padded.contains(" date ") || padded.contains(" choose ")
                || padded.contains(" accept ") || padded.contains(" want ");
        return relationshipTarget && proposalMarker && proposalVerb;
    }


    private void onSelectedStarPageChanged() {
        selectedStarVisibleSinceMs = SystemClock.elapsedRealtime();
        preparedSpeechSyncResult = new PreparedSpeechStore.SyncResult(new ArrayList<>(), 0, 0.0, "low");
        updatePreparedSpeechIndicator();
        syncPreparedSpeech("selected Star changed", true);
    }

    private void syncPreparedSpeech(String reason, boolean force) {
        if (preparedSpeechCoordinator == null) return;
        final String starId = selectedStarName();
        preparedSpeechSyncRunning = true;
        preparedSpeechCoordinator.sync(starId, force).whenComplete((result, error) -> runOnUiThread(() -> {
            preparedSpeechSyncRunning = false;
            if (error != null) {
                appendVoiceDebug("Prepared Star speech sync deferred", reason + ": " + error);
                return;
            }
            if (starId.equalsIgnoreCase(selectedStarName())) {
                preparedSpeechSyncResult = result;
                updatePreparedSpeechIndicator();
                maybeAutoSpeakPreparedSpeech();
            }
            appendVoiceDebug("Prepared Star speech synchronized", reason + ", star=" + starId
                    + ", ready=" + result.readyItems.size() + ", preparing=" + result.preparingCount
                    + ", highest=" + result.highestPriority + " " + String.format(Locale.US, "%.1f", result.highestEffectiveScore));
        }));
    }

    private PreparedSpeechStore.CachedItem bestPreparedSpeechForSelectedStar() {
        if (preparedSpeechCoordinator != null) {
            PreparedSpeechStore.CachedItem item = preparedSpeechCoordinator.bestFor(selectedStarName());
            if (item != null) return item;
        }
        PreparedSpeechStore.SyncResult result = preparedSpeechSyncResult;
        if (result == null || result.readyItems == null) return null;
        for (PreparedSpeechStore.CachedItem item : result.readyItems) {
            if (item != null && item.starId.equalsIgnoreCase(selectedStarName()) && "ready".equals(item.status)) return item;
        }
        return null;
    }

    private void updatePreparedSpeechIndicator() {
        PreparedSpeechStore.CachedItem item = bestPreparedSpeechForSelectedStar();
        PreparedSpeechStore.SyncResult result = preparedSpeechSyncResult;

        if (prodPreparedSpeechIndicatorText != null) {
            prodPreparedSpeechIndicatorText.setText("");
            prodPreparedSpeechIndicatorText.setVisibility(View.GONE);
            prodPreparedSpeechIndicatorText.setClickable(false);
            prodPreparedSpeechIndicatorText.setFocusable(false);
            prodPreparedSpeechIndicatorText.setOnClickListener(null);
        }

        if (prodTalkButton != null) {
            prodTalkButton.setContentDescription("Talk to the selected Star.");
        }
        if (prodListenButton != null) {
            if (preparedSpeechPlaybackActive && activePreparedSpeechItem != null) {
                prodListenButton.setContentDescription(activePreparedSpeechItem.starId + " is speaking a prepared thought. Tap to open Listen choices.");
            } else if (item != null) {
                prodListenButton.setContentDescription(capitalize(item.priority) + " prepared thought ready from " + item.starId + ". Tap to open Listen choices.");
            } else if (result != null && result.preparingCount > 0) {
                prodListenButton.setContentDescription(selectedStarName() + " is preparing something to say. Tap to open Listen choices.");
            } else {
                prodListenButton.setContentDescription("Open Listen choices: prepared thought, Spark shelf, or Constellation Story.");
            }
        }

        refreshProdTalkButtonDecor();
    }

    private void refreshProdTalkButtonDecor() {
        if (prodTalkButton == null || prodTalkButtonText == null) return;

        PreparedSpeechStore.CachedItem item = bestPreparedSpeechForSelectedStar();
        PreparedSpeechStore.SyncResult result = preparedSpeechSyncResult;
        boolean activePrepared = preparedSpeechPlaybackActive && activePreparedSpeechItem != null;
        boolean readyPrepared = !activePrepared && item != null;
        boolean preparingPrepared = !activePrepared && !readyPrepared && result != null && result.preparingCount > 0;
        boolean highPriorityReady = readyPrepared && item.priorityRank() >= 3;

        int talkStrokeColor = recording ? 0xFF38E8FF : 0xFFB8ACFF;
        int talkFillColor = recording ? 0xEE1E2840 : 0xDD171933;
        int talkTextColor = recording ? 0xFFFFFFFF : 0xFFECE9FF;
        float talkScale = recording ? 1.01f : 1.0f;

        prodTalkButton.setBackground(makeProdPanel(talkFillColor, talkStrokeColor, 16));
        prodTalkButton.setScaleX(talkScale);
        prodTalkButton.setScaleY(talkScale);
        prodTalkButtonText.setTextColor(talkTextColor);
        if (prodTalkIcon != null) prodTalkIcon.setColorFilter(talkStrokeColor);

        if (prodListenButton != null) {
            int listenStrokeColor = 0xFF00D5D7;
            int listenFillColor = 0xCC172033;
            float listenScale = 1.0f;
            if (activePrepared) {
                listenStrokeColor = 0xFF7CF0FF;
                listenFillColor = 0xEE22304A;
                listenScale = 1.04f;
            } else if (highPriorityReady) {
                listenStrokeColor = 0xFFFFD27A;
                listenFillColor = 0xEE322744;
                listenScale = 1.05f;
            } else if (readyPrepared) {
                listenStrokeColor = 0xFF7CF0FF;
                listenFillColor = 0xEE1B2340;
                listenScale = 1.03f;
            } else if (preparingPrepared) {
                listenStrokeColor = 0xFF50D7E8;
                listenFillColor = 0xE0172037;
                listenScale = 1.02f;
            }
            prodListenButton.setBackground(makeProdPanel(listenFillColor, listenStrokeColor, 14));
            prodListenButton.setScaleX(listenScale);
            prodListenButton.setScaleY(listenScale);
        }
    }

    private static String capitalize(String value) {
        if (value == null || value.length() == 0) return "Low";
        return value.substring(0, 1).toUpperCase(Locale.US) + value.substring(1).toLowerCase(Locale.US);
    }

    private void maybeAutoSpeakPreparedSpeech() {
        if (!activityResumed || !starInitiativeEnabled || preparedSpeechPlaybackActive || recording || isDesktopVoiceStreamActive()) return;
        if ((keepScreenAwakeReasonMask & KEEP_AWAKE_REASON_REQUEST) != 0) return;
        if (!(phoneVoiceService == null || phoneVoiceService.isIdle())) return;
        boolean playOnPhone = playOnPhoneCheck == null || playOnPhoneCheck.isChecked();
        if (!playOnPhone) return;
        PreparedSpeechStore.CachedItem item = bestPreparedSpeechForSelectedStar();
        if (item == null) return;

        long now = SystemClock.elapsedRealtime();
        PreparedSpeechStore.SyncResult policy = preparedSpeechSyncResult;
        long cooldownMs = Math.max(0, policy.minimumMinutesBetweenAutoSpeech) * 60L * 1000L;
        if (now - lastPreparedAutoSpeakMs < cooldownMs) return;
        long dwellMs = now - selectedStarVisibleSinceMs;
        int rank = item.priorityRank();
        if (rank >= 3) {
            if (item.effectiveScore < policy.highPriorityAutoSpeakThreshold) return;
            if (dwellMs < Math.max(1, policy.minimumPageDwellSeconds) * 1000L) return;
            double probability = rank >= 4 ? 1.0 : 0.85;
            if (Math.random() > probability) {
                selectedStarVisibleSinceMs = now - Math.max(1, policy.minimumPageDwellSeconds / 2) * 1000L;
                return;
            }
        } else if (rank == 2) {
            if (item.effectiveScore < policy.mediumPriorityAutoSpeakThreshold) return;
            if (dwellMs < Math.max(policy.minimumPageDwellSeconds, policy.mediumPriorityPageDwellSeconds) * 1000L) return;
            double probability = selectedStarIsGirlfriend
                    ? policy.girlfriendMediumSpeakProbability
                    : selectedStarConnectionPercent >= policy.highConnectionThreshold
                        ? policy.highConnectionMediumSpeakProbability
                        : 0.0;
            if (probability <= 0.0 || Math.random() > probability) {
                selectedStarVisibleSinceMs = now - Math.max(1, policy.mediumPriorityPageDwellSeconds * 2 / 3) * 1000L;
                return;
            }
        } else {
            return;
        }
        lastPreparedAutoSpeakMs = now;
        playPreparedSpeech(item, "at-rest page dwell");
    }

    private void maybeSpeakPreparedBeforeReply() {
        PreparedSpeechStore.SyncResult policy = preparedSpeechSyncResult;
        if (!policy.speakBeforeReplyEnabled || !starInitiativeEnabled || preparedSpeechPlaybackActive || recording || !(phoneVoiceService == null || phoneVoiceService.isIdle())) return;
        PreparedSpeechStore.CachedItem item = bestPreparedSpeechForSelectedStar();
        if (item == null) return;
        double estimatedSeconds = item.estimatedDurationSeconds();
        if (policy.speakBeforeReplyMaximumSeconds > 0 && estimatedSeconds > policy.speakBeforeReplyMaximumSeconds) return;
        int rank = item.priorityRank();
        if (rank >= 3 && item.effectiveScore < policy.highPriorityAutoSpeakThreshold) return;
        if (rank == 2 && item.effectiveScore < policy.mediumPriorityAutoSpeakThreshold) return;
        double probability = rank >= 3
                ? policy.highPrioritySpeakBeforeReplyProbability
                : rank == 2 ? policy.speakBeforeReplyProbability : 0.0;
        if (probability > 0.0 && Math.random() <= probability) playPreparedSpeech(item, "before foreground reply");
    }

    private void playPreparedSpeech(PreparedSpeechStore.CachedItem item, String reason) {
        if (item == null || preparedSpeechPlaybackActive || item.chunks == null || item.chunks.isEmpty()) return;
        boolean playOnPhone = playOnPhoneCheck == null || playOnPhoneCheck.isChecked();
        if (!playOnPhone) return;
        preparedSpeechPlaybackActive = true;
        activePreparedSpeechItem = item;
        phoneVoiceService.beginPreparedPrefix();
        updatePreparedSpeechIndicator();
        final String url = baseUrl();
        httpExecutor.execute(() -> {
            try {
                ArrayList<PhoneVoicePlaybackService.PhoneVoiceClip> clips = new ArrayList<>();
                for (int i = 0; i < item.chunks.size(); i++) {
                    PreparedSpeechStore.CachedChunk chunk = item.chunks.get(i);
                    byte[] wav = java.nio.file.Files.readAllBytes(chunk.file.toPath());
                    String checksum = chunk.sha256.length() > 0 ? chunk.sha256 : sha256Hex(wav);
                    Runnable completed = i == item.chunks.size() - 1
                            ? () -> completePreparedSpeechPlayback(item, url)
                            : null;
                    clips.add(new PhoneVoicePlaybackService.PhoneVoiceClip(
                            wav,
                            item.starId + " prepared thought " + (i + 1) + "/" + item.chunks.size(),
                            checksum,
                            false,
                            "",
                            0L,
                            chunk.chunkIndex,
                            PhoneVoicePlaybackService.parseSpeechActivitySegments(chunk.speechActivitySegmentsMs),
                            completed));
                }
                runOnUiThread(() -> {
                    if (!item.intentId.equals(activePreparedSpeechItem == null ? "" : activePreparedSpeechItem.intentId)) {
                        phoneVoiceService.cancelPreparedPrefix();
                        return;
                    }
                    if (item.spokenText.length() > 0 && !recentProdConversationAlreadyHas(item.starId, item.spokenText)) {
                        appendProdConversationLine(item.starId, item.spokenText);
                    }
                    if (prodMiniStatusText != null) prodMiniStatusText.setText(item.starId + " wanted to tell you something.");
                    phoneVoiceService.enqueuePreparedPrefix(clips);
                    appendVoiceDebug(item.starId + " began a prepared thought", reason + ", intent=" + item.intentId
                            + ", priority=" + item.priority + ", score=" + String.format(Locale.US, "%.1f", item.effectiveScore)
                            + ", chunks=" + clips.size());
                });

                try {
                    PreparedSpeechStore.postAction(this, url, item.intentId, "lease");
                    PreparedSpeechStore.postAction(this, url, item.intentId, "started");
                } catch (Exception acknowledgementError) {
                    runOnUiThread(() -> appendVoiceDebug(
                            "Prepared thought acknowledgement delayed",
                            item.intentId + ": " + acknowledgementError));
                }
            } catch (Exception ex) {
                preparedSpeechPlaybackActive = false;
                activePreparedSpeechItem = null;
                phoneVoiceService.cancelPreparedPrefix();
                runOnUiThread(() -> {
                    appendVoiceDebug("Prepared Star speech could not play", item.intentId + ": " + ex);
                    syncPreparedSpeech("prepared playback failure", true);
                });
            }
        });
    }

    private void completePreparedSpeechPlayback(PreparedSpeechStore.CachedItem item, String url) {
        if (item == null) return;
        preparedSpeechPlaybackActive = false;
        activePreparedSpeechItem = null;
        try {
            PreparedSpeechStore.markCompletionPending(this, item.intentId, true);
        } catch (Exception ex) {
            appendVoiceDebug("Prepared completion marker could not be saved", item.intentId + ": " + ex);
        }
        if (preparedSpeechCoordinator != null) preparedSpeechCoordinator.suppressIntent(item.intentId);
        preparedSpeechSyncResult = preparedSpeechCoordinator == null
                ? preparedSpeechSyncResult
                : preparedSpeechCoordinator.latest();
        updatePreparedSpeechIndicator();
        httpExecutor.execute(() -> {
            try {
                PreparedSpeechStore.postAction(this, url, item.intentId, "completed");
                PreparedSpeechStore.deleteIntent(this, item.intentId);
                runOnUiThread(() -> {
                    appendVoiceDebug(item.starId + " finished her prepared thought", "intent=" + item.intentId + " acknowledged and removed from Android cache.");
                    syncPreparedSpeech("prepared thought completed", true);
                });
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    appendVoiceDebug("Prepared thought completion will retry", item.intentId + ": " + ex + ". The local audio is held out of playback until acknowledgement succeeds.");
                    syncPreparedSpeech("completion acknowledgement retry", true);
                });
            }
        });
    }

    private void sendMessage() {
        String playerText = messageEdit == null ? "" : messageEdit.getText().toString();
        String requestType = requestTypeSpinner == null || requestTypeSpinner.getSelectedItem() == null ? "Chat" : requestTypeSpinner.getSelectedItem().toString();
        if (playerText.trim().length() == 0 && !"Story".equalsIgnoreCase(requestType)) {
            setDebug("Nothing to send", "Type a message first, choose Story so the Star can invent one, or use Record Voice so Whisper can provide the transcript.");
            appendVoiceDebug("Elusiaa tilted her head", "The message box was empty, so nothing was sent.");
            return;
        }

        String starName = selectedStarName();
        appendProdConversationLine("You", playerText);
        if (messageEdit != null) messageEdit.setText("");
        if (prodTextComposer != null) prodTextComposer.setVisibility(View.GONE);
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Text request sent; keeping the screen on while the Star answers.");
        invalidatePrefetchedInitiative("The player sent a text message, so the prepared at-rest line is no longer current.");
        pauseStarInitiativeBackgroundService("Text request has priority over at-rest initiative.");
        maybeSpeakPreparedBeforeReply();

        try {
            JSONObject json = baseRequestJson();
            json.put("playerText", playerText);
            json.put("girlfriendProposal", looksLikeGirlfriendProposal(playerText));
            setDebug("Sending text", "Message box cleared. The request was placed directly on the persistent WebSocket. Voice will arrive on the phone audio socket when generated.");
            appendVoiceDebug(starName + " received your message", "WebSocket message request sent immediately at " + now());
            sendSocketRequest("message", json, 180000L,
                    response -> handleConversationResponse("Text request complete", response.toString()),
                    error -> runOnUiThread(() -> {
                        setDebug("Text request wait interrupted", error.toString() + "\nThe desktop may still finish the reply. Android will receive her queued response over the persistent WebSocket if it arrives.");
                        appendVoiceDebug("Text request wait interrupted", "Still listening on the WebSocket for a queued bridge response event: " + error);
                        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Text WebSocket wait failed; keeping the phone awake briefly for queued events.");
                        mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted text request."), 20000L);
                        mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("text request recovery grace finished", false), 30000L);
                    }));
        } catch (Exception ex) {
            setDebug("Text request wait interrupted", ex.toString() + "\nThe desktop may still finish the reply. Android will receive her queued response over the persistent WebSocket if it arrives.");
            appendVoiceDebug("Text request wait interrupted", "Still listening on the WebSocket for a queued bridge response event: " + ex);
            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Text WebSocket wait failed; keeping the phone awake briefly while queued bridge events are checked.");
            mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted text request."), 20000L);
            mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("text request recovery grace finished", false), 30000L);
        }
    }

    private void toggleRecording() {
        if (recording) stopRecording(true); else startRecording();
    }

    private void startRecording() {
        if (voiceCaptureService == null || !voiceCaptureService.hasPermission()) {
            startRecordingAfterAudioPermission = true;
            requestAudioPermissionIfNeeded();
            setDebug("Microphone permission needed", "Grant RECORD_AUDIO and voice recording will start.");
            return;
        }

        try {
            voiceCaptureService.start(new VoiceCaptureService.Listener() {
                @Override
                public void onStarted() {
                    runOnUiThread(() -> {
                        recording = true;
                        viewModel.setRecording(true);
                        if (recordButton != null) recordButton.setText("Stop / Send");
                        updateProdRecordingUi();
                        setDebug("Recording", "Speak now. Press Send Voice when finished, or Cancel to discard.");
                    });
                }

                @Override
                public void onLevel(double percent) {
                    // The production screen intentionally keeps the recorder visually calm; the
                    // service still exposes level updates for a future meter or accessibility view.
                }

                @Override
                public void onStopped() {
                    runOnUiThread(() -> {
                        recording = false;
                        viewModel.setRecording(false);
                        if (recordButton != null) recordButton.setText("Record Voice");
                        updateProdRecordingUi();
                    });
                }

                @Override
                public void onError(Throwable error) {
                    runOnUiThread(() -> {
                        recording = false;
                        viewModel.setRecording(false);
                        updateProdRecordingUi();
                        setDebug("Microphone failed", error == null ? "Unknown recording failure" : error.toString());
                    });
                }
            });
        } catch (Exception ex) {
            recording = false;
            viewModel.setRecording(false);
            updateProdRecordingUi();
            setDebug("Microphone could not start", ex.toString());
            appendVoiceDebug("The Last Cat could not start recording", ex.toString());
        }
    }

    private void stopRecording(boolean send) {
        boolean wasRecording = voiceCaptureService != null && voiceCaptureService.isRecording();
        recording = false;
        viewModel.setRecording(false);
        if (recordButton != null) recordButton.setText("Record Voice");
        updateProdRecordingUi();
        if (!wasRecording || voiceCaptureService == null) return;

        voiceCaptureService.stop().whenComplete((captured, error) -> runOnUiThread(() -> {
            if (error != null) {
                setDebug("Recording could not stop", error.toString());
                return;
            }
            if (!send || captured == null) return;
            if (captured.pcm16.length < VoiceCaptureService.SAMPLE_RATE / 2) {
                setDebug("Recording too short", "Captured " + captured.pcm16.length + " bytes. Record at least about one second.");
                return;
            }
            appendProdConversationLine("You", "[voice message]");
            sendAudio(captured.pcm16);
        }));
    }

    private void sendAudio(byte[] pcm16) {
        JSONObject requestJson;
        try {
            requestJson = baseRequestJson();
            requestJson.put("audioFormat", "wav");
            requestJson.put("sampleRate", SAMPLE_RATE);
            requestJson.put("channels", 1);
            requestJson.put("bitsPerSample", 16);
        } catch (Exception ex) {
            setDebug("Voice request could not start", ex.toString());
            appendVoiceDebug("Khavia could not prepare your recorded voice", ex.toString());
            return;
        }

        maybeSpeakPreparedBeforeReply();
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Voice request sent; keeping the screen on through transcription, LLM, and speech.");
        invalidatePrefetchedInitiative("The player sent a voice message, so the prepared at-rest line is no longer current.");
        pauseStarInitiativeBackgroundService("Voice request has priority over at-rest initiative.");
        foregroundRequestExecutor.execute(() -> {
            try {
                byte[] wav = VoiceCaptureService.wavFromPcm16(pcm16, SAMPLE_RATE, 1);
                requestJson.put("audioBase64", Base64.encodeToString(wav, Base64.NO_WRAP));
                String checksum = sha256Hex(wav);
                runOnUiThread(() -> {
                    setDebug("Sending voice", "Uploading recorded WAV to desktop Whisper. Star voice will stream back on the socket. recordedSha256=" + shortHash(checksum));
                    appendVoiceDebug("Khavia carried your recorded voice", "WebSocket audio request queued at " + now() + ", bytes=" + wav.length + ", sha256=" + shortHash(checksum));
                });
                sendSocketRequest("audio", requestJson, 240000L,
                        response -> handleConversationResponse("Voice request complete", response.toString()),
                        error -> runOnUiThread(() -> {
                            setDebug("Voice request wait interrupted", error.toString() + "\nIf Whisper already produced a transcript or the Star finishes later, Android will show it from WebSocket bridge events.");
                            appendVoiceDebug("Voice request wait interrupted", "Still listening on the WebSocket for transcript/response bridge events: " + error);
                            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Voice WebSocket wait failed; keeping the phone awake briefly for queued events.");
                            mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted voice request."), 20000L);
                            mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("voice request recovery grace finished", false), 30000L);
                        }));
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    setDebug("Voice request wait interrupted", ex.toString() + "\nIf Whisper already produced a transcript or the Star finishes later, Android will show it from queued bridge events.");
                    appendVoiceDebug("Voice request wait interrupted", "Still listening on the WebSocket for transcript/response bridge events: " + ex);
                    setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Voice WebSocket wait failed; keeping the phone awake briefly while queued bridge events are checked.");
                    mainHandler.postDelayed(() -> setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Finished grace period after interrupted voice request."), 20000L);
                    mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("voice request recovery grace finished", false), 30000L);
                });
            }
        });
    }

    private void handleConversationResponse(String title, String body) {
        runOnUiThread(() -> {
            try {
                JSONObject json = new JSONObject(body);
                String transcript = optStringEither(json, "transcript", "Transcript", "");
                String spoken = optStringEither(json, "spokenText", "SpokenText", "");
                boolean initiativeShouldSpeak = optBooleanEither(json, "initiativeShouldSpeak", "InitiativeShouldSpeak", true);
                boolean initiativeSkippedBeforeLlm = optBooleanEither(json, "initiativeSkippedBeforeLlm", "InitiativeSkippedBeforeLlm", false);
                String initiativeReason = optStringEither(json, "initiativeReason", "InitiativeReason", "");
                String initiativeTopic = optStringEither(json, "initiativeTopic", "InitiativeTopic", "");
                double initiativeStateScore = optDoubleEither(json, "initiativeStateScore", "InitiativeStateScore", 0.0);
                String starOutput = optStringEither(json, "starOutput", "StarOutput", "");
                String starId = optStringEither(json, "starId", "StarId", "Star");
                int chunks = optIntEither(json, "voiceChunksSent", "VoiceChunksSent", 0);
                int bytes = optIntEither(json, "voiceBytesSent", "VoiceBytesSent", 0);
                boolean voiceRequested = optBooleanEither(json, "voiceRequested", "VoiceRequested", false);
                boolean voiceSocketConnected = optBooleanEither(json, "voiceSocketConnected", "VoiceSocketConnected", false);
                boolean preparedListenPlayback = optBooleanEither(json, "prefetchForListen", "PrefetchForListen", false);
                String voiceMessage = optStringEither(json, "voiceMessage", "VoiceMessage", "");
                String voiceError = optStringEither(json, "voiceError", "VoiceError", "");
                long serverElapsedMs = optLongEither(json, "serverElapsedMs", "ServerElapsedMs", 0L);
                long runtimeElapsedMs = optLongEither(json, "runtimeElapsedMs", "RuntimeElapsedMs", 0L);
                long gateWaitMs = optLongEither(json, "gateWaitMs", "GateWaitMs", 0L);
                long clientQueueMs = optLongEither(json, "clientQueueMs", "ClientQueueMs", 0L);
                long clientSocketRoundTripMs = optLongEither(json, "clientSocketRoundTripMs", "ClientSocketRoundTripMs", 0L);
                long clientAfterSendMs = optLongEither(json, "clientAfterSendMs", "ClientAfterSendMs", 0L);
                boolean hasGirlfriendState = json.has("isGirlfriendAfter") || json.has("IsGirlfriendAfter");
                boolean girlfriendAfter = optBooleanEither(json, "isGirlfriendAfter", "IsGirlfriendAfter", selectedStarIsGirlfriend);
                if (hasGirlfriendState) {
                    selectedStarIsGirlfriend = girlfriendAfter;
                }
                String storyTitle = optStringEither(json, "storyTitle", "StoryTitle", "");
                String storyMarkdown = optStringEither(json, "storyMarkdown", "StoryMarkdown", "");
                String storySummary = optStringEither(json, "storySummary", "StorySummary", "");
                int storyChunks = optIntEither(json, "storyChunkCount", "StoryChunkCount", 0);
                boolean storyReady = optBooleanEither(json, "storyMarkdownReady", "StoryMarkdownReady", false);
                boolean photoAttached = optBooleanEither(json, "photoAttached", "PhotoAttached", false);
                String photoSummary = optStringEither(json, "photoSummary", "PhotoSummary", "");
                String photoError = optStringEither(json, "photoError", "PhotoError", "");
                String topEmotions = formatTopAffectedEmotions(optJsonArrayEither(json, "topAffectedEmotions", "TopAffectedEmotions"));
                if (topEmotions.length() > 0) {
                    currentTopAffectedEmotions = topEmotions;
                }
                updateEmotionVectorsFromJson(json);
                String dailyLine = extractDailyStateLine(json);
                if (dailyLine.length() > 0) {
                    currentDailyStarState = dailyLine;
                }
                String interstar = optStringEither(json, "interstarStatusText", "InterstarStatusText", "");
                if (interstar.length() > 0) {
                    currentInterstarStatus = interstar;
                    if (interstarStatusText != null) interstarStatusText.setText(currentInterstarStatus);
                }
                if (starStateText != null && (topEmotions.length() > 0 || dailyLine.length() > 0 || interstar.length() > 0)) {
                    String base = starStateText.getText().toString().split("\nTop emotions:")[0].split("\nToday:")[0].split("\nInterstar state:")[0];
                    starStateText.setText(base + "\n" + currentTopAffectedEmotions + "\n" + currentDailyStarState + "\n" + currentInterstarStatus);
                }

                String visibleResponse = storyMarkdown.length() > 0 ? storyMarkdown : (starOutput.length() > 0 ? starOutput : spoken);
                boolean isInitiativeResponse = title.toLowerCase(Locale.US).contains("initiative") || "StarInitiative".equalsIgnoreCase(optStringEither(json, "requestType", "RequestType", ""));
                if (isInitiativeResponse && !initiativeShouldSpeak) {
                    String noLine = initiativeReason.length() > 0 ? initiativeReason : "The selected Star had nothing state-backed to say right now.";
                    if (prodMiniStatusText != null) prodMiniStatusText.setText("No at-rest line right now.");
                    setDebug(title, "No at-rest speech. score=" + initiativeStateScore + " skippedBeforeLlm=" + initiativeSkippedBeforeLlm + " topic=" + initiativeTopic + " reason=" + noLine);
                    appendVoiceDebug("At-rest Star initiative", "No speech: " + clip(noLine, 220));
                    allowPreparedListenAfter(4500L, "The at-rest check returned no speech, so another prepared check may run after a short quiet pause.");
                    setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "At-rest Star initiative finished without speech.");
                    return;
                }

                if (visibleResponse.length() == 0) {
                    visibleResponse = "No spoken text was returned in the bridge response. Check the desktop debug output for the Star response.";
                }

                if (transcript.length() > 0 && title.toLowerCase(Locale.US).contains("voice")) {
                    boolean replacedTranscript = replaceLatestProdVoicePlaceholder(transcript);
                    if (!replacedTranscript && !recentProdConversationAlreadyHas("You", transcript)) {
                        appendProdConversationLine("You", transcript);
                    }
                }
                if (!recentProdConversationAlreadyHas(starId, visibleResponse)) {
                    appendProdConversationLine(starId, visibleResponse);
                }

                String header = title + " — " + starId;
                if (transcript.length() > 0) header += "\nTranscript: " + transcript;
                if (storyTitle.length() > 0) header += "\nStory: " + storyTitle;
                if (storySummary.length() > 0) header += "\nSummary: " + storySummary;
                responseText.setText(header + "\n\n" + visibleResponse);

                String details = "Response chars: " + visibleResponse.length()
                        + "\nSpoken chars: " + spoken.length()
                        + "\nServer total: " + serverElapsedMs + " ms"
                        + "\nRuntime: " + runtimeElapsedMs + " ms"
                        + "\nRuntime gate wait: " + gateWaitMs + " ms"
                        + "\nAndroid request queue: " + clientQueueMs + " ms"
                        + "\nWebSocket round trip: " + clientSocketRoundTripMs + " ms"
                        + "\nAfter socket send: " + clientAfterSendMs + " ms"
                        + "\nEstimated transport/UI overhead: " + Math.max(0L, clientSocketRoundTripMs - serverElapsedMs) + " ms"
                        + "\nVoice chunks queued: " + chunks
                        + "\nVoice bytes: " + bytes
                        + "\nStory markdown ready: " + storyReady
                        + "\nStory chunks: " + storyChunks
                        + "\nStory markdown chars: " + storyMarkdown.length();
                if (initiativeTopic.length() > 0 || initiativeReason.length() > 0 || isInitiativeResponse) {
                    details += "\nInitiative should speak: " + initiativeShouldSpeak
                            + "\nInitiative skipped before LLM: " + initiativeSkippedBeforeLlm
                            + "\nInitiative state score: " + initiativeStateScore;
                    if (initiativeTopic.length() > 0) details += "\nInitiative topic: " + clip(initiativeTopic, 240);
                    if (initiativeReason.length() > 0) details += "\nInitiative reason: " + clip(initiativeReason, 400);
                }
                if (photoAttached) details += "\nPhoto attached: true";
                if (photoSummary.length() > 0) details += "\nPhoto summary: " + clip(photoSummary, 400);
                if (photoError.length() > 0) details += "\nPhoto note: " + clip(photoError, 400);
                if (topEmotions.length() > 0) details += "\n" + topEmotions;
                if (voiceMessage.length() > 0) details += "\n" + voiceMessage;
                if (voiceError.length() > 0) details += "\nVoice note: " + voiceError;
                setDebug(title, details);
                appendVoiceDebug(starId + " returned text", "WebSocket response at " + now() + ", responseChars=" + visibleResponse.length() + ", spokenChars=" + spoken.length() + ", voiceMessage=" + clip(voiceMessage, 140));
                if (!preparedListenPlayback && (!voiceRequested || !voiceSocketConnected)) {
                    allowPreparedListenAfter(4500L, "The foreground response has no pending phone voice stream.");
                }
            } catch (Exception ex) {
                responseText.setText(clip(body, 8000));
                setDebug(title + " (raw)", clip(body, 900));
            } finally {
                setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, title + " finished; screen can rest after any streaming voice/playback completes.");
                mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("foreground conversation finished; prepare the next Listen response", true), 6500L);
            }
        });
    }

    private String get(String urlText, int timeoutMs) throws Exception {
        URL url = new URL(urlText);
        HttpURLConnection connection = new LocalNetworkBindingService(this).openHttpConnection(url);
        connection.setConnectTimeout(timeoutMs);
        connection.setReadTimeout(timeoutMs);
        connection.setRequestMethod("GET");
        return readResponse(connection);
    }

    private String postJson(String urlText, String json, int timeoutMs) throws Exception {
        byte[] bytes = json.getBytes(StandardCharsets.UTF_8);
        URL url = new URL(urlText);
        HttpURLConnection connection = new LocalNetworkBindingService(this).openHttpConnection(url);
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
        if (phoneVoiceService != null) phoneVoiceService.connect(host, port);
    }

    private void closeAudioSocket() {
        if (phoneVoiceService != null) phoneVoiceService.disconnect();
    }

    private void playSavedVoiceAudio() {
        if (phoneVoiceService != null) phoneVoiceService.playSavedVoice();
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

    private static JSONObject optJsonObjectEither(JSONObject object, String camel, String pascal) {
        if (object == null) return null;
        JSONObject value = object.optJSONObject(camel);
        if (value != null) return value;
        return object.optJSONObject(pascal);
    }

    private static JSONArray optJsonArrayEither(JSONObject object, String camel, String pascal) {
        if (object == null) return null;
        JSONArray value = object.optJSONArray(camel);
        if (value != null) return value;
        return object.optJSONArray(pascal);
    }


    private static String formatEmotionVectors(String positiveVector, String negativeVector) {
        ArrayList<VectorSignal> positives = parseEmotionVector(positiveVector, STANDARD_EMOTION_IDS, "+");
        ArrayList<VectorSignal> negatives = parseEmotionVector(negativeVector, NEGATIVE_EMOTION_IDS, "-");
        sortVectorSignals(positives);
        sortVectorSignals(negatives);
        if (positives.size() == 0 && negatives.size() == 0) return "Last emotional vectors: none yet.";
        StringBuilder sb = new StringBuilder("Last emotional vectors:\n");
        if (positives.size() > 0) {
            sb.append("Good: ").append(joinVectorSignals(positives, 8));
        }
        if (negatives.size() > 0) {
            if (positives.size() > 0) sb.append("\n");
            sb.append("Bad: ").append(joinVectorSignals(negatives, 8));
        }
        if ((positiveVector != null && positiveVector.length() > 0) || (negativeVector != null && negativeVector.length() > 0)) {
            sb.append("\nRaw good=").append(positiveVector == null ? "" : positiveVector)
                    .append(" | bad=").append(negativeVector == null ? "" : negativeVector);
        }
        return sb.toString();
    }

    private static ArrayList<VectorSignal> parseEmotionVector(String vector, String[] ids, String sign) {
        ArrayList<VectorSignal> list = new ArrayList<>();
        if (ids == null || ids.length == 0) return list;
        String[] parts = vector == null ? new String[0] : vector.split(";");
        for (int i = 0; i < ids.length; i++) {
            double confidence = 0.0;
            double score = 0.0;
            if (i < parts.length) {
                String[] pair = parts[i].trim().split(":");
                if (pair.length >= 2) {
                    try { confidence = Double.parseDouble(pair[0]); } catch (Exception ignored) { }
                    try { score = Double.parseDouble(pair[1]); } catch (Exception ignored) { }
                }
            }
            if (confidence > 0 || score > 0) {
                list.add(new VectorSignal(ids[i], sign, confidence, score));
            }
        }
        return list;
    }

    private static void sortVectorSignals(ArrayList<VectorSignal> list) {
        Collections.sort(list, new Comparator<VectorSignal>() {
            @Override
            public int compare(VectorSignal a, VectorSignal b) {
                int weighted = Double.compare(b.weightedScore(), a.weightedScore());
                if (weighted != 0) return weighted;
                return Double.compare(b.score, a.score);
            }
        });
    }

    private static String joinVectorSignals(ArrayList<VectorSignal> list, int maxCount) {
        StringBuilder sb = new StringBuilder();
        int count = Math.min(Math.max(1, maxCount), list.size());
        for (int i = 0; i < count; i++) {
            if (i > 0) sb.append(" | ");
            VectorSignal item = list.get(i);
            sb.append(item.sign).append(item.id).append(" ")
                    .append(String.format(Locale.US, "%.0f", item.score))
                    .append(" @ ")
                    .append(String.format(Locale.US, "%.2f", item.confidence));
        }
        return sb.toString();
    }

    private static String formatTopAffectedEmotions(JSONArray array) {
        if (array == null || array.length() == 0) return "";
        StringBuilder sb = new StringBuilder("Top emotions: ");
        int count = 0;
        for (int i = 0; i < array.length(); i++) {
            JSONObject item = array.optJSONObject(i);
            if (item == null) continue;
            String emotion = optStringEither(item, "emotionId", "EmotionId", "");
            if (emotion.length() == 0) continue;
            String valence = optStringEither(item, "valence", "Valence", "positive");
            double score = optDoubleEither(item, "score", "Score", 0.0);
            double confidence = optDoubleEither(item, "confidence", "Confidence", 0.0);
            boolean accepted = optBooleanEither(item, "acceptedForStateUpdate", "AcceptedForStateUpdate", false);
            if (count > 0) sb.append(" | ");
            sb.append(emotion)
                    .append(" ")
                    .append("negative".equalsIgnoreCase(valence) ? "-" : "+")
                    .append(String.format(Locale.US, "%.0f", score))
                    .append(" @ ")
                    .append(String.format(Locale.US, "%.2f", confidence));
            if (accepted) sb.append("*");
            count++;
        }
        return count == 0 ? "" : sb.toString();
    }


    private static String firstNonEmpty(String... values) {
        if (values == null) return "";
        for (String value : values) {
            if (value != null && value.trim().length() > 0) return value.trim();
        }
        return "";
    }

    private static final class VectorSignal {
        final String id;
        final String sign;
        final double confidence;
        final double score;

        VectorSignal(String id, String sign, double confidence, double score) {
            this.id = id == null ? "" : id;
            this.sign = sign == null ? "+" : sign;
            this.confidence = Math.max(0.0, Math.min(1.0, confidence));
            this.score = Math.max(0.0, Math.min(100.0, score));
        }

        double weightedScore() {
            return confidence * score;
        }
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

    private static long optLongEither(JSONObject json, String camelName, String pascalName, long fallback) {
        if (json.has(camelName)) return json.optLong(camelName, fallback);
        if (json.has(pascalName)) return json.optLong(pascalName, fallback);
        return fallback;
    }

    private static boolean optBooleanEither(JSONObject json, String camelName, String pascalName, boolean fallback) {
        if (json.has(camelName)) return json.optBoolean(camelName, fallback);
        if (json.has(pascalName)) return json.optBoolean(pascalName, fallback);
        return fallback;
    }

    private static double optDoubleEither(JSONObject json, String camelName, String pascalName, double fallback) {
        if (json.has(camelName)) return json.optDouble(camelName, fallback);
        if (json.has(pascalName)) return json.optDouble(pascalName, fallback);
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


    private static final class StarMeta {
        final String id;
        final String element;
        final String domain;
        final int accentColor;
        final int portraitResId;
        final int[] frameResIds;
        StarMeta(String id, String element, String domain, int accentColor, int portraitResId, int[] frameResIds) {
            this.id = id;
            this.element = element;
            this.domain = domain;
            this.accentColor = accentColor;
            this.portraitResId = portraitResId;
            this.frameResIds = frameResIds;
        }
        int previewResId() {
            return frameResIds != null && frameResIds.length > 0 ? frameResIds[0] : portraitResId;
        }
    }

}
