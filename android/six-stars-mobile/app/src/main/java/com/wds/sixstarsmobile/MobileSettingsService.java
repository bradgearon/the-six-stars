package com.wds.sixstarsmobile;

import android.content.Context;
import android.content.SharedPreferences;

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
    static final String DEFAULT_BRIDGE_URL = "http://192.168.1.179:8066";
    static final String LEGACY_BRIDGE_URL = "http://192.168.1.217:8066";

    private final SharedPreferences prefs;

    MobileSettingsService(Context context) {
        prefs = context.getApplicationContext().getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
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
        if (value == null || value.trim().isEmpty() || LEGACY_BRIDGE_URL.equalsIgnoreCase(value.trim())) {
            value = DEFAULT_BRIDGE_URL;
            prefs.edit().putString(PREF_BRIDGE_URL, value).apply();
        }
        return value.trim();
    }
    void setBridgeUrl(String value) {
        String normalized = value == null ? "" : value.trim();
        prefs.edit().putString(PREF_BRIDGE_URL, normalized.isEmpty() ? DEFAULT_BRIDGE_URL : normalized).apply();
    }
    String preferredIpv6Address() {
        return BridgeAddressService.normalizeIpv6Address(prefs.getString(PREF_PREFERRED_IPV6_ADDRESS, ""));
    }
    boolean setPreferredIpv6Address(String value) {
        String normalized = BridgeAddressService.normalizeIpv6Address(value);
        String before = preferredIpv6Address();
        SharedPreferences.Editor editor = prefs.edit().putString(PREF_PREFERRED_IPV6_ADDRESS, normalized);
        if (!normalized.isEmpty()) editor.putLong(PREF_IPV6_DISCOVERED_AT_MS, System.currentTimeMillis());
        else editor.putLong(PREF_IPV6_DISCOVERED_AT_MS, 0L);
        editor.apply();
        return !before.equalsIgnoreCase(normalized);
    }
    boolean rememberDiscoveredIpv6(String value) {
        String normalized = BridgeAddressService.normalizeIpv6Address(value);
        if (normalized.isEmpty()) return false;
        return setPreferredIpv6Address(normalized);
    }
    boolean useIpv6Primary() { return prefs.getBoolean(PREF_USE_IPV6_PRIMARY, true); }
    void setUseIpv6Primary(boolean value) { prefs.edit().putBoolean(PREF_USE_IPV6_PRIMARY, value).apply(); }
    long ipv6DiscoveredAtMs() { return prefs.getLong(PREF_IPV6_DISCOVERED_AT_MS, 0L); }
    String preferredBridgeUrl() {
        if (!useIpv6Primary()) return bridgeUrl();
        String value = BridgeAddressService.ipv6BridgeUrl(preferredIpv6Address(), BridgeAddressService.bridgePort(bridgeUrl()));
        return value.isEmpty() ? bridgeUrl() : value;
    }
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

    private static String nonEmpty(String value, String fallback) {
        return value == null || value.trim().isEmpty() ? fallback : value.trim();
    }
}
