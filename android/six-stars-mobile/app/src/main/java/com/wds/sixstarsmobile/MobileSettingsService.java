package com.wds.sixstarsmobile;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;

import java.util.ArrayList;
import java.util.List;

/** Centralizes all durable Android companion preferences and defaults. */
final class MobileSettingsService {
    static final String PREFS_NAME = "SixStarsMobilePrefs";
    static final String PREF_PROD_MODE = "prodMode";
    static final String PREF_STAR_INITIATIVE_ENABLED = "starInitiativeEnabled";
    static final String PREF_STAR_INITIATIVE_BACKGROUND_ENABLED = "starInitiativeBackgroundServiceEnabled";
    static final String PREF_BRIDGE_URL = "bridgeUrl";
    static final String PREF_PREFERRED_IPV6_ADDRESS = "preferredIpv6Address";
    static final String PREF_USE_IPV6_PRIMARY = "useIpv6Primary";
    static final String PREF_IPV6_DISCOVERED_AT_MS = "ipv6DiscoveredAtMs";
    static final String PREF_SELECTED_STAR_ID = "selectedStarId";
    static final String PREF_ROMANCE_MODE = "romanceMode";
    static final String PREF_PLAY_ON_PHONE = "playOnPhone";
    static final String PREF_PREPARED_LISTEN_GENERATION = "preparedListenGeneration";
    static final String PREF_LAST_BACKGROUND_REQUEST_MS = "starInitiativeBackgroundLastRequestMs";
    static final String PREF_PREPARED_LISTEN_NOT_BEFORE_MS = "preparedListenNotBeforeMs";
    static final String PREF_AREA = "area";
    static final String PREF_QUEST = "quest";
    static final String PREF_PROD_CONVERSATION_PREFIX = "prodConversation.";
    static final String DEFAULT_BRIDGE_URL = "http://192.168.1.179:8066";
    static final String LEGACY_BRIDGE_URL = "http://192.168.1.217:8066";

    private final SharedPreferences prefs;

    MobileSettingsService(Context context) {
        prefs = context.getApplicationContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
        // IPv6 bridge promotion caused route churn after Android sleep/resume and could replace a
        // healthy IPv4 control/audio socket while a foreground request was starting. The mobile
        // companion now intentionally uses the configured IPv4/hostname bridge only. Clear any
        // previously learned IPv6 route so an older preference can never silently reactivate it.
        if (prefs.getBoolean(PREF_USE_IPV6_PRIMARY, false)
                || prefs.contains(PREF_PREFERRED_IPV6_ADDRESS)
                || prefs.getLong(PREF_IPV6_DISCOVERED_AT_MS, 0L) != 0L) {
            prefs.edit()
                    .putBoolean(PREF_USE_IPV6_PRIMARY, false)
                    .remove(PREF_PREFERRED_IPV6_ADDRESS)
                    .putLong(PREF_IPV6_DISCOVERED_AT_MS, 0L)
                    .apply();
        }
    }

    SharedPreferences raw() { return prefs; }
    boolean isProductionMode() { return prefs.getBoolean(PREF_PROD_MODE, true); }
    void setProductionMode(boolean value) { prefs.edit().putBoolean(PREF_PROD_MODE, value).apply(); }
    boolean isStarInitiativeEnabled() { return prefs.getBoolean(PREF_STAR_INITIATIVE_ENABLED, true); }
    void setStarInitiativeEnabled(boolean value) {
        prefs.edit()
                .putBoolean(PREF_STAR_INITIATIVE_ENABLED, value)
                .putBoolean(PREF_STAR_INITIATIVE_BACKGROUND_ENABLED, value)
                .apply();
    }
    String bridgeUrl() {
        String value = prefs.getString(PREF_BRIDGE_URL, DEFAULT_BRIDGE_URL);
        if (value == null || value.trim().isEmpty() || LEGACY_BRIDGE_URL.equalsIgnoreCase(value.trim())
                || BridgeAddressService.isIpv6Url(value.trim())) {
            value = DEFAULT_BRIDGE_URL;
            prefs.edit().putString(PREF_BRIDGE_URL, value).apply();
        }
        return value.trim();
    }
    void setBridgeUrl(String value) {
        String normalized = value == null ? "" : value.trim();
        if (normalized.isEmpty() || BridgeAddressService.isIpv6Url(normalized)) normalized = DEFAULT_BRIDGE_URL;
        prefs.edit().putString(PREF_BRIDGE_URL, normalized).apply();
    }
    String preferredIpv6Address() { return ""; }
    boolean setPreferredIpv6Address(String value) {
        boolean hadValue = prefs.contains(PREF_PREFERRED_IPV6_ADDRESS)
                || prefs.getBoolean(PREF_USE_IPV6_PRIMARY, false)
                || prefs.getLong(PREF_IPV6_DISCOVERED_AT_MS, 0L) != 0L;
        prefs.edit()
                .putBoolean(PREF_USE_IPV6_PRIMARY, false)
                .remove(PREF_PREFERRED_IPV6_ADDRESS)
                .putLong(PREF_IPV6_DISCOVERED_AT_MS, 0L)
                .apply();
        return hadValue;
    }
    boolean rememberDiscoveredIpv6(String value) { return false; }
    boolean useIpv6Primary() { return false; }
    void setUseIpv6Primary(boolean value) { prefs.edit().putBoolean(PREF_USE_IPV6_PRIMARY, false).apply(); }
    long ipv6DiscoveredAtMs() { return 0L; }
    String preferredBridgeUrl() { return bridgeUrl(); }
    String selectedStarId() { return nonEmpty(prefs.getString(PREF_SELECTED_STAR_ID, "Elusiaa"), "Elusiaa"); }
    void setSelectedStarId(String value) { prefs.edit().putString(PREF_SELECTED_STAR_ID, nonEmpty(value, "Elusiaa")).apply(); }
    boolean romanceMode() { return prefs.getBoolean(PREF_ROMANCE_MODE, true); }
    void setRomanceMode(boolean value) { prefs.edit().putBoolean(PREF_ROMANCE_MODE, value).apply(); }
    boolean playOnPhone() { return prefs.getBoolean(PREF_PLAY_ON_PHONE, true); }
    void setPlayOnPhone(boolean value) { prefs.edit().putBoolean(PREF_PLAY_ON_PHONE, value).apply(); }
    String area() { return nonEmpty(prefs.getString(PREF_AREA, "VirethornCaves"), "VirethornCaves"); }
    void setArea(String value) { prefs.edit().putString(PREF_AREA, nonEmpty(value, "VirethornCaves")).apply(); }
    String quest() { return nonEmpty(prefs.getString(PREF_QUEST, "AndroidMobileBridge"), "AndroidMobileBridge"); }
    void setQuest(String value) { prefs.edit().putString(PREF_QUEST, nonEmpty(value, "AndroidMobileBridge")).apply(); }
    long preparedListenGeneration() { return prefs.getLong(PREF_PREPARED_LISTEN_GENERATION, 0L); }
    void setPreparedListenGeneration(long value) { prefs.edit().putLong(PREF_PREPARED_LISTEN_GENERATION, Math.max(0L, value)).apply(); }
    long lastBackgroundRequestMs() { return prefs.getLong(PREF_LAST_BACKGROUND_REQUEST_MS, 0L); }
    void setLastBackgroundRequestMs(long value) { prefs.edit().putLong(PREF_LAST_BACKGROUND_REQUEST_MS, Math.max(0L, value)).apply(); }
    long preparedListenNotBeforeMs() { return prefs.getLong(PREF_PREPARED_LISTEN_NOT_BEFORE_MS, 0L); }
    void setPreparedListenNotBeforeMs(long value) { prefs.edit().putLong(PREF_PREPARED_LISTEN_NOT_BEFORE_MS, Math.max(0L, value)).apply(); }


    List<String> prodConversationLines(String starId) {
        String key = conversationKey(starId);
        String raw = prefs.getString(key, "");
        ArrayList<String> lines = new ArrayList<>();
        if (raw == null || raw.trim().isEmpty()) return lines;
        try {
            JSONArray array = new JSONArray(raw);
            for (int i = 0; i < array.length() && lines.size() < 12; i++) {
                String line = array.optString(i, "").trim();
                if (!line.isEmpty()) lines.add(line);
            }
        } catch (Exception ignored) {
            // A malformed old cache should never block app startup. Replace it on the next save.
        }
        return lines;
    }

    void setProdConversationLines(String starId, List<String> lines) {
        JSONArray array = new JSONArray();
        if (lines != null) {
            for (String line : lines) {
                if (line == null || line.trim().isEmpty()) continue;
                array.put(line.trim());
                if (array.length() >= 12) break;
            }
        }
        prefs.edit().putString(conversationKey(starId), array.toString()).apply();
    }

    void clearProdConversationLines(String starId) {
        prefs.edit().remove(conversationKey(starId)).apply();
    }

    void clearAllProdConversationLines() {
        SharedPreferences.Editor editor = prefs.edit();
        for (String key : prefs.getAll().keySet()) {
            if (key != null && key.startsWith(PREF_PROD_CONVERSATION_PREFIX)) editor.remove(key);
        }
        editor.apply();
    }

    private static String conversationKey(String starId) {
        return PREF_PROD_CONVERSATION_PREFIX + nonEmpty(starId, "Elusiaa").toLowerCase(java.util.Locale.US);
    }

    private static String nonEmpty(String value, String fallback) {
        return value == null || value.trim().isEmpty() ? fallback : value.trim();
    }
}
