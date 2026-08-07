package com.wds.sixstarsmobile;

import android.app.Application;
import android.os.Handler;
import android.os.Looper;

import androidx.annotation.NonNull;
import androidx.lifecycle.AndroidViewModel;
import androidx.lifecycle.LiveData;
import androidx.lifecycle.MutableLiveData;

import org.json.JSONObject;

import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Lifecycle-safe coordinator for the Android companion. MainActivity renders views and forwards user
 * intent; network, recording, playback, persistence, prepared speech, and background initiative work
 * are owned by services here.
 */
public final class MainViewModel extends AndroidViewModel {
    public static final class UiState {
        public final boolean connected;
        public final boolean recording;
        public final boolean waitingForReply;
        public final boolean voicePlaying;
        public final String selectedStarId;
        public final String connectionStatus;
        public final String statusMessage;

        UiState(boolean connected, boolean recording, boolean waitingForReply, boolean voicePlaying,
                String selectedStarId, String connectionStatus, String statusMessage) {
            this.connected = connected;
            this.recording = recording;
            this.waitingForReply = waitingForReply;
            this.voicePlaying = voicePlaying;
            this.selectedStarId = selectedStarId == null ? "Elusiaa" : selectedStarId;
            this.connectionStatus = connectionStatus == null ? "Disconnected" : connectionStatus;
            this.statusMessage = statusMessage == null ? "" : statusMessage;
        }

        UiState withConnection(boolean value, String status) {
            return new UiState(value, recording, waitingForReply, voicePlaying, selectedStarId, status, statusMessage);
        }
        UiState withRecording(boolean value) {
            return new UiState(connected, value, waitingForReply, voicePlaying, selectedStarId, connectionStatus, statusMessage);
        }
        UiState withWaiting(boolean value, String message) {
            return new UiState(connected, recording, value, voicePlaying, selectedStarId, connectionStatus, message);
        }
        UiState withVoice(boolean value) {
            return new UiState(connected, recording, waitingForReply, value, selectedStarId, connectionStatus, statusMessage);
        }
        UiState withStar(String value) {
            return new UiState(connected, recording, waitingForReply, voicePlaying, value, connectionStatus, statusMessage);
        }
    }

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private final ExecutorService backgroundExecutor = Executors.newFixedThreadPool(3);
    private final MutableLiveData<UiState> uiState = new MutableLiveData<>();
    private final MutableLiveData<MobileBridgeEventService.Event> bridgeEvents = new MutableLiveData<>();
    private final MutableLiveData<String> protocolDebug = new MutableLiveData<>();

    private final MobileSettingsService settings;
    private final MobileBridgeEventService eventService;
    private final BridgeConnectionService bridge;
    private final ConversationService conversations;
    private final VoiceCaptureService voiceCapture;
    private final PhotoAttachmentService photos;
    private final PreparedSpeechCoordinator preparedSpeech;
    private final StarInitiativeCoordinator initiatives;
    private final StarAnimationCoordinator animations;
    private final PhoneVoicePlaybackService phoneVoice;
    private boolean initialized;
    private boolean bridgeEventsPrimed;
    private long lastBridgeEventSequence;

    public MainViewModel(@NonNull Application application) {
        super(application);
        settings = new MobileSettingsService(application);
        eventService = new MobileBridgeEventService();
        UiState initial = new UiState(false, false, false, false, settings.selectedStarId(), "Connecting…", "");
        uiState.setValue(initial);
        bridge = new BridgeConnectionService(settings, new BridgeConnectionService.Listener() {
            @Override public void onConnected(long latestSequence) { handleBridgeConnected(latestSequence); }
            @Override public void onDisconnected(String reason) { postState(current().withConnection(false, reason)); }
            @Override public void onEvent(JSONObject event) { handleBridgeEvent(event); }
            @Override public void onProtocolDebug(String message) { protocolDebug.postValue(message); }
        });
        conversations = new ConversationService(bridge);
        voiceCapture = new VoiceCaptureService(application);
        photos = new PhotoAttachmentService(application);
        preparedSpeech = new PreparedSpeechCoordinator(application, settings, backgroundExecutor);
        initiatives = new StarInitiativeCoordinator(application, settings);
        animations = new StarAnimationCoordinator(mainHandler, state -> { });
        phoneVoice = new PhoneVoicePlaybackService(application);
    }


    private synchronized void handleBridgeConnected(long latestSequence) {
        postState(current().withConnection(true, "Connected"));
        if (!bridgeEventsPrimed) {
            bridgeEventsPrimed = true;
            lastBridgeEventSequence = Math.max(lastBridgeEventSequence, latestSequence);
        } else {
            bridge.subscribeAfter(lastBridgeEventSequence);
        }

        // The phone voice transport is a separate TCP socket. Before the UI-service refactor,
        // MainActivity called testConnection(false) from the control WebSocket onConnected
        // callback; that health request discovered audioPort and opened this socket. Keeping only
        // the control WebSocket connected leaves the desktop with zero phone-audio clients, so it
        // returns text but cannot deliver any WAV chunks. Bootstrap the voice socket here instead,
        // where connection lifecycle now belongs.
        connectPhoneVoiceAfterBridgeConnected();
    }

    private void connectPhoneVoiceAfterBridgeConnected() {
        if (!settings.playOnPhone()) {
            phoneVoice.disconnect();
            return;
        }

        bridge.request("health", new JSONObject(), 10000L).whenComplete((health, error) -> {
            if (error != null) {
                protocolDebug.postValue("Phone voice bootstrap health request failed: " + error.getMessage());
                return;
            }

            try {
                String bridgeUrl = settings.bridgeUrl();
                String host = new URL(bridgeUrl).getHost();
                int audioPort = health == null
                        ? defaultAudioPort(bridgeUrl)
                        : health.optInt("audioPort", defaultAudioPort(bridgeUrl));
                phoneVoice.connect(host, audioPort);
            } catch (Exception ex) {
                protocolDebug.postValue("Phone voice bootstrap failed: " + ex.getMessage());
            }
        });
    }

    private static int defaultAudioPort(String bridgeUrl) {
        try {
            URL url = new URL(bridgeUrl);
            int port = url.getPort() > 0 ? url.getPort() : 8066;
            return port >= 65535 ? port - 1 : port + 1;
        } catch (Exception ignored) {
            return 8067;
        }
    }

    private void handleBridgeEvent(JSONObject payload) {
        MobileBridgeEventService.Event event = eventService.parse(payload);
        synchronized (this) {
            if (event.sequence > 0L && event.sequence <= lastBridgeEventSequence) return;
            if (event.sequence > lastBridgeEventSequence) lastBridgeEventSequence = event.sequence;
        }
        bridgeEvents.postValue(event);
    }

    LiveData<UiState> uiState() { return uiState; }
    LiveData<MobileBridgeEventService.Event> bridgeEvents() { return bridgeEvents; }
    LiveData<String> protocolDebug() { return protocolDebug; }
    MobileSettingsService settings() { return settings; }
    BridgeConnectionService bridge() { return bridge; }
    ConversationService conversations() { return conversations; }
    VoiceCaptureService voiceCapture() { return voiceCapture; }
    PhotoAttachmentService photos() { return photos; }
    PreparedSpeechCoordinator preparedSpeech() { return preparedSpeech; }
    StarInitiativeCoordinator initiatives() { return initiatives; }
    StarAnimationCoordinator animations() { return animations; }

    PhoneVoicePlaybackService phoneVoice() { return phoneVoice; }

    synchronized void initialize() {
        if (initialized) return;
        initialized = true;
        phoneVoice.start();
        bridge.start();
        if (settings.isStarInitiativeEnabled()) initiatives.start("Android view model initialized", true);
    }

    void ensureConnected() { bridge.ensureConnected(); }
    void selectStar(String starId) {
        settings.setSelectedStarId(starId);
        postState(current().withStar(settings.selectedStarId()));
    }
    void setWaitingForReply(boolean value, String message) { postState(current().withWaiting(value, message)); }
    void setRecording(boolean value) { postState(current().withRecording(value)); }
    void setVoicePlaying(boolean value) { postState(current().withVoice(value)); }

    private UiState current() {
        UiState value = uiState.getValue();
        return value == null ? new UiState(false, false, false, false, settings.selectedStarId(), "Disconnected", "") : value;
    }
    private void postState(UiState value) {
        if (Looper.myLooper() == Looper.getMainLooper()) uiState.setValue(value); else uiState.postValue(value);
    }

    @Override protected void onCleared() {
        bridge.close();
        voiceCapture.close();
        phoneVoice.close();
        backgroundExecutor.shutdownNow();
    }
}
