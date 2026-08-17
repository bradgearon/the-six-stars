package com.wds.sixstarsmobile;

import android.app.Application;
import android.os.Handler;
import android.os.Looper;

import androidx.annotation.NonNull;
import androidx.lifecycle.AndroidViewModel;
import androidx.lifecycle.LiveData;
import androidx.lifecycle.MutableLiveData;

import org.json.JSONObject;

import java.util.concurrent.CompletableFuture;
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
    private final MutableLiveData<JSONObject> mobileStatePayload = new MutableLiveData<>();
    private final MutableLiveData<String> protocolDebug = new MutableLiveData<>();

    private final MobileSettingsService settings;
    private final LocalNetworkBindingService localNetwork;
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
        localNetwork = new LocalNetworkBindingService(application);
        eventService = new MobileBridgeEventService();
        UiState initial = new UiState(false, false, false, false, settings.selectedStarId(), "Connecting…", "");
        uiState.setValue(initial);
        bridge = new BridgeConnectionService(settings, localNetwork, new BridgeConnectionService.Listener() {
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
        phoneVoice = new PhoneVoicePlaybackService(application, localNetwork);
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
        synchronizeSelectedStarAndState();
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
                boolean promoted = acceptHealthNetworkInfo(health, true);
                if (promoted) return; // The IPv6 socket will request health again after it connects.

                String activeUrl = bridge.activeBaseUrl();
                String host = BridgeAddressService.hostFromBaseUrl(activeUrl);
                int audioPort = health == null
                        ? defaultAudioPort(activeUrl)
                        : health.optInt("audioPort", defaultAudioPort(activeUrl));
                phoneVoice.connect(host, audioPort);
            } catch (Exception ex) {
                protocolDebug.postValue("Phone voice bootstrap failed: " + ex.getMessage());
            }
        });
    }

    private static int defaultAudioPort(String bridgeUrl) {
        return BridgeAddressService.audioPort(bridgeUrl);
    }

    /** Saves the best IPv6 address advertised by the active desktop socket and optionally promotes it. */
    boolean acceptHealthNetworkInfo(JSONObject health, boolean promote) {
        String discovered = localNetwork.choosePreferredIpv6(BridgeAddressService.discoveredIpv6Candidates(health));
        if (discovered.isEmpty()) discovered = BridgeAddressService.discoveredIpv6(health);
        if (discovered.isEmpty()) {
            if (health != null && health.optBoolean("localConnection", false)
                    && health.has("ipv6Available") && !health.optBoolean("ipv6Available", false)) {
                boolean cleared = settings.setPreferredIpv6Address("");
                if (cleared) protocolDebug.postValue("Cleared the saved desktop IPv6 because the active local adapter no longer exposes one.");
            }
            return false;
        }
        boolean changed = settings.rememberDiscoveredIpv6(discovered);
        if (changed) {
            protocolDebug.postValue("Saved desktop IPv6 from active local socket: " + discovered
                    + " (adapter=" + health.optString("localAdapterName", "unknown") + ").");
        }
        return promote && settings.useIpv6Primary() && bridge.promoteToPreferredIpv6(changed);
    }

    CompletableFuture<String> refreshPreferredIpv6(boolean promote) {
        CompletableFuture<String> result = new CompletableFuture<>();
        bridge.request("health", new JSONObject(), 10000L).whenComplete((health, error) -> {
            if (error != null) {
                result.completeExceptionally(error);
                return;
            }
            String discovered = localNetwork.choosePreferredIpv6(BridgeAddressService.discoveredIpv6Candidates(health));
            if (discovered.isEmpty()) discovered = BridgeAddressService.discoveredIpv6(health);
            if (discovered.isEmpty()) {
                result.completeExceptionally(new IllegalStateException(
                        "The active desktop adapter did not expose a routable local IPv6 address."));
                return;
            }
            boolean changed = settings.rememberDiscoveredIpv6(discovered);
            if (promote && settings.useIpv6Primary()) bridge.promoteToPreferredIpv6(changed);
            result.complete(discovered);
        });
        return result;
    }

    boolean updatePreferredIpv6Settings(String address, boolean useAsPrimary) {
        String raw = address == null ? "" : address.trim();
        String normalized = BridgeAddressService.normalizeIpv6Address(raw);
        if (!raw.isEmpty() && normalized.isEmpty()) return false;
        settings.setPreferredIpv6Address(normalized);
        settings.setUseIpv6Primary(useAsPrimary);
        bridge.reconnectPreferred();
        return true;
    }

    String activeBridgeUrl() { return bridge.activeBaseUrl(); }
    String localRouteDescription() {
        String host = BridgeAddressService.hostFromBaseUrl(bridge.activeBaseUrl());
        return localNetwork.routeDescription(host);
    }

    private void synchronizeSelectedStarAndState() {
        String sessionId = settings.isProductionMode() ? "android-phone-prod" : "android-phone-dev";
        String starId = settings.selectedStarId();
        JSONObject command = new JSONObject();
        try {
            command.put("commandId", "android-startup-select-" + System.currentTimeMillis());
            command.put("commandType", "SelectStar");
            command.put("sourceClientId", "SixStarsMobileAndroid");
            command.put("sessionId", sessionId);
            command.put("starId", starId);
            command.put("reason", "Restore Android's saved Star selection after bridge connection.");
        } catch (Exception ignored) { }

        // Selection is a desktop-UI synchronization command, but the state response already contains
        // every Star. Do not make the phone wait for dispatch before loading its saved Star: a slow
        // or interrupted command used to leave the relationship values and conversation blank for
        // the full command timeout.
        conversations.sendCommand(command, 10000L).whenComplete((ignored, commandError) -> {
            if (commandError != null) {
                protocolDebug.postValue("Desktop Star selection synchronization was deferred: "
                        + commandError.getMessage());
            }
        });
        fetchSelectedStarState(sessionId, starId, 0);
    }

    private void fetchSelectedStarState(String sessionId, String starId, int attempt) {
        JSONObject request = new JSONObject();
        try {
            request.put("sessionId", sessionId);
            request.put("starId", starId);
        } catch (Exception ignored) { }
        conversations.fetchState(request, 15000L).whenComplete((payload, error) -> {
            if (error == null) {
                mobileStatePayload.postValue(payload == null ? new JSONObject() : payload);
                return;
            }
            if (attempt < 2) {
                mainHandler.postDelayed(() -> fetchSelectedStarState(sessionId, starId, attempt + 1),
                        1200L * (attempt + 1L));
                return;
            }
            protocolDebug.postValue("Initial selected Star state refresh failed: " + error.getMessage());
        });
    }

    private void handleBridgeEvent(JSONObject payload) {
        MobileBridgeEventService.Event event = eventService.parse(payload);
        synchronized (this) {
            if (event.sequence > 0L && event.sequence <= lastBridgeEventSequence) return;
            if (event.sequence > lastBridgeEventSequence) lastBridgeEventSequence = event.sequence;
        }
        // MutableLiveData.postValue() coalesces pending values when several socket events arrive
        // before the main thread drains them. That is unacceptable for a transport event stream:
        // a MobileTranscriptReady event can otherwise be overwritten by the Star response/state
        // event that follows it, making the user's spoken line appear to vanish. Queue setValue()
        // onto the main looper instead so every bridge event is delivered in socket order.
        mainHandler.post(() -> bridgeEvents.setValue(event));
    }

    LiveData<UiState> uiState() { return uiState; }
    LiveData<MobileBridgeEventService.Event> bridgeEvents() { return bridgeEvents; }
    LiveData<JSONObject> mobileStatePayload() { return mobileStatePayload; }
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
    void refreshPhoneVoiceConnection() { connectPhoneVoiceAfterBridgeConnected(); }
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
