package com.wds.sixstarsmobile;

import android.app.Service;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.Random;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

public final class StarInitiativeBackgroundService extends Service {
    public static final String ACTION_START = "com.wds.sixstarsmobile.action.START_STAR_INITIATIVE_SERVICE";
    public static final String ACTION_CHECK_NOW = "com.wds.sixstarsmobile.action.CHECK_STAR_INITIATIVE_NOW";
    public static final String ACTION_STOP = "com.wds.sixstarsmobile.action.STOP_STAR_INITIATIVE_SERVICE";
    public static final String ACTION_RESPONSE = "com.wds.sixstarsmobile.STAR_INITIATIVE_SERVICE_RESPONSE";
    public static final String ACTION_STATUS = "com.wds.sixstarsmobile.STAR_INITIATIVE_SERVICE_STATUS";

    private static final String PREFS_NAME = "SixStarsMobilePrefs";
    private static final String PREF_PROD_MODE = "prodMode";
    private static final String PREF_STAR_INITIATIVE_ENABLED = "starInitiativeEnabled";
    private static final String PREF_STAR_INITIATIVE_BACKGROUND_ENABLED = "starInitiativeBackgroundServiceEnabled";
    private static final String PREF_BRIDGE_URL = "bridgeUrl";
    private static final String PREF_SELECTED_STAR_ID = "selectedStarId";
    private static final String PREF_ROMANCE_MODE = "romanceMode";
    private static final String PREF_PREPARED_LISTEN_GENERATION = "preparedListenGeneration";
    private static final String PREF_AREA = "area";
    private static final String PREF_LAST_BACKGROUND_REQUEST_MS = "starInitiativeBackgroundLastRequestMs";
    private static final String PREF_PREPARED_LISTEN_NOT_BEFORE_MS = "preparedListenNotBeforeMs";
    private static final String DEFAULT_BRIDGE_URL = "http://192.168.1.179:8066";
    private static final String LEGACY_BRIDGE_URL = "http://192.168.1.217:8066";

    private final Random random = new Random();
    private final AtomicBoolean checkInFlight = new AtomicBoolean(false);
    private HandlerThread workerThread;
    private Handler workerHandler;
    private ExecutorService networkExecutor;
    private volatile boolean stopped;
    private volatile long nextRetryDelayMs = 20000L;

    private final Runnable checkRunnable = new Runnable() {
        @Override
        public void run() {
            queueBackgroundCheck("scheduled background service check");
        }
    };

    @Override
    public void onCreate() {
        super.onCreate();
        workerThread = new HandlerThread("six-stars-star-initiative-service");
        workerThread.start();
        workerHandler = new Handler(workerThread.getLooper());
        networkExecutor = Executors.newSingleThreadExecutor();
        stopped = false;
        scheduleNextCheck(6000L);
        broadcastStatus("started", "Prepared Listen cache service started. It will quietly prepare one at-rest line and voice when the runtime is idle.", selectedStarId());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? ACTION_START : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            stopped = true;
            if (workerHandler != null) workerHandler.removeCallbacksAndMessages(null);
            broadcastStatus("stopped", "Star initiative background service stopped.", selectedStarId());
            stopSelf();
            return START_NOT_STICKY;
        }

        stopped = false;
        String reason = intent == null ? "Android background service" : intent.getStringExtra("reason");
        if (ACTION_CHECK_NOW.equals(action)) {
            scheduleCheckAfterQuietMoment(reason == null ? "foreground requested quiet check" : reason);
        } else {
            scheduleNextCheck(6000L);
        }
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onDestroy() {
        stopped = true;
        if (workerHandler != null) workerHandler.removeCallbacksAndMessages(null);
        if (workerThread != null) workerThread.quitSafely();
        if (networkExecutor != null) networkExecutor.shutdownNow();
        super.onDestroy();
    }

    private void scheduleCheckAfterQuietMoment(String reason) {
        if (workerHandler == null) return;
        workerHandler.removeCallbacksAndMessages(null);
        workerHandler.postDelayed(() -> queueBackgroundCheck(reason == null ? "quiet moment" : reason), 4500L);
    }

    private void scheduleNextCheck(long delayMs) {
        if (workerHandler == null || stopped) return;
        workerHandler.removeCallbacksAndMessages(null);
        workerHandler.postDelayed(checkRunnable, Math.max(3500L, delayMs));
    }

    private void scheduleRandomNextCheck() {
        int delayMinutes;
        synchronized (random) {
            delayMinutes = 25 + random.nextInt(16); // Cache exists; verify only every 25-40 minutes.
        }
        scheduleNextCheck(delayMinutes * 60L * 1000L);
    }

    private void queueBackgroundCheck(String reason) {
        if (stopped) return;
        if (!isBackgroundInitiativeEnabled()) {
            scheduleRandomNextCheck();
            return;
        }
        if (!checkInFlight.compareAndSet(false, true)) {
            scheduleRandomNextCheck();
            return;
        }

        final String checkReason = reason == null || reason.length() == 0 ? "Android background initiative service" : reason;
        networkExecutor.execute(() -> {
            boolean cacheReady = false;
            try {
                cacheReady = runBackgroundInitiativeCheck(checkReason);
            } catch (Exception ex) {
                nextRetryDelayMs = 60000L;
                broadcastResponse(false, false, selectedStarId(), "Background prepared Listen request failed: " + ex, checkReason);
            } finally {
                checkInFlight.set(false);
                if (cacheReady) scheduleRandomNextCheck();
                else scheduleNextCheck(Math.max(3500L, nextRetryDelayMs));
            }
        });
    }

    private boolean isBackgroundInitiativeEnabled() {
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        boolean enabled = prefs.getBoolean(PREF_STAR_INITIATIVE_ENABLED, true);
        prefs.edit()
                .putBoolean(PREF_STAR_INITIATIVE_ENABLED, enabled)
                .putBoolean(PREF_STAR_INITIATIVE_BACKGROUND_ENABLED, enabled)
                .apply();
        return enabled;
    }

    private boolean runBackgroundInitiativeCheck(String reason) throws Exception {
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        nextRetryDelayMs = 20000L;
        long now = System.currentTimeMillis();
        String starId = selectedStarId();
        String sessionId = prefs.getBoolean(PREF_PROD_MODE, true) ? "android-phone-prod" : "android-phone-dev";
        long cacheGeneration = prefs.getLong(PREF_PREPARED_LISTEN_GENERATION, 0L);

        long notBeforeMs = prefs.getLong(PREF_PREPARED_LISTEN_NOT_BEFORE_MS, 0L);
        if (now < notBeforeMs) {
            nextRetryDelayMs = Math.min(5L * 60L * 1000L, Math.max(3500L, notBeforeMs - now + 250L));
            broadcastStatus("waitingForIdle", starId + " will prepare her next Listen response after the current request or speech is fully idle.", starId);
            return false;
        }

        if (PrefetchedInitiativeStore.hasFresh(this, sessionId, starId)) {
            broadcastStatus("cacheReady", starId + " already has a prepared at-rest voice waiting for Listen.", starId);
            broadcastResponse(true, true, starId, "Prepared Listen cache is ready.", reason);
            return true;
        }

        long last = prefs.getLong(PREF_LAST_BACKGROUND_REQUEST_MS, 0L);
        if (now - last < 12000L) {
            nextRetryDelayMs = Math.max(3500L, 12000L - (now - last) + 250L);
            return false;
        }
        prefs.edit().putLong(PREF_LAST_BACKGROUND_REQUEST_MS, now).apply();

        broadcastStatus("requestStarted", starId + " is quietly preparing her next at-rest line and voice. Reason: " + reason, starId);

        JSONObject json = new JSONObject();
        json.put("commandId", "android-bg-listen-prefetch-" + now);
        json.put("commandType", "MaybeStarInitiative");
        json.put("sourceClientId", "SixStarsMobileAndroidBackgroundService");
        json.put("sessionId", sessionId);
        json.put("starId", starId);
        json.put("requestType", "StarInitiative");
        json.put("playerText", "");
        json.put("area", prefs.getString(PREF_AREA, "VirethornCaves"));
        json.put("quest", "AndroidPreparedListenCache");
        json.put("romanceMode", prefs.getBoolean(PREF_ROMANCE_MODE, true));
        json.put("speakOnDesktop", false);
        json.put("returnAudio", false);
        json.put("returnAudioInline", true);
        json.put("prefetchForListen", true);
        json.put("clientName", "SixStarsMobileAndroid-PreparedListenCache");
        json.put("reason", "prepare one private at-rest line and voice for the next Android Listen tap; " + reason);

        MobileSettingsService networkSettings = new MobileSettingsService(this);
        String primaryBaseUrl = networkSettings.preferredBridgeUrl();
        String body;
        try {
            body = postJson(primaryBaseUrl + "/api/mobile/dispatch", json.toString(), 240000);
        } catch (Exception primaryError) {
            String fallbackBaseUrl = networkSettings.bridgeUrl();
            if (BridgeAddressService.normalizeBaseUrl(primaryBaseUrl).equalsIgnoreCase(BridgeAddressService.normalizeBaseUrl(fallbackBaseUrl))) {
                throw primaryError;
            }
            body = postJson(fallbackBaseUrl + "/api/mobile/dispatch", json.toString(), 240000);
        }
        if (stopped
                || cacheGeneration != prefs.getLong(PREF_PREPARED_LISTEN_GENERATION, 0L)
                || !starId.equalsIgnoreCase(selectedStarId())) {
            broadcastStatus("cacheDiscarded", "Discarded a prepared line because the player spoke, changed Stars, or consumed the previous cache while it was being generated.", starId);
            return false;
        }

        JSONObject response = new JSONObject(body);
        boolean saved = PrefetchedInitiativeStore.save(this, sessionId, starId, response);
        if (saved) {
            int chunks = response.optInt("voiceChunksCached", response.optJSONArray("inlineVoiceChunks") == null ? 0 : response.optJSONArray("inlineVoiceChunks").length());
            int bytes = response.optInt("voiceBytesCached", 0);
            String message = starId + " prepared " + Math.max(1, chunks) + " voice chunk(s)" + (bytes > 0 ? " (" + bytes + " bytes)" : "") + " for the next Listen tap.";
            broadcastStatus("cacheReady", message, starId);
            broadcastResponse(true, true, starId, message, reason);
            return true;
        }

        nextRetryDelayMs = 60000L;
        String message = response.optString("initiativeReason", response.optString("message", starId + " did not prepare an at-rest line yet."));
        broadcastStatus("cacheEmpty", message, starId);
        broadcastResponse(true, false, starId, message, reason);
        return false;
    }

    private String selectedStarId() {
        String star = getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getString(PREF_SELECTED_STAR_ID, "Elusiaa");
        if (star == null || star.trim().length() == 0) return "Elusiaa";
        return star.trim();
    }

    private String baseUrl() {
        return new MobileSettingsService(this).preferredBridgeUrl();
    }

    private void broadcastStatus(String status, String message, String starId) {
        Intent intent = new Intent(ACTION_STATUS);
        intent.setPackage(getPackageName());
        intent.putExtra("status", status == null ? "status" : status);
        intent.putExtra("message", message == null ? "" : message);
        intent.putExtra("starId", starId == null ? selectedStarId() : starId);
        sendBroadcast(intent);
    }

    private void broadcastResponse(boolean ok, boolean cacheReady, String starId, String message, String reason) {
        Intent intent = new Intent(ACTION_RESPONSE);
        intent.setPackage(getPackageName());
        intent.putExtra("ok", ok);
        intent.putExtra("cacheReady", cacheReady);
        intent.putExtra("starId", starId == null ? selectedStarId() : starId);
        intent.putExtra("message", message == null ? "" : message);
        intent.putExtra("reason", reason == null ? "" : reason);
        sendBroadcast(intent);
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
        if (code < 200 || code >= 300) throw new RuntimeException(String.format(Locale.US, "HTTP %d: %s", code, body));
        return body;
    }
}
