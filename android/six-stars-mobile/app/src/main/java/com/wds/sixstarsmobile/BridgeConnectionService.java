package com.wds.sixstarsmobile;

import org.json.JSONObject;

import java.util.concurrent.CompletableFuture;

/** Owns the persistent multiplexed control WebSocket and the configured LAN bridge lifecycle. */
final class BridgeConnectionService implements AutoCloseable {
    interface Listener {
        void onConnected(long latestSequence);
        void onDisconnected(String reason);
        void onEvent(JSONObject event);
        void onProtocolDebug(String message);
    }

    private final MobileBridgeWebSocketClient client;
    private final MobileSettingsService settings;
    private final Listener listener;
    private final Object gate = new Object();
    private volatile String activeBaseUrl = "";

    BridgeConnectionService(MobileSettingsService settings, LocalNetworkBindingService localNetwork, Listener listener) {
        this.settings = settings;
        this.listener = listener;
        this.client = new MobileBridgeWebSocketClient(localNetwork, new MobileBridgeWebSocketClient.Listener() {
            @Override public void onConnected(long latestSequence) { listener.onConnected(latestSequence); }
            @Override public void onDisconnected(String reason) { listener.onDisconnected(reason); }
            @Override public void onEvent(JSONObject event) { listener.onEvent(event); }
            @Override public void onProtocolDebug(String message) { listener.onProtocolDebug(message); }
        });
    }

    void start() { connectConfigured(false); }

    void ensureConnected() {
        if (!client.isConnected() && !client.isConnecting()) connectConfigured(false);
    }

    void reconnect(String baseUrl) {
        settings.setBridgeUrl(baseUrl);
        connectConfigured(true);
    }

    void reconnectPreferred() { connectConfigured(true); }

    /**
     * Android can keep Java Socket/WebSocket objects marked connected while Wi-Fi was suspended.
     * On a real resume, replace the idle control socket before the player can send a new voice
     * request. Requests that are already in flight are never interrupted.
     */
    void refreshAfterResume() {
        if (client.pendingCount() > 0) {
            listener.onProtocolDebug("Android resumed while a bridge request is still in flight; keeping the current control socket until it finishes.");
            ensureConnected();
            return;
        }
        String target = configuredBaseUrl();
        synchronized (gate) { activeBaseUrl = target; }
        listener.onProtocolDebug("Refreshing the IPv4 bridge socket after Android resume: " + target + ".");
        client.forceReconnect(target);
    }

    // Kept as compatibility shims for older callers/UI code. IPv6 promotion is disabled.
    boolean promoteToPreferredIpv6() { return false; }
    boolean promoteToPreferredIpv6(boolean force) { return false; }

    private void connectConfigured(boolean force) {
        String target = configuredBaseUrl();
        synchronized (gate) {
            if (!force && target.equals(activeBaseUrl) && (client.isConnected() || client.isConnecting())) return;
            activeBaseUrl = target;
        }
        if (force) client.forceReconnect(target);
        else client.connect(target);
    }

    private String configuredBaseUrl() {
        return BridgeAddressService.normalizeBaseUrl(settings.bridgeUrl());
    }

    CompletableFuture<JSONObject> request(String operation, JSONObject payload, long timeoutMs) {
        ensureConnected();
        return client.request(operation, payload, timeoutMs);
    }

    void subscribeAfter(long sequence) { client.subscribeAfter(sequence); }
    boolean isConnected() { return client.isConnected(); }
    boolean isConnecting() { return client.isConnecting(); }
    boolean isUsingIpv6() { return false; }
    String activeBaseUrl() { return activeBaseUrl.isEmpty() ? configuredBaseUrl() : activeBaseUrl; }
    int pendingCount() { return client.pendingCount(); }

    @Override public void close() { client.close(); }
}
