package com.wds.sixstarsmobile;

import android.Manifest;
import android.app.AlertDialog;
import android.content.SharedPreferences;
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
import android.media.AudioRecord;
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

import org.json.JSONArray;
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
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.Comparator;
import java.util.Date;
import java.util.HashSet;
import java.util.Locale;
import java.util.TreeMap;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;

public final class MainActivity extends ComponentActivity {
    private static final String PREFS_NAME = "SixStarsMobilePrefs";
    private static final String PREF_PROD_MODE = "prodMode";
    private static final String PREF_STAR_INITIATIVE_ENABLED = "starInitiativeEnabled";
    private static final String PREF_STAR_INITIATIVE_BACKGROUND_ENABLED = "starInitiativeBackgroundServiceEnabled";
    private static final String PREF_BRIDGE_URL = "bridgeUrl";
    private static final String PREF_SELECTED_STAR_ID = "selectedStarId";
    private static final String PREF_ROMANCE_MODE = "romanceMode";
    private static final String PREF_PLAY_ON_PHONE = "playOnPhone";
    private static final String PREF_AREA = "area";
    private static final String PREF_QUEST = "quest";
    private static final String DEFAULT_BRIDGE_URL = "http://192.168.1.217:8066";
    private static final String ACTION_STAR_INITIATIVE_SERVICE_RESPONSE = "com.wds.sixstarsmobile.STAR_INITIATIVE_SERVICE_RESPONSE";
    private static final String ACTION_STAR_INITIATIVE_SERVICE_STATUS = "com.wds.sixstarsmobile.STAR_INITIATIVE_SERVICE_STATUS";
    private static final String[] STAR_IDS = new String[]{"Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari"};
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
    private final Object socketLock = new Object();
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
    private TextView prodMiniStatusText;
    private PixelStarAnimationView prodSpriteView;
    private TextView prodTalkButtonText;
    private View prodTalkButton;
    private View prodListenButton;
    private View prodPhotoButton;
    private Button prodBreakupButton;
    private View prodVoiceActions;
    private Button prodCancelVoiceButton;
    private Button prodSendVoiceButton;
    private Button prodSendTextButton;
    private View prodTextToggleButton;
    private View prodTextComposer;
    private TextView prodConversationText;
    private ScrollView prodConversationScroll;
    private String prodAnimationState = "idle";
    private int voiceAnimationGeneration;
    private boolean starVoicePlaybackActive;
    private PhoneVoiceClip activeAnimatedVoiceClip;
    private AlertDialog activeStarSelectionDialog;
    private boolean prodMode = true;
    private boolean starInitiativeEnabled;
    private boolean starInitiativeBackgroundEnabled;
    private long lastStarInitiativeRequestMs;
    private long lastManualListenRequestMs;
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
    private MobileBridgeWebSocketClient bridgeSocketClient;
    private boolean bridgeEventsPrimed;
    private File lastVoiceAudioFile;
    private String lastVoiceAudioChecksum = "";
    private ByteArrayOutputStream lastVoicePcmAccumulator;
    private int lastVoiceAccumulatorSampleRate = 24000;
    private int lastVoiceAccumulatorChannels = 1;
    private int lastVoiceAccumulatorTotalChunks = 0;
    private int lastVoiceAccumulatorChunks = 0;

    private volatile boolean destroyed;
    private AudioRecord audioRecord;
    private Thread recordingThread;
    private ByteArrayOutputStream recordedPcm;

    private Socket audioSocket;
    private Thread audioSocketThread;
    private String connectedAudioHost = "";
    private int connectedAudioPort = -1;
    private final WavAudioQueue wavAudioQueue = new WavAudioQueue();
    private final TreeMap<String, ArrayList<String>> prodConversationByStar = new TreeMap<>();
    private final Object voiceStreamStateLock = new Object();
    private String activeVoiceStreamId = "";
    private long activeVoiceStreamOrdinal = 0L;
    private int droppedStaleVoiceFrames;
    private final Object orderedVoiceQueueLock = new Object();
    private final TreeMap<Integer, PhoneVoiceClip> pendingDesktopVoiceClips = new TreeMap<>();
    private final HashSet<Integer> acceptedDesktopVoiceSequences = new HashSet<>();
    private int nextExpectedDesktopVoiceSequence = 0;
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

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        lastVoiceAudioFile = new File(getFilesDir(), "last-six-stars-phone-voice.wav");
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        prodMode = prefs.getBoolean(PREF_PROD_MODE, true);
        starInitiativeEnabled = prefs.getBoolean(PREF_STAR_INITIATIVE_ENABLED, true);
        starInitiativeBackgroundEnabled = starInitiativeEnabled;
        buildUi();
        initializeBridgeWebSocket();
        registerStarInitiativeServiceReceiver();
        syncStarInitiativeRuntimePrefs();
        syncStarInitiativeBackgroundService("Android activity started", false);
        requestAudioPermissionIfNeeded();
        wavAudioQueue.start();
        mainHandler.postDelayed(healthLoop, 600);
    }

    @Override
    protected void onResume() {
        super.onResume();
        registerStarInitiativeServiceReceiver();
        ensureBridgeWebSocket();
        mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("Android activity resumed", false), 3500L);
    }

    @Override
    protected void onDestroy() {
        destroyed = true;
        stopRecording(false);
        if (bridgeSocketClient != null) bridgeSocketClient.close();
        closeAudioSocket();
        wavAudioQueue.stop();
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
                STAR_IDS));
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
        romanceCheck.setChecked(getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getBoolean(PREF_ROMANCE_MODE, true));
        romanceCheck.setOnCheckedChangeListener((buttonView, isChecked) -> syncStarInitiativeRuntimePrefs());
        root.addView(romanceCheck);

        playOnPhoneCheck = new CheckBox(this);
        playOnPhoneCheck.setText("Play Star voice on this phone");
        playOnPhoneCheck.setChecked(getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getBoolean(PREF_PLAY_ON_PHONE, true));
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
            getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit()
                    .putBoolean(PREF_STAR_INITIATIVE_ENABLED, starInitiativeEnabled)
                    .putBoolean(PREF_STAR_INITIATIVE_BACKGROUND_ENABLED, starInitiativeBackgroundEnabled)
                    .apply();
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
        prodMiniStatusText = null;
        prodSpriteView = null;
        prodTalkButtonText = null;
        prodTalkButton = null;
        prodListenButton = null;
        prodPhotoButton = null;
        prodBreakupButton = null;
        prodVoiceActions = null;
        prodCancelVoiceButton = null;
        prodSendVoiceButton = null;
        prodSendTextButton = null;
        prodTextToggleButton = null;
        prodTextComposer = null;
        prodConversationText = null;
        prodConversationScroll = null;

        createProdHiddenControls();
        setContentView(R.layout.activity_prod_companion);

        prodStarNameText = findViewById(R.id.prod_star_name);
        prodStarSubtitleText = findViewById(R.id.prod_star_subtitle);
        prodRelationshipText = findViewById(R.id.prod_relationship);
        prodTalkButtonText = findViewById(R.id.prod_talk_text);
        prodTalkButton = findViewById(R.id.prod_talk_button);
        prodListenButton = findViewById(R.id.prod_listen_button);
        prodPhotoButton = findViewById(R.id.prod_photo_button);
        prodBreakupButton = findViewById(R.id.prod_breakup_button);
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

        prodSpriteView = findViewById(R.id.prod_sprite_view);
        if (prodSpriteView != null) {
            prodSpriteView.setClickable(true);
            prodSpriteView.setFocusable(true);
            prodSpriteView.setOnClickListener(openStarTapPanel);
        }

        prodTalkButton.setOnClickListener(v -> prodTalkButtonTapped());
        if (prodListenButton != null) prodListenButton.setOnClickListener(v -> requestStarInitiativeFromListenButton());
        if (prodPhotoButton != null) prodPhotoButton.setOnClickListener(v -> startPhotoCapture());
        if (prodBreakupButton != null) prodBreakupButton.setOnClickListener(v -> dispatchBreakupGirlfriend());
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
    }

    private void createProdHiddenControls() {
        serverUrlEdit = input(savedBridgeUrl(), false);
        starSpinner = new Spinner(this);
        starSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item, STAR_IDS));
        starSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (suppressStarSelectionDispatch) return;
                refreshProdStarUi(starSpinner.getSelectedItem().toString());
                syncStarInitiativeBackgroundService("Selected Star changed", true);
                dispatchSelectStar(starSpinner.getSelectedItem().toString());
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        requestTypeSpinner = new Spinner(this);
        requestTypeSpinner.setAdapter(new ArrayAdapter<>(this, android.R.layout.simple_spinner_dropdown_item,
                new String[]{"Chat", "Story", "Photo", "Letter", "BattleUpdate", "Debug"}));
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        areaEdit = input(prefs.getString(PREF_AREA, "VirethornCaves"), false);
        questEdit = input(prefs.getString(PREF_QUEST, prodMode ? "AndroidProdCompanion" : "AndroidPhoneBridge"), false);
        romanceCheck = new CheckBox(this);
        romanceCheck.setText("Romance Mode");
        romanceCheck.setChecked(prefs.getBoolean(PREF_ROMANCE_MODE, true));
        romanceCheck.setOnCheckedChangeListener((buttonView, isChecked) -> syncStarInitiativeRuntimePrefs());
        playOnPhoneCheck = new CheckBox(this);
        playOnPhoneCheck.setText("Play Star voice on this phone");
        playOnPhoneCheck.setChecked(prefs.getBoolean(PREF_PLAY_ON_PHONE, true));
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

        LinearLayout buttons2 = row();
        Button audio = prodButton("Test Audio");
        Button dev = prodButton("Dev Mode");
        buttons2.addView(audio, weightParams(1));
        buttons2.addView(dev, weightParams(1));
        controls.addView(buttons2);

        TextView status = prodOutput(buildProdMenuStatusText(), 12);
        status.setTextIsSelectable(true);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(status);
        controls.addView(scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(360)));
        outer.addView(controls);

        AlertDialog dialog = new AlertDialog.Builder(this)
                .setView(outer)
                .setNegativeButton("Close", null)
                .create();
        dialog.setOnShowListener(d -> {
            dialog.getWindow().setBackgroundDrawable(makeProdPanel(0xF00B1220, 0xFF00D5D7, 18));
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(0xFFEAF7FF);
            save.setOnClickListener(v -> {
                if (serverUrlEdit != null) serverUrlEdit.setText(bridgeUrl.getText().toString().trim());
                if (playOnPhoneCheck != null) playOnPhoneCheck.setChecked(playPhone.isChecked());
                if (romanceCheck != null) romanceCheck.setChecked(romance.isChecked());
                starInitiativeEnabled = initiative.isChecked();
                starInitiativeBackgroundEnabled = starInitiativeEnabled;
                getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit()
                        .putBoolean(PREF_STAR_INITIATIVE_ENABLED, starInitiativeEnabled)
                        .putBoolean(PREF_STAR_INITIATIVE_BACKGROUND_ENABLED, starInitiativeBackgroundEnabled)
                        .apply();
                syncStarInitiativeBackgroundService("Prod settings saved", true);
                setDebug("Prod settings saved", "Bridge=" + baseUrl() + ", phoneVoice=" + (playOnPhoneCheck == null || playOnPhoneCheck.isChecked()) + ", romance=" + (romanceCheck == null || romanceCheck.isChecked()) + ", starInitiative=" + starInitiativeEnabled + ", autoSpeak=" + starInitiativeEnabled);
                status.setText(buildProdMenuStatusText());
                testConnection(false);
            });
            test.setOnClickListener(v -> {
                if (serverUrlEdit != null) serverUrlEdit.setText(bridgeUrl.getText().toString().trim());
                testConnection(true);
                mainHandler.postDelayed(() -> status.setText(buildProdMenuStatusText()), 900);
            });
            audio.setOnClickListener(v -> testPhoneAudio());
            dev.setOnClickListener(v -> { dialog.dismiss(); switchUiMode(false); });
        });
        dialog.show();
    }

    private String buildProdMenuStatusText() {
        StringBuilder sb = new StringBuilder();
        sb.append("Star: ").append(selectedStarName()).append('\n');
        sb.append(prodRelationshipText == null ? "Relationship: unknown" : prodRelationshipText.getText()).append("\n\n");
        sb.append(nonEmptyText(connectionStatusText, "Connection: not checked yet.")).append('\n');
        sb.append(nonEmptyText(audioStatusText, "Audio: phone voice socket not connected yet.")).append("\n\n");
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
        TextView sub = prodText("Tap a portrait to switch companions. The cat portrait opens app status and settings.", 12, 0xFFB6E6F2, false);
        sub.setGravity(Gravity.CENTER_HORIZONTAL);
        outer.addView(sub);

        GridLayout grid = new GridLayout(this);
        grid.setColumnCount(2);
        grid.setPadding(0, dp(10), 0, dp(6));
        for (String starId : STAR_IDS) {
            StarMeta meta = starMeta(starId);
            LinearLayout card = starSelectionCard(meta);
            GridLayout.LayoutParams lp = new GridLayout.LayoutParams();
            lp.width = 0;
            lp.height = GridLayout.LayoutParams.WRAP_CONTENT;
            lp.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, 1f);
            lp.setMargins(dp(4), dp(4), dp(4), dp(4));
            grid.addView(card, lp);
        }
        outer.addView(grid, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        AlertDialog dialog = new AlertDialog.Builder(this)
                .setView(outer)
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

    private LinearLayout starSelectionCard(StarMeta meta) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setGravity(Gravity.CENTER_HORIZONTAL);
        card.setPadding(dp(8), dp(8), dp(8), dp(8));
        card.setBackground(makeProdPanel(0xDD111827, meta.accentColor, 16));
        ImageView portrait = new ImageView(this);
        portrait.setImageResource(meta.portraitResId);
        portrait.setAdjustViewBounds(true);
        portrait.setScaleType(ImageView.ScaleType.FIT_CENTER);
        card.addView(portrait, new LinearLayout.LayoutParams(dp(96), dp(96)));
        TextView name = prodText(meta.id, 15, 0xFFFFFFFF, true);
        name.setGravity(Gravity.CENTER_HORIZONTAL);
        card.addView(name);
        TextView sub = prodText(meta.element + " • " + meta.domain, 11, 0xFFCFFAFE, false);
        sub.setGravity(Gravity.CENTER_HORIZONTAL);
        card.addView(sub);
        card.setOnClickListener(v -> {
            selectStarInSpinner(meta.id);
            refreshProdStarUi(meta.id);
            syncStarInitiativeBackgroundService("Selected Star changed", true);
            dispatchSelectStar(meta.id);
            if (activeStarSelectionDialog != null) activeStarSelectionDialog.dismiss();
        });
        return card;
    }

    private void switchUiMode(boolean useProd) {
        prodMode = useProd;
        getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().putBoolean(PREF_PROD_MODE, useProd).apply();
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
    }

    private void setProdAnimationState(String state) {
        prodAnimationState = (state == null || state.length() == 0) ? "idle" : state.toLowerCase(Locale.US);
        if (prodSpriteView != null) {
            prodSpriteView.setStarAnimation(selectedStarName(), prodAnimationState, starMeta(selectedStarName()).frameResIds);
        }
    }

    private void cancelStarVoiceAnimationMarkers(String reason) {
        voiceAnimationGeneration++;
        activeAnimatedVoiceClip = null;
        starVoicePlaybackActive = false;
        setProdAnimationState("idle");
    }

    private void finishStarVoiceAnimationMarkers(PhoneVoiceClip clip) {
        if (clip == null || activeAnimatedVoiceClip != clip) return;
        voiceAnimationGeneration++;
        activeAnimatedVoiceClip = null;
        starVoicePlaybackActive = false;
        setProdAnimationState("idle");
    }

    private void startStarVoiceAnimationMarkers(PhoneVoiceClip clip, long fallbackDurationMs, long playbackElapsedMs) {
        if (!prodMode || clip == null || !clip.fromDesktop) return;
        final int generation = ++voiceAnimationGeneration;
        activeAnimatedVoiceClip = clip;
        starVoicePlaybackActive = true;
        long elapsedMs = Math.max(0L, playbackElapsedMs);
        int[] segments = clip.speechActivitySegmentsMs == null ? new int[0] : clip.speechActivitySegmentsMs;
        if (segments.length < 2) {
            // Older desktop headers do not contain word/syllable timing. In that case,
            // talk for the remaining audio duration, beginning only after AudioTrack.play().
            setProdAnimationState("talking");
            long remainingMs = Math.max(80L, fallbackDurationMs - elapsedMs);
            mainHandler.postDelayed(() -> {
                if (generation == voiceAnimationGeneration) {
                    if (activeAnimatedVoiceClip == clip) activeAnimatedVoiceClip = null;
                    starVoicePlaybackActive = false;
                    setProdAnimationState("idle");
                }
            }, remainingMs);
            return;
        }

        long latestEnd = 0L;
        int scheduled = 0;
        for (int i = 0; i + 1 < segments.length && scheduled < 240; i += 2) {
            long startMs = Math.max(0L, segments[i]);
            long durationMs = Math.max(60L, Math.min(4000L, segments[i + 1]));
            long endMs = startMs + durationMs;
            latestEnd = Math.max(latestEnd, endMs);
            if (endMs <= elapsedMs) continue;
            scheduled++;

            long startDelayMs = Math.max(0L, startMs - elapsedMs);
            long endDelayMs = Math.max(1L, endMs - elapsedMs);
            if (startDelayMs == 0L) {
                setProdAnimationState("talking");
            } else {
                mainHandler.postDelayed(() -> {
                    if (generation == voiceAnimationGeneration && isCurrentVoiceClip(clip)) {
                        starVoicePlaybackActive = true;
                        setProdAnimationState("talking");
                    }
                }, startDelayMs);
            }
            mainHandler.postDelayed(() -> {
                if (generation == voiceAnimationGeneration && isCurrentVoiceClip(clip)) {
                    setProdAnimationState("idle");
                }
            }, endDelayMs);
        }

        final long releaseAt = Math.max(
                1L,
                Math.max(Math.max(250L, fallbackDurationMs), latestEnd + 80L) - elapsedMs);
        mainHandler.postDelayed(() -> {
            if (generation == voiceAnimationGeneration) {
                if (activeAnimatedVoiceClip == clip) activeAnimatedVoiceClip = null;
                starVoicePlaybackActive = false;
                setProdAnimationState("idle");
            }
        }, releaseAt);
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
            prodConversationText.setText("No conversation yet. Tap Talk or open Text.");
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
        prodStarSubtitleText.setText(meta.element + " Star • " + meta.domain + " • tap to switch");
        if (prodSpriteView != null) prodSpriteView.setStarAnimation(meta.id, prodAnimationState, meta.frameResIds);
        refreshProdConversationWindow();
    }

    private void updateProdStarStateUi(String starId, boolean met, boolean ally, int heart, int trust, int romance, double connection, boolean ready, boolean girlfriend, String status) {
        refreshProdStarUi(starId);
        if (prodRelationshipText != null) {
            String gfSuffix = girlfriend ? "   Girlfriend" + (selectedStarCuteModeEnabled ? (selectedStarCuteModeActive ? "   Cute" : "") : "   Cute off") : (ready ? "   Ready" : "");
            prodRelationshipText.setText("♥ Heart " + heart + "   Trust " + trust + "   Romance " + romance + "   " + String.format(Locale.US, "%.1f", connection) + "%" + gfSuffix);
        }
        if (prodBreakupButton != null) prodBreakupButton.setVisibility(View.GONE);
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
        if (bold) tv.setTypeface(Typeface.DEFAULT_BOLD);
        tv.setIncludeFontPadding(true);
        return tv;
    }

    private TextView prodOutput(String text, int minLines) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(0xFFE5E7EB);
        view.setTextSize(12);
        view.setMinLines(minLines);
        view.setPadding(dp(10), dp(8), dp(10), dp(8));
        view.setBackground(makeProdPanel(0xB90B1220, 0x6656D3E8, 14));
        return view;
    }

    private Button prodButton(String text) {
        Button button = new Button(this);
        button.setText(text);
        button.setTextColor(0xFFEAF7FF);
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
        if (id.equalsIgnoreCase("Maera")) return new StarMeta("Maera", "Blood", "Possession", 0xFFFF4D6D, R.drawable.portrait_maera, new int[]{R.drawable.star_maera_idle_0, R.drawable.star_maera_idle_1, R.drawable.star_maera_idle_2, R.drawable.star_maera_idle_3, R.drawable.star_maera_idle_4, R.drawable.star_maera_idle_5});
        if (id.equalsIgnoreCase("Khavia")) return new StarMeta("Khavia", "Water", "Friendship", 0xFF22D3EE, R.drawable.portrait_khavia, new int[]{R.drawable.star_khavia_idle_0});
        if (id.equalsIgnoreCase("Auerlis")) return new StarMeta("Auerlis", "Fire", "Joy", 0xFFF97316, R.drawable.portrait_auerlis, new int[]{R.drawable.star_auerlis_idle_0});
        if (id.equalsIgnoreCase("Nyxari")) return new StarMeta("Nyxari", "Shadow", "Fragmentation", 0xFF60A5FA, R.drawable.portrait_nyxari, new int[]{R.drawable.star_nyxari_idle_0});
        return new StarMeta("Elusiaa", "Light", "Mercy", 0xFF38E8FF, R.drawable.portrait_elusiaa, new int[]{R.drawable.star_elusiaa_idle_0, R.drawable.star_elusiaa_idle_1, R.drawable.star_elusiaa_idle_2, R.drawable.star_elusiaa_idle_3, R.drawable.star_elusiaa_idle_4, R.drawable.star_elusiaa_idle_5});
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
        return getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getString(PREF_BRIDGE_URL, DEFAULT_BRIDGE_URL);
    }

    private String baseUrl() {
        String value = serverUrlEdit == null ? savedBridgeUrl() : serverUrlEdit.getText().toString().trim();
        if (value.endsWith("/")) value = value.substring(0, value.length() - 1);
        return value.length() == 0 ? DEFAULT_BRIDGE_URL : value;
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
        bridgeSocketClient = new MobileBridgeWebSocketClient(new MobileBridgeWebSocketClient.Listener() {
            @Override
            public void onConnected(long latestSequence) {
                runOnUiThread(() -> {
                    if (!bridgeEventsPrimed) {
                        lastBridgeEventSequence = Math.max(lastBridgeEventSequence, latestSequence);
                        bridgeEventsPrimed = true;
                    } else if (bridgeSocketClient != null) {
                        bridgeSocketClient.subscribeAfter(lastBridgeEventSequence);
                    }
                    connectionStatusText.setText("Connected by WebSocket to " + baseUrl());
                    appendVoiceDebug("Kaelia opened the mobile response pipe", "Persistent WebSocket connected. pending=" + (bridgeSocketClient == null ? 0 : bridgeSocketClient.pendingCount()));
                    testConnection(false);
                });
            }

            @Override
            public void onDisconnected(String reason) {
                runOnUiThread(() -> {
                    connectionStatusText.setText("Reconnecting to " + baseUrl());
                    appendVoiceDebug("Nyxari felt the mobile pipe reconnect", clip(reason, 500));
                });
            }

            @Override
            public void onEvent(JSONObject event) {
                runOnUiThread(() -> handleBridgeEvent(event));
            }

            @Override
            public void onProtocolDebug(String message) {
                runOnUiThread(() -> appendVoiceDebug("Mobile WebSocket", clip(message, 900)));
            }
        });
        ensureBridgeWebSocket();
    }

    private void ensureBridgeWebSocket() {
        if (destroyed || bridgeSocketClient == null) return;
        bridgeSocketClient.connect(baseUrl());
    }

    private void sendSocketRequest(String operation, JSONObject payload, long timeoutMs, Consumer<JSONObject> onSuccess, Consumer<Throwable> onFailure) {
        ensureBridgeWebSocket();
        MobileBridgeWebSocketClient socket = bridgeSocketClient;
        if (socket == null) {
            onFailure.accept(new IllegalStateException("Mobile bridge WebSocket is not initialized."));
            return;
        }
        CompletableFuture<JSONObject> future = socket.request(operation, payload, timeoutMs);
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
        JSONObject request = new JSONObject();
        sendSocketRequest("health", request, 10000L, json -> {
            int audioPort = json.optInt("audioPort", defaultAudioPort());
            try {
                String host = new URL(baseUrl()).getHost();
                runOnUiThread(() -> {
                    connectionStatusText.setText("Connected: " + json.optString("machineName", "desktop") + " at " + baseUrl() + " (WebSocket)");
                    if (manual) setDebug("Health OK", "Persistent WebSocket; HTTP port " + json.optInt("port") + ", WebSocket clients " + json.optInt("webSocketClientCount") + ", audio port " + audioPort + ", audio clients " + json.optInt("audioClientCount"));
                });
                if (playOnPhoneCheck != null && playOnPhoneCheck.isChecked()) ensureAudioSocket(host, audioPort);
                fetchMobileState(false);
            } catch (Exception ex) {
                runOnUiThread(() -> {
                    if (manual) setDebug("Health parse failed", ex.toString());
                });
            }
        }, error -> runOnUiThread(() -> {
            connectionStatusText.setText("Connecting to " + baseUrl() + " — " + error.getMessage());
            if (manual) setDebug("WebSocket health failed", error.toString());
        }));
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
        SharedPreferences.Editor editor = getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit()
                .putBoolean(PREF_PROD_MODE, prodMode)
                .putBoolean(PREF_STAR_INITIATIVE_ENABLED, starInitiativeEnabled)
                .putBoolean(PREF_STAR_INITIATIVE_BACKGROUND_ENABLED, starInitiativeBackgroundEnabled)
                .putString(PREF_BRIDGE_URL, baseUrl())
                .putString(PREF_SELECTED_STAR_ID, selectedStarName())
                .putBoolean(PREF_ROMANCE_MODE, romanceCheck == null || romanceCheck.isChecked())
                .putBoolean(PREF_PLAY_ON_PHONE, playOnPhoneCheck == null || playOnPhoneCheck.isChecked());
        if (areaEdit != null) editor.putString(PREF_AREA, areaEdit.getText().toString());
        if (questEdit != null) editor.putString(PREF_QUEST, questEdit.getText().toString());
        editor.apply();
    }

    private void syncStarInitiativeBackgroundService(String reason, boolean checkSoon) {
        syncStarInitiativeRuntimePrefs();
        Intent intent = new Intent(this, StarInitiativeBackgroundService.class);
        if (starInitiativeEnabled) {
            intent.setAction(checkSoon ? StarInitiativeBackgroundService.ACTION_CHECK_NOW : StarInitiativeBackgroundService.ACTION_START);
            intent.putExtra("reason", reason == null ? "Android foreground sync" : reason);
        } else {
            intent.setAction(StarInitiativeBackgroundService.ACTION_STOP);
        }

        try {
            startService(intent);
        } catch (Exception ex) {
            appendVoiceDebug("Nyxari could not sync the at-rest service", ex.toString());
        }
    }

    private void pauseStarInitiativeBackgroundService(String reason) {
        Intent intent = new Intent(this, StarInitiativeBackgroundService.class);
        intent.setAction(StarInitiativeBackgroundService.ACTION_STOP);
        try {
            startService(intent);
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
                setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Background Star initiative service is asking whether she has something to say.");
                if (prodMiniStatusText != null) prodMiniStatusText.setText((starId == null || starId.length() == 0 ? selectedStarName() : starId) + " is thinking at rest...");
            }
            setDebug("Background at-rest service", clip(message == null ? status : message, 1200));
            appendVoiceDebug("Background at-rest service", clip(message == null ? status : message, 220));
            return;
        }

        if (!ACTION_STAR_INITIATIVE_SERVICE_RESPONSE.equals(action)) return;

        boolean ok = intent.getBooleanExtra("ok", false);
        String eventJson = intent.getStringExtra("eventJson");
        String body = intent.getStringExtra("body");
        String message = intent.getStringExtra("message");
        try {
            if (ok && eventJson != null && eventJson.trim().length() > 0) {
                handleBridgeEvent(new JSONObject(eventJson));
            } else if (ok && body != null && body.trim().length() > 0) {
                handleConversationResponse("Background at-rest Star initiative complete", body);
            } else {
                setDebug("Background at-rest service", message == null ? "No at-rest response." : message);
                appendVoiceDebug("Background at-rest service", message == null ? "No at-rest response." : message);
                setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Background Star initiative service finished without a response.");
            }
        } catch (Exception ex) {
            setDebug("Background at-rest service parse failed", ex.toString());
            appendVoiceDebug("Background at-rest service parse failed", ex.toString());
            setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, "Background Star initiative service response parse failed.");
        }
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

    private void requestStarInitiativeFromListenButton() {
        if (!prodMode || destroyed) return;
        if (recording) {
            setDebug("Listen button waiting", "Finish or cancel the current voice recording before asking the Star if she has something to say.");
            appendVoiceDebug("Listen button", "Voice recording is active, so no initiative request was sent.");
            return;
        }

        long now = System.currentTimeMillis();
        if (now - lastManualListenRequestMs < 2500L) return;
        lastManualListenRequestMs = now;
        lastStarInitiativeRequestMs = now;

        syncStarInitiativeRuntimePrefs();
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
        String selected = optStringEither(json, "selectedStarId", "SelectedStarId", selectedStarName());
        selectStarInSpinner(selected);
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

        if (isTranscriptBridgeEvent(type)) {
            handleTranscriptBridgeEvent(event, type, seq, starId, message);
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
                appendProdConversationLine(starId, clip(visible, 900));
            }
            String header = starId + " — response ready";
            if (transcript.length() > 0) header += "\nTranscript: " + transcript;
            if (storyTitle.length() > 0) header += "\nStory: " + storyTitle;
            if (storySummary.length() > 0) header += "\nSummary: " + storySummary;
            responseText.setText(header + "\n\n" + clip(visible, storyMarkdown.length() > 0 ? 20000 : 8000));
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
        if (prodBreakupButton != null) prodBreakupButton.setVisibility(View.GONE);
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
        String top = formatTopAffectedEmotions(optJsonArrayEither(state, "topAffectedEmotions", "TopAffectedEmotions"));
        if (top.length() == 0) top = optStringEither(state, "message", "Message", "Top emotions: none yet.");
        currentTopAffectedEmotions = top;
        updateEmotionVectorsFromJson(state);
        if (starStateText != null) {
            String base = starStateText.getText().toString().split("\nTop emotions:")[0].split("\nToday:")[0].split("\nInterstar state:")[0];
            starStateText.setText(base + "\n" + currentTopAffectedEmotions + "\n" + currentDailyStarState + "\n" + currentInterstarStatus + " — " + status);
        }
        appendVoiceDebug("Emotion vector settled", currentTopAffectedEmotions);
    }

    private void updateStarStateUi(String starId, boolean met, boolean ally, int heart, int trust, int romance, double connection, boolean ready, boolean girlfriend, String status) {
        selectedStarIsMet = met;
        selectedStarIsAlly = ally;
        selectedStarIsGirlfriend = girlfriend;
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
            wavAudioQueue.enqueue(new PhoneVoiceClip(localWav, "local 24 kHz test beep", checksum, false, "", 0L, 0, new int[0]));
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
                byte[] bytes = readAllBytes(getContentResolver().openInputStream(uri));
                String mimeType = getContentResolver().getType(uri);
                sendPhoto(bytes, mimeType == null || mimeType.length() == 0 ? "image/jpeg" : mimeType);
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

    private byte[] readAllBytes(InputStream input) throws Exception {
        if (input == null) return new byte[0];
        try {
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = input.read(buffer)) >= 0) {
                if (read > 0) output.write(buffer, 0, read);
            }
            return output.toByteArray();
        } finally {
            try { input.close(); } catch (Exception ignored) { }
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
        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Image request sent; keeping the screen on while the Star answers.");
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
        pauseStarInitiativeBackgroundService("Text request has priority over at-rest initiative.");

        try {
            JSONObject json = baseRequestJson();
            json.put("playerText", playerText);
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
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            startRecordingAfterAudioPermission = true;
            requestAudioPermissionIfNeeded();
            setDebug("Microphone permission needed", "Grant RECORD_AUDIO and voice recording will start.");
            return;
        }

        try {
            int minBuffer = AudioRecord.getMinBufferSize(
                    SAMPLE_RATE,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            if (minBuffer <= 0) minBuffer = SAMPLE_RATE;
            int bufferSize = Math.max(minBuffer, SAMPLE_RATE * 2);
            AudioFormat inputFormat = new AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                    .build();

            recordedPcm = new ByteArrayOutputStream();
            audioRecord = new AudioRecord.Builder()
                    .setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
                    .setAudioFormat(inputFormat)
                    .setBufferSizeInBytes(bufferSize)
                    .build();
            if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                audioRecord.release();
                audioRecord = null;
                throw new IllegalStateException("AudioRecord did not initialize.");
            }

            audioRecord.startRecording();
            recording = true;
            if (recordButton != null) recordButton.setText("Stop / Send");
            updateProdRecordingUi();
            setDebug("Recording", "Speak now. Press Send Voice when finished, or Cancel to discard.");

            recordingThread = new Thread(() -> {
                byte[] buffer = new byte[2048];
                while (recording && audioRecord != null) {
                    int read = audioRecord.read(buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                    if (read > 0) {
                        synchronized (this) {
                            if (recordedPcm != null) recordedPcm.write(buffer, 0, read);
                        }
                    }
                }
            }, "six-stars-mobile-recorder");
            recordingThread.start();
        } catch (Exception ex) {
            recording = false;
            if (audioRecord != null) {
                try { audioRecord.release(); } catch (Exception ignored) { }
                audioRecord = null;
            }
            updateProdRecordingUi();
            setDebug("Microphone could not start", ex.toString());
            appendVoiceDebug("The Last Cat could not start recording", ex.toString());
        }
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
        updateProdRecordingUi();

        if (send && wasRecording) {
            byte[] pcm;
            synchronized (this) {
                pcm = recordedPcm == null ? new byte[0] : recordedPcm.toByteArray();
            }
            if (pcm.length < SAMPLE_RATE / 2) {
                setDebug("Recording too short", "Captured " + pcm.length + " bytes. Record at least about one second.");
                return;
            }
            appendProdConversationLine("You", "[voice message]");
            sendAudio(pcm);
        }
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

        setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, true, "Voice request sent; keeping the screen on through transcription, LLM, and speech.");
        pauseStarInitiativeBackgroundService("Voice request has priority over at-rest initiative.");
        foregroundRequestExecutor.execute(() -> {
            try {
                byte[] wav = wavFromPcm16(pcm16, SAMPLE_RATE, 1);
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
                String voiceMessage = optStringEither(json, "voiceMessage", "VoiceMessage", "");
                String voiceError = optStringEither(json, "voiceError", "VoiceError", "");
                long serverElapsedMs = optLongEither(json, "serverElapsedMs", "ServerElapsedMs", 0L);
                long runtimeElapsedMs = optLongEither(json, "runtimeElapsedMs", "RuntimeElapsedMs", 0L);
                long gateWaitMs = optLongEither(json, "gateWaitMs", "GateWaitMs", 0L);
                long clientQueueMs = optLongEither(json, "clientQueueMs", "ClientQueueMs", 0L);
                long clientSocketRoundTripMs = optLongEither(json, "clientSocketRoundTripMs", "ClientSocketRoundTripMs", 0L);
                long clientAfterSendMs = optLongEither(json, "clientAfterSendMs", "ClientAfterSendMs", 0L);
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
                    appendProdConversationLine(starId, clip(visibleResponse, 900));
                }

                String header = title + " — " + starId;
                if (transcript.length() > 0) header += "\nTranscript: " + transcript;
                if (storyTitle.length() > 0) header += "\nStory: " + storyTitle;
                if (storySummary.length() > 0) header += "\nSummary: " + storySummary;
                responseText.setText(header + "\n\n" + clip(visibleResponse, storyMarkdown.length() > 0 ? 20000 : 8000));

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
            } catch (Exception ex) {
                responseText.setText(clip(body, 8000));
                setDebug(title + " (raw)", clip(body, 900));
            } finally {
                setKeepScreenAwake(KEEP_AWAKE_REASON_REQUEST, false, title + " finished; screen can rest after any streaming voice/playback completes.");
                mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("foreground conversation finished", false), 30000L);
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
            resetVoiceStreamStateForSocketReconnect();
            runOnUiThread(() -> {
                audioStatusText.setText("Phone voice socket connected: " + host + ":" + port);
                appendVoiceDebug("Elusiaa connected the voice socket", host + ":" + port);
                appendVoiceDebug("Khavia reset the phone voice stream guard", "Fresh socket connection; accepting the desktop stream counter from the beginning again.");
                mainHandler.postDelayed(() -> syncStarInitiativeBackgroundService("phone voice socket reconnected", false), 3500L);
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
            setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, false, "Phone voice socket disconnected.");
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
            int audioSequence = header.optInt("audioSequence", header.optInt("chunkIndex", 0));
            String starId = header.optString("starId", "Star");
            String streamId = header.optString("streamId", "");
            long streamOrdinal = header.optLong("streamOrdinal", 0L);
            int[] speechActivitySegmentsMs = parseSpeechActivitySegments(header.optString("speechActivitySegmentsMs", ""));
            if (!acceptVoiceStreamFrame(streamId, streamOrdinal, starId)) {
                droppedStaleVoiceFrames++;
                runOnUiThread(() -> appendVoiceDebug("Nyxari dropped a stale voice frame", "incomingStream=" + streamOrdinal + ", activeStream=" + activeVoiceStreamOrdinal + ", dropped=" + droppedStaleVoiceFrames));
                return;
            }
            PhoneVoiceClip clip = new PhoneVoiceClip(wav, starId + " chunk " + chunkNumber + "/" + totalChunks, receivedChecksum, true, streamId, streamOrdinal, audioSequence, speechActivitySegmentsMs);
            OrderedEnqueueResult ordered = enqueueDesktopVoiceClipInOrder(clip);
            runOnUiThread(() -> {
                audioStatusText.setText((ordered.queuedNow > 0 ? "Queued" : ordered.dropped ? "Dropped" : "Buffered")
                        + " phone voice chunk " + chunkNumber
                        + "/" + totalChunks
                        + " — stream " + streamOrdinal
                        + " — seq " + audioSequence
                        + " — " + wav.length + " bytes — sha256 " + shortHash(receivedChecksum)
                        + (checksumOk ? " ✓" : " mismatch"));
                appendVoiceDebug(starId + " voice chunk arrived", "stream=" + streamOrdinal + ", seq=" + audioSequence + ", expected=" + ordered.nextExpected + ", buffered=" + ordered.buffered + ", queuedNow=" + ordered.queuedNow + ", dropped=" + ordered.dropped + ", chunk=" + chunkNumber + "/" + totalChunks + ", speechSegments=" + (speechActivitySegmentsMs.length / 2) + ", bytes=" + wav.length + ", frameSha256=" + shortHash(receivedChecksum) + ", checksum=" + (checksumOk ? "ok" : "mismatch expected " + shortHash(expectedChecksum)));
            });
            return;
        }

        String text = new String(payload, StandardCharsets.UTF_8);
        String message = text;
        try {
            JSONObject status = new JSONObject(text);
            message = status.optString("message", text);
            handleVoiceStreamControl(status);
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

    private int[] parseSpeechActivitySegments(String compact) {
        if (compact == null || compact.trim().length() == 0) return new int[0];
        String[] parts = compact.split(";");
        ArrayList<Integer> values = new ArrayList<>();
        for (String part : parts) {
            if (part == null) continue;
            String[] pair = part.trim().split(":");
            if (pair.length != 2) continue;
            try {
                int startMs = Math.max(0, Integer.parseInt(pair[0].trim()));
                int durationMs = Math.max(40, Math.min(4000, Integer.parseInt(pair[1].trim())));
                values.add(startMs);
                values.add(durationMs);
                if (values.size() >= 480) break;
            } catch (Exception ignored) { }
        }
        int[] result = new int[values.size()];
        for (int i = 0; i < values.size(); i++) result[i] = values.get(i);
        return result;
    }

    private boolean acceptVoiceStreamFrame(String streamId, long streamOrdinal, String starId) {
        if (streamOrdinal <= 0L || streamId == null || streamId.length() == 0) return true;
        boolean startedNew = false;
        synchronized (voiceStreamStateLock) {
            boolean sameStream = streamId.equals(activeVoiceStreamId);
            if (!sameStream && activeVoiceStreamId.length() > 0 && streamOrdinal < activeVoiceStreamOrdinal) {
                // The desktop can restart its stream counter after an app/socket reconnect. A valid
                // new stream should arrive with a streamAction=start status frame first; that status
                // frame resets activeVoiceStreamOrdinal even when the ordinal is lower. If no start
                // frame has made this id active, keep treating lower-ordinal frames as stale.
                return false;
            }
            if (!sameStream || streamOrdinal > activeVoiceStreamOrdinal) {
                activeVoiceStreamOrdinal = streamOrdinal;
                activeVoiceStreamId = streamId;
                startedNew = true;
                resetReceivedVoiceAccumulatorLocked();
                resetOrderedVoiceQueueLocked();
            }
        }
        if (startedNew) {
            wavAudioQueue.clearForNewStream();
            final long ordinal = streamOrdinal;
            final String id = streamId;
            runOnUiThread(() -> {
                setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, true, starId + " voice stream started.");
                appendVoiceDebug(starId + " opened a fresh voice stream", "stream=" + ordinal + ", id=" + id + ". Cleared stale queued Android audio.");
            });
        }
        return true;
    }

    private void handleVoiceStreamControl(JSONObject status) {
        String action = status.optString("streamAction", "");
        if (action.length() == 0) return;
        String streamId = status.optString("streamId", "");
        long streamOrdinal = status.optLong("streamOrdinal", 0L);
        String starId = status.optString("starId", "Star");
        if (streamOrdinal <= 0L) return;

        boolean clear = false;
        synchronized (voiceStreamStateLock) {
            if ("start".equalsIgnoreCase(action)) {
                boolean sameStream = streamOrdinal == activeVoiceStreamOrdinal && streamId.equals(activeVoiceStreamId);
                if (!sameStream) {
                    // A fresh desktop process/socket may restart streamOrdinal at 1. Treat the
                    // explicit start frame as authoritative even when its ordinal is lower than a
                    // previous app session's active stream, otherwise the phone drops every new chunk
                    // as stale and the text appears without sound.
                    activeVoiceStreamOrdinal = streamOrdinal;
                    activeVoiceStreamId = streamId;
                    clear = true;
                    resetReceivedVoiceAccumulatorLocked();
                    resetOrderedVoiceQueueLocked();
                }
            } else if ("cancel".equalsIgnoreCase(action) || "end".equalsIgnoreCase(action)) {
                if (streamId.equals(activeVoiceStreamId) || streamOrdinal >= activeVoiceStreamOrdinal) {
                    activeVoiceStreamOrdinal = streamOrdinal;
                    activeVoiceStreamId = streamId;
                    clear = "cancel".equalsIgnoreCase(action);
                    if (clear) {
                        resetReceivedVoiceAccumulatorLocked();
                        resetOrderedVoiceQueueLocked();
                    }
                }
            }
        }

        if (clear) {
            wavAudioQueue.clearForNewStream();
        }

        final boolean clearedForStream = clear;
        if ("start".equalsIgnoreCase(action)) {
            runOnUiThread(() -> {
                setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, true, starId + " voice stream started.");
                if (clearedForStream) appendVoiceDebug(starId + " voice stream " + action, "stream=" + streamOrdinal + ", id=" + streamId + ". Cleared queued Android audio before continuing.");
            });
        } else if ("end".equalsIgnoreCase(action) || "cancel".equalsIgnoreCase(action)) {
            runOnUiThread(() -> {
                if (clearedForStream) appendVoiceDebug(starId + " voice stream " + action, "stream=" + streamOrdinal + ", id=" + streamId + ". Cleared queued Android audio before continuing.");
                setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, false, starId + " voice stream " + action + ".");
            });
        }
    }

    private boolean isCurrentVoiceClip(PhoneVoiceClip clip) {
        if (clip == null || !clip.fromDesktop || clip.streamOrdinal <= 0L) return true;
        synchronized (voiceStreamStateLock) {
            return clip.streamOrdinal == activeVoiceStreamOrdinal && clip.streamId.equals(activeVoiceStreamId);
        }
    }

    private OrderedEnqueueResult enqueueDesktopVoiceClipInOrder(PhoneVoiceClip clip) {
        if (clip == null) return new OrderedEnqueueResult(0, 0, nextExpectedDesktopVoiceSequence, true);
        if (!isCurrentVoiceClip(clip)) return new OrderedEnqueueResult(0, 0, nextExpectedDesktopVoiceSequence, true);

        int queuedNow = 0;
        boolean dropped = false;
        synchronized (orderedVoiceQueueLock) {
            if (clip.audioSequence < nextExpectedDesktopVoiceSequence
                    || acceptedDesktopVoiceSequences.contains(clip.audioSequence)
                    || pendingDesktopVoiceClips.containsKey(clip.audioSequence)) {
                dropped = true;
            } else {
                pendingDesktopVoiceClips.put(clip.audioSequence, clip);
                while (true) {
                    PhoneVoiceClip ready = pendingDesktopVoiceClips.remove(nextExpectedDesktopVoiceSequence);
                    if (ready == null) break;
                    acceptedDesktopVoiceSequences.add(ready.audioSequence);
                    wavAudioQueue.enqueue(ready);
                    nextExpectedDesktopVoiceSequence++;
                    queuedNow++;
                }
            }
            return new OrderedEnqueueResult(queuedNow, pendingDesktopVoiceClips.size(), nextExpectedDesktopVoiceSequence, dropped);
        }
    }

    private void resetOrderedVoiceQueueLocked() {
        synchronized (orderedVoiceQueueLock) {
            pendingDesktopVoiceClips.clear();
            acceptedDesktopVoiceSequences.clear();
            nextExpectedDesktopVoiceSequence = 0;
        }
    }

    private void resetReceivedVoiceAccumulatorLocked() {
        synchronized (this) {
            lastVoicePcmAccumulator = null;
            lastVoiceAccumulatorTotalChunks = 0;
            lastVoiceAccumulatorChunks = 0;
        }
    }


    private void resetVoiceStreamStateForSocketReconnect() {
        synchronized (voiceStreamStateLock) {
            activeVoiceStreamId = "";
            activeVoiceStreamOrdinal = 0L;
            droppedStaleVoiceFrames = 0;
            resetReceivedVoiceAccumulatorLocked();
            resetOrderedVoiceQueueLocked();
        }
        wavAudioQueue.clearForNewStream();
        setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, false, "Voice socket reconnected; old stream state cleared.");
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
        setKeepScreenAwake(KEEP_AWAKE_REASON_VOICE_STREAM, false, "Phone voice socket closed.");
    }



    private String appendCurrentVoiceClipToSavedAudio(PhoneVoiceClip clip) {
        if (clip == null || clip.wav == null || clip.wav.length == 0) return "";
        try {
            ParsedWav parsed = ParsedWav.parse(clip.wav);
            byte[] combinedWav;
            synchronized (this) {
                if (lastVoicePcmAccumulator == null
                        || parsed.sampleRate != lastVoiceAccumulatorSampleRate
                        || parsed.channels != lastVoiceAccumulatorChannels) {
                    lastVoicePcmAccumulator = new ByteArrayOutputStream();
                    lastVoiceAccumulatorSampleRate = parsed.sampleRate;
                    lastVoiceAccumulatorChannels = parsed.channels;
                    lastVoiceAccumulatorTotalChunks = 0;
                    lastVoiceAccumulatorChunks = 0;
                }
                lastVoicePcmAccumulator.write(parsed.pcm, 0, parsed.pcm.length);
                lastVoiceAccumulatorChunks++;
                lastVoiceAccumulatorTotalChunks = Math.max(lastVoiceAccumulatorTotalChunks, clip.audioSequence + 1);
                combinedWav = wavFromPcm16(lastVoicePcmAccumulator.toByteArray(), lastVoiceAccumulatorSampleRate, lastVoiceAccumulatorChannels);
            }
            String combinedChecksum = sha256Hex(combinedWav);
            saveLastVoiceAudio(combinedWav, combinedChecksum);
            return combinedChecksum;
        } catch (Exception ex) {
            saveLastVoiceAudio(clip.wav, "");
            runOnUiThread(() -> appendVoiceDebug("Khavia saved only the latest voice chunk", "Could not append clip to saved stream: " + ex.toString()));
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
                wavAudioQueue.enqueue(new PhoneVoiceClip(wav, "saved phone voice replay", checksum, false, "", 0L, 0, new int[0]));
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
    }

    private final class WavAudioQueue {
        private final BlockingQueue<PhoneVoiceClip> queue = new LinkedBlockingQueue<>();
        private volatile boolean running;
        private volatile AudioTrack activeTrack;
        private Thread thread;

        void start() {
            if (running) return;
            running = true;
            thread = new Thread(this::loop, "six-stars-wav-player");
            thread.start();
        }

        void stop() {
            running = false;
            clearForNewStream();
        }

        void clearForNewStream() {
            queue.clear();
            runOnUiThread(() -> {
                cancelStarVoiceAnimationMarkers("Audio queue cleared for a new Star voice stream.");
                setKeepScreenAwake(KEEP_AWAKE_REASON_AUDIO_PLAYBACK, false, "Audio queue cleared for a new Star voice stream.");
            });
            AudioTrack track = activeTrack;
            if (track != null) {
                try { track.pause(); } catch (Exception ignored) { }
                try { track.flush(); } catch (Exception ignored) { }
                try { track.stop(); } catch (Exception ignored) { }
            }
            if (thread != null) thread.interrupt();
        }

        void enqueue(PhoneVoiceClip clip) {
            if (clip != null && clip.wav != null && clip.wav.length > 0) queue.offer(clip);
        }

        private void loop() {
            while (running) {
                try {
                    PhoneVoiceClip clip = queue.take();
                    if (!isCurrentVoiceClip(clip)) {
                        final PhoneVoiceClip dropped = clip;
                        runOnUiThread(() -> appendVoiceDebug("Nyxari skipped stale queued audio", dropped.label + ", stream=" + dropped.streamOrdinal + ", active=" + activeVoiceStreamOrdinal));
                        continue;
                    }
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
            if (minBuffer <= 0) minBuffer = parsed.sampleRate * parsed.channels * 2 / 5;
            int bufferSize = Math.max(minBuffer, Math.min(parsed.pcm.length, parsed.sampleRate * parsed.channels * 2));
            long durationMs = (long) parsed.pcm.length * 1000L / Math.max(1, parsed.sampleRate * parsed.channels * 2);

            AudioAttributes audioAttributes = new AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
                    .build();
            AudioFormat outputFormat = new AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(parsed.sampleRate)
                    .setChannelMask(channelConfig)
                    .build();
            AudioTrack track = new AudioTrack.Builder()
                    .setAudioAttributes(audioAttributes)
                    .setAudioFormat(outputFormat)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setBufferSizeInBytes(bufferSize)
                    .build();
            if (track.getState() != AudioTrack.STATE_INITIALIZED) {
                track.release();
                throw new IllegalStateException("AudioTrack did not initialize for " + parsed.sampleRate + " Hz audio.");
            }

            AtomicBoolean playbackFinished = new AtomicBoolean(false);
            AtomicBoolean animationStarted = new AtomicBoolean(false);
            Thread playbackStartWatcher = null;

            activeTrack = track;
            try {
                if (!isCurrentVoiceClip(clip)) return;
                String savedChecksum = clip.fromDesktop ? appendCurrentVoiceClipToSavedAudio(clip) : "";
                if (clip.fromDesktop) {
                    runOnUiThread(() -> appendVoiceDebug(
                            "Khavia saved the ordered phone voice stream",
                            "afterSeq=" + clip.audioSequence
                                    + ", savedSha256=" + shortHash(savedChecksum)
                                    + ", savedChunks=" + lastVoiceAccumulatorChunks + "/" + lastVoiceAccumulatorTotalChunks));
                }

                int offset = 0;
                int bytesPerFrame = Math.max(1, parsed.channels * 2);
                int expectedFrames = parsed.pcm.length / bytesPerFrame;

                // Prime AudioTrack before starting it. The prime write is capped at the
                // configured stream buffer so a blocking write cannot wait for playback.
                int primeBytes = Math.max(1, Math.min(bufferSize, parsed.sampleRate * bytesPerFrame / 4));
                int firstWriteSize = Math.min(parsed.pcm.length, primeBytes);
                int firstWrite = track.write(parsed.pcm, 0, firstWriteSize, AudioTrack.WRITE_BLOCKING);
                if (firstWrite <= 0) {
                    throw new IllegalStateException("AudioTrack rejected the first PCM buffer: " + firstWrite);
                }
                offset += firstWrite;

                AudioTrack.OnPlaybackPositionUpdateListener playbackListener = new AudioTrack.OnPlaybackPositionUpdateListener() {
                    @Override public void onMarkerReached(AudioTrack audioTrack) {
                        long framePosition = Math.max(1L, Integer.toUnsignedLong(audioTrack.getPlaybackHeadPosition()));
                        signalPlaybackStarted(
                                clip, parsed.sampleRate, durationMs, framePosition,
                                "marker", animationStarted, playbackFinished);
                    }

                    @Override public void onPeriodicNotification(AudioTrack audioTrack) {
                        // The one-frame marker is used for startup. Periodic notifications are disabled.
                    }
                };
                track.setPlaybackPositionUpdateListener(playbackListener, mainHandler);
                int markerResult = track.setNotificationMarkerPosition(1);

                track.play();
                runOnUiThread(() -> {
                    setKeepScreenAwake(KEEP_AWAKE_REASON_AUDIO_PLAYBACK, true, "Starting " + clip.label + ".");
                    audioStatusText.setText("Starting " + clip.label + " — " + parsed.sampleRate + " Hz — sha256 " + shortHash(clip.sha256));
                });

                // Some devices do not dispatch the one-frame marker reliably during route warmup.
                // Keep writing audio immediately and confirm presentation independently with
                // AudioTimestamp, falling back to the continuously advancing playback head.
                playbackStartWatcher = startPlaybackStartWatcher(
                        track, clip, parsed.sampleRate, durationMs, markerResult,
                        animationStarted, playbackFinished);

                while (offset < parsed.pcm.length && isCurrentVoiceClip(clip)) {
                    int toWrite = Math.min(parsed.pcm.length - offset, Math.max(1024, bufferSize));
                    int wrote = track.write(parsed.pcm, offset, toWrite, AudioTrack.WRITE_BLOCKING);
                    if (wrote <= 0) break;
                    offset += wrote;
                }

                // MODE_STREAM write() only guarantees the bytes were accepted by AudioTrack, not that
                // every queued sample has already reached the speaker. Wait until the playback head
                // reaches the final frame, then move immediately to the next queued WAV.
                long waitStarted = System.currentTimeMillis();
                long maxWaitMs = Math.max(250L, durationMs + 750L);
                while (running && isCurrentVoiceClip(clip) && track.getPlaybackHeadPosition() < expectedFrames
                        && System.currentTimeMillis() - waitStarted < maxWaitMs) {
                    try { Thread.sleep(12L); } catch (InterruptedException ignored) { break; }
                }

                if (track.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) track.stop();
                playbackFinished.set(true);
                if (playbackStartWatcher != null) playbackStartWatcher.interrupt();
                runOnUiThread(() -> appendVoiceDebug(
                        "Elusiaa finished playing audio",
                        clip.label + ", sha256=" + shortHash(clip.sha256) + ", queuedNext=" + queue.size()));
            } finally {
                playbackFinished.set(true);
                if (playbackStartWatcher != null) playbackStartWatcher.interrupt();
                if (activeTrack == track) activeTrack = null;
                try {
                    track.setNotificationMarkerPosition(0);
                    track.setPlaybackPositionUpdateListener(null);
                } catch (Exception ignored) { }
                try {
                    if (track.getPlayState() == AudioTrack.PLAYSTATE_PLAYING) track.stop();
                } catch (Exception ignored) { }
                track.release();
                runOnUiThread(() -> {
                    finishStarVoiceAnimationMarkers(clip);
                    setKeepScreenAwake(
                            KEEP_AWAKE_REASON_AUDIO_PLAYBACK,
                            false,
                            "Audio playback finished.");
                });
            }
        }

        private Thread startPlaybackStartWatcher(
                AudioTrack track,
                PhoneVoiceClip clip,
                int sampleRate,
                long durationMs,
                int markerResult,
                AtomicBoolean animationStarted,
                AtomicBoolean playbackFinished) {
            Thread watcher = new Thread(() -> {
                AudioTimestamp timestamp = new AudioTimestamp();
                long deadlineMs = SystemClock.elapsedRealtime()
                        + Math.max(5000L, Math.min(30000L, durationMs + 5000L));

                while (running
                        && !playbackFinished.get()
                        && !animationStarted.get()
                        && isCurrentVoiceClip(clip)
                        && SystemClock.elapsedRealtime() < deadlineMs) {
                    long framePosition = 0L;
                    String source = "playback-head";
                    try {
                        if (track.getTimestamp(timestamp) && timestamp.framePosition > 0L) {
                            framePosition = timestamp.framePosition;
                            source = "audio-timestamp";
                        } else {
                            framePosition = Integer.toUnsignedLong(track.getPlaybackHeadPosition());
                        }
                    } catch (IllegalStateException released) {
                        break;
                    }

                    if (framePosition > 0L) {
                        signalPlaybackStarted(
                                clip, sampleRate, durationMs, framePosition, source,
                                animationStarted, playbackFinished);
                        break;
                    }

                    try {
                        Thread.sleep(10L);
                    } catch (InterruptedException interrupted) {
                        break;
                    }
                }

                if (!playbackFinished.get() && !animationStarted.get() && isCurrentVoiceClip(clip)) {
                    runOnUiThread(() -> appendVoiceDebug(
                            "Elusiaa could not confirm audio output",
                            clip.label + ", markerResult=" + markerResult
                                    + ", durationMs=" + durationMs
                                    + ", sha256=" + shortHash(clip.sha256)));
                }
            }, "six-stars-playback-start-watcher");
            watcher.start();
            return watcher;
        }

        private void signalPlaybackStarted(
                PhoneVoiceClip clip,
                int sampleRate,
                long durationMs,
                long framePosition,
                String source,
                AtomicBoolean animationStarted,
                AtomicBoolean playbackFinished) {
            if (playbackFinished.get()
                    || !running
                    || !isCurrentVoiceClip(clip)
                    || !animationStarted.compareAndSet(false, true)) {
                return;
            }

            long detectedAtMs = SystemClock.elapsedRealtime();
            long elapsedAtDetectionMs = Math.max(0L, framePosition) * 1000L / Math.max(1, sampleRate);
            runOnUiThread(() -> {
                if (playbackFinished.get() || !running || !isCurrentVoiceClip(clip)) return;

                long callbackDelayMs = Math.max(0L, SystemClock.elapsedRealtime() - detectedAtMs);
                long playbackElapsedMs = Math.min(
                        Math.max(0L, durationMs),
                        elapsedAtDetectionMs + callbackDelayMs);
                audioStatusText.setText("Playing " + clip.label + " — " + sampleRate + " Hz — sha256 " + shortHash(clip.sha256));
                appendVoiceDebug(
                        "Elusiaa started playing audio",
                        clip.label + ", source=" + source
                                + ", playbackFrames=" + framePosition
                                + ", playbackElapsedMs=" + playbackElapsedMs
                                + ", durationMs=" + durationMs
                                + ", bytes=" + clip.wav.length
                                + ", sha256=" + shortHash(clip.sha256));
                startStarVoiceAnimationMarkers(clip, durationMs, playbackElapsedMs);
            });
        }
    }

    private static final class OrderedEnqueueResult {
        final int queuedNow;
        final int buffered;
        final int nextExpected;
        final boolean dropped;

        OrderedEnqueueResult(int queuedNow, int buffered, int nextExpected, boolean dropped) {
            this.queuedNow = queuedNow;
            this.buffered = buffered;
            this.nextExpected = nextExpected;
            this.dropped = dropped;
        }
    }

    private static final class PhoneVoiceClip {
        final byte[] wav;
        final String label;
        final String sha256;
        final boolean fromDesktop;
        final String streamId;
        final long streamOrdinal;
        final int audioSequence;
        final int[] speechActivitySegmentsMs;

        PhoneVoiceClip(byte[] wav, String label, String sha256, boolean fromDesktop, String streamId, long streamOrdinal, int audioSequence, int[] speechActivitySegmentsMs) {
            this.wav = wav;
            this.label = label == null ? "phone voice" : label;
            this.sha256 = sha256 == null ? "" : sha256;
            this.fromDesktop = fromDesktop;
            this.streamId = streamId == null ? "" : streamId;
            this.streamOrdinal = streamOrdinal;
            this.audioSequence = audioSequence;
            this.speechActivitySegmentsMs = speechActivitySegmentsMs == null ? new int[0] : speechActivitySegmentsMs;
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
