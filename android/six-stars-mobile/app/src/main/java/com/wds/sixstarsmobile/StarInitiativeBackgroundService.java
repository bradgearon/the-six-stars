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
    private static final String PREF_PLAY_ON_PHONE = "playOnPhone";
    private static final String PREF_AREA = "area";
    private static final String PREF_QUEST = "quest";
    private static final String PREF_LAST_BACKGROUND_REQUEST_MS = "starInitiativeBackgroundLastRequestMs";
    private static final String DEFAULT_BRIDGE_URL = "http://192.168.1.217:8066";

    private final Random random = new Random();
    private final AtomicBoolean checkInFlight = new AtomicBoolean(false);
    private HandlerThread workerThread;
    private Handler workerHandler;
    private ExecutorService networkExecutor;
    private volatile boolean stopped;

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
        scheduleNextCheck(30000L);
        broadcastStatus("started", "Star initiative background service started. First quiet check will happen after about 30 seconds so foreground replies keep priority.", selectedStarId());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? ACTION_START : intent.getAction();
        if (ACTION_STOP.equals(action)) {
            broadcastStatus("stopped", "Star initiative background service stopped.", selectedStarId());
            stopSelf();
            return START_NOT_STICKY;
        }

        stopped = false;
        String reason = intent == null ? "Android background service" : intent.getStringExtra("reason");
        if (ACTION_CHECK_NOW.equals(action)) {
            scheduleCheckAfterQuietMoment(reason == null ? "foreground requested quiet check" : reason);
        } else {
            scheduleNextCheck(30000L);
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
        int delaySeconds;
        synchronized (random) {
            delaySeconds = 135 + random.nextInt(235); // About 2.25 to 6.15 minutes.
        }
        scheduleNextCheck(delaySeconds * 1000L);
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
            try {
                runBackgroundInitiativeCheck(checkReason);
            } catch (Exception ex) {
                broadcastResponse(false, selectedStarId(), "", "", "Background Star initiative failed: " + ex, checkReason);
            } finally {
                checkInFlight.set(false);
                scheduleRandomNextCheck();
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

    private void runBackgroundInitiativeCheck(String reason) throws Exception {
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        long now = System.currentTimeMillis();
        long last = prefs.getLong(PREF_LAST_BACKGROUND_REQUEST_MS, 0L);
        if (now - last < 120000L) {
            return;
        }
        prefs.edit().putLong(PREF_LAST_BACKGROUND_REQUEST_MS, now).apply();

        String starId = selectedStarId();
        broadcastStatus("requestStarted", starId + " is quietly checking whether she has something to say. Reason: " + reason, starId);

        JSONObject json = new JSONObject();
        json.put("commandId", "android-bg-initiative-" + now);
        json.put("commandType", "MaybeStarInitiative");
        json.put("sourceClientId", "SixStarsMobileAndroidBackgroundService");
        json.put("sessionId", prefs.getBoolean(PREF_PROD_MODE, true) ? "android-phone-prod" : "android-phone-dev");
        json.put("starId", starId);
        json.put("requestType", "StarInitiative");
        json.put("playerText", "");
        json.put("area", prefs.getString(PREF_AREA, "VirethornCaves"));
        json.put("quest", "AndroidBackgroundStarInitiativeService");
        json.put("romanceMode", prefs.getBoolean(PREF_ROMANCE_MODE, true));
        json.put("speakOnDesktop", false);
        json.put("returnAudio", prefs.getBoolean(PREF_PLAY_ON_PHONE, true));
        json.put("clientName", "SixStarsMobileAndroid-BackgroundStarInitiativeService");
        json.put("reason", reason);

        String body = postJson(baseUrl() + "/api/mobile/dispatch", json.toString(), 240000);
        JSONObject response = new JSONObject(body);
        JSONObject event = response.optJSONObject("event");
        boolean ok = response.optBoolean("ok", event != null);
        boolean shouldSpeak = true;
        String message = response.optString("message", ok ? starId + " had an at-rest response." : "No at-rest response.");
        if (event != null) {
            shouldSpeak = event.optBoolean("initiativeShouldSpeak", event.optBoolean("InitiativeShouldSpeak", event.optString("spokenText", event.optString("SpokenText", "")).trim().length() > 0));
            String reasonText = event.optString("initiativeReason", event.optString("InitiativeReason", ""));
            if (!shouldSpeak && reasonText.length() > 0) message = reasonText;
        } else {
            shouldSpeak = response.optBoolean("initiativeShouldSpeak", response.optBoolean("InitiativeShouldSpeak", response.optString("spokenText", response.optString("SpokenText", "")).trim().length() > 0));
            String reasonText = response.optString("initiativeReason", response.optString("InitiativeReason", ""));
            if (!shouldSpeak && reasonText.length() > 0) message = reasonText;
        }
        broadcastResponse(ok && shouldSpeak, starId, shouldSpeak && event != null ? event.toString() : "", body, message, reason);
    }

    private String selectedStarId() {
        String star = getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getString(PREF_SELECTED_STAR_ID, "Elusiaa");
        if (star == null || star.trim().length() == 0) return "Elusiaa";
        return star.trim();
    }

    private String baseUrl() {
        String value = getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getString(PREF_BRIDGE_URL, DEFAULT_BRIDGE_URL);
        if (value == null) value = DEFAULT_BRIDGE_URL;
        value = value.trim();
        if (value.endsWith("/")) value = value.substring(0, value.length() - 1);
        return value.length() == 0 ? DEFAULT_BRIDGE_URL : value;
    }

    private void broadcastStatus(String status, String message, String starId) {
        Intent intent = new Intent(ACTION_STATUS);
        intent.setPackage(getPackageName());
        intent.putExtra("status", status == null ? "status" : status);
        intent.putExtra("message", message == null ? "" : message);
        intent.putExtra("starId", starId == null ? selectedStarId() : starId);
        sendBroadcast(intent);
    }

    private void broadcastResponse(boolean ok, String starId, String eventJson, String body, String message, String reason) {
        Intent intent = new Intent(ACTION_RESPONSE);
        intent.setPackage(getPackageName());
        intent.putExtra("ok", ok);
        intent.putExtra("starId", starId == null ? selectedStarId() : starId);
        intent.putExtra("eventJson", eventJson == null ? "" : eventJson);
        intent.putExtra("body", body == null ? "" : body);
        intent.putExtra("message", message == null ? "" : message);
        intent.putExtra("reason", reason == null ? "" : reason);
        sendBroadcast(intent);
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
        if (code < 200 || code >= 300) throw new RuntimeException(String.format(Locale.US, "HTTP %d: %s", code, body));
        return body;
    }
}
