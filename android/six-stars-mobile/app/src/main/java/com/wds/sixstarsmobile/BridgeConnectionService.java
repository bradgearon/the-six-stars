package com.wds.sixstarsmobile;

import android.os.SystemClock;

import org.json.JSONObject;

import java.util.List;
import java.util.concurrent.CompletableFuture;

/** Owns the persistent multiplexed control WebSocket, IPv6-first selection, and fallback lifecycle. */
final class BridgeConnectionService implements AutoCloseable {
    interface Listener {
        void onConnected(long latestSequence);
        void onDisconnected(String reason);
        void onEvent(JSONObject event);
        void onProtocolDebug(String message);
    }

    private static final long IPV6_RETRY_SUPPRESSION_MS = 5L * 60L * 1000L;

    private final MobileBridgeWebSocketClient client;
    private final MobileSettingsService settings;
    private final Listener listener;
    private final Object gate = new Object();
    private volatile String activeBaseUrl = "";
    private volatile boolean activeConnectionWasIpv6;
    private volatile boolean fallbackAttempted;
    private volatile long ipv6SuppressedUntilElapsedMs;

    BridgeConnectionService(MobileSettingsService settings, Listener listener) {
        this.settings = settings;
        this.listener = listener;
        this.client = new MobileBridgeWebSocketClient(new MobileBridgeWebSocketClient.Listener() {
            @Override public void onConnected(long latestSequence) {
                synchronized (gate) {
                    activeConnectionWasIpv6 = BridgeAddressService.isIpv6Url(activeBaseUrl);
                    if (activeConnectionWasIpv6) {
                        fallbackAttempted = false;
                        ipv6SuppressedUntilElapsedMs = 0L;
                    }
                }
                listener.onConnected(latestSequence);
            }

            @Override public void onDisconnected(String reason) {
                boolean switched = tryFallbackFromIpv6(reason);
                listener.onDisconnected(switched
                        ? reason + " Falling back to the saved desktop bridge address."
                        : reason);
            }

            @Override public void onEvent(JSONObject event) { listener.onEvent(event); }
            @Override public void onProtocolDebug(String message) { listener.onProtocolDebug(message); }
        });
    }

    void start() { connectPreferred(false); }

    void ensureConnected() {
        if (!client.isConnected() && !client.isConnecting()) connectPreferred(false);
    }

    void reconnect(String fallbackBaseUrl) {
        settings.setBridgeUrl(fallbackBaseUrl);
        synchronized (gate) {
            fallbackAttempted = false;
        }
        connectPreferred(true);
    }

    void reconnectPreferred() {
        synchronized (gate) {
            fallbackAttempted = false;
            ipv6SuppressedUntilElapsedMs = 0L;
        }
        connectPreferred(true);
    }

    boolean promoteToPreferredIpv6() {
        if (!settings.useIpv6Primary() || settings.preferredIpv6Address().isEmpty()) return false;
        if (SystemClock.elapsedRealtime() < ipv6SuppressedUntilElapsedMs) return false;
        String preferred = settings.preferredBridgeUrl();
        if (!BridgeAddressService.isIpv6Url(preferred)) return false;
        if (BridgeAddressService.isIpv6Url(activeBaseUrl) && (client.isConnected() || client.isConnecting())) return false;
        synchronized (gate) {
            fallbackAttempted = false;
            activeBaseUrl = preferred;
        }
        listener.onProtocolDebug("Promoting the local bridge connection to saved IPv6 " + preferred + ".");
        client.connect(preferred);
        return true;
    }

    private void connectPreferred(boolean force) {
        List<String> candidates = BridgeAddressService.connectionCandidates(settings);
        String target = candidates.isEmpty() ? settings.bridgeUrl() : candidates.get(0);
        if (BridgeAddressService.isIpv6Url(target) && SystemClock.elapsedRealtime() < ipv6SuppressedUntilElapsedMs) {
            target = settings.bridgeUrl();
        }
        synchronized (gate) {
            if (!force && target.equals(activeBaseUrl) && (client.isConnected() || client.isConnecting())) return;
            activeBaseUrl = target;
            activeConnectionWasIpv6 = BridgeAddressService.isIpv6Url(target);
        }
        client.connect(target);
    }

    private boolean tryFallbackFromIpv6(String reason) {
        String fallback = settings.bridgeUrl();
        synchronized (gate) {
            if (!BridgeAddressService.isIpv6Url(activeBaseUrl) || fallbackAttempted
                    || BridgeAddressService.normalizeBaseUrl(fallback).equalsIgnoreCase(BridgeAddressService.normalizeBaseUrl(activeBaseUrl))) {
                return false;
            }
            fallbackAttempted = true;
            ipv6SuppressedUntilElapsedMs = SystemClock.elapsedRealtime() + IPV6_RETRY_SUPPRESSION_MS;
            activeBaseUrl = BridgeAddressService.normalizeBaseUrl(fallback);
            activeConnectionWasIpv6 = false;
        }
        listener.onProtocolDebug("IPv6 bridge connection failed; using fallback " + fallback + ". Reason: " + reason);
        client.connect(fallback);
        return true;
    }

    CompletableFuture<JSONObject> request(String operation, JSONObject payload, long timeoutMs) {
        ensureConnected();
        return client.request(operation, payload, timeoutMs);
    }

    void subscribeAfter(long sequence) { client.subscribeAfter(sequence); }
    boolean isConnected() { return client.isConnected(); }
    boolean isConnecting() { return client.isConnecting(); }
    boolean isUsingIpv6() { return BridgeAddressService.isIpv6Url(activeBaseUrl); }
    String activeBaseUrl() { return activeBaseUrl.isEmpty() ? settings.preferredBridgeUrl() : activeBaseUrl; }
    int pendingCount() { return client.pendingCount(); }
    @Override public void close() { client.close(); }
}
