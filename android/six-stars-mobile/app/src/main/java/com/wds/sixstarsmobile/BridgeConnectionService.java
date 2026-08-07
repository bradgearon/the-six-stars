package com.wds.sixstarsmobile;

import org.json.JSONObject;

import java.util.concurrent.CompletableFuture;

/** Owns the persistent multiplexed control WebSocket and its reconnect lifecycle. */
final class BridgeConnectionService implements AutoCloseable {
    interface Listener {
        void onConnected(long latestSequence);
        void onDisconnected(String reason);
        void onEvent(JSONObject event);
        void onProtocolDebug(String message);
    }

    private final MobileBridgeWebSocketClient client;
    private final MobileSettingsService settings;

    BridgeConnectionService(MobileSettingsService settings, Listener listener) {
        this.settings = settings;
        this.client = new MobileBridgeWebSocketClient(new MobileBridgeWebSocketClient.Listener() {
            @Override public void onConnected(long latestSequence) { listener.onConnected(latestSequence); }
            @Override public void onDisconnected(String reason) { listener.onDisconnected(reason); }
            @Override public void onEvent(JSONObject event) { listener.onEvent(event); }
            @Override public void onProtocolDebug(String message) { listener.onProtocolDebug(message); }
        });
    }

    void start() { client.connect(settings.bridgeUrl()); }
    void ensureConnected() { if (!client.isConnected() && !client.isConnecting()) client.connect(settings.bridgeUrl()); }
    void reconnect(String baseUrl) {
        settings.setBridgeUrl(baseUrl);
        client.connect(settings.bridgeUrl());
    }
    CompletableFuture<JSONObject> request(String operation, JSONObject payload, long timeoutMs) {
        ensureConnected();
        return client.request(operation, payload, timeoutMs);
    }
    void subscribeAfter(long sequence) { client.subscribeAfter(sequence); }
    boolean isConnected() { return client.isConnected(); }
    boolean isConnecting() { return client.isConnecting(); }
    int pendingCount() { return client.pendingCount(); }
    @Override public void close() { client.close(); }
}
