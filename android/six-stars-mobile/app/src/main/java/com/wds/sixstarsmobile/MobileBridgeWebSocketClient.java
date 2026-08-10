package com.wds.sixstarsmobile;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

import javax.net.SocketFactory;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;

/**
 * One persistent, multiplexed pipe between Android and the desktop bridge. Each request receives
 * a small integer id and a CompletableFuture. Requests are placed on the wire in enqueue order;
 * responses may arrive out of order and complete the matching future from the id-indexed map.
 */
final class MobileBridgeWebSocketClient implements AutoCloseable {
    interface Listener {
        void onConnected(long latestSequence);
        void onDisconnected(String reason);
        void onEvent(JSONObject event);
        void onProtocolDebug(String message);
    }

    private static final long RECONNECT_MIN_MS = 500L;
    private static final long RECONNECT_MAX_MS = 10000L;

    private final Listener listener;
    private final LocalNetworkBindingService localNetwork;
    private final OkHttpClient httpClient;
    private final ScheduledExecutorService scheduler = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread thread = new Thread(r, "six-stars-mobile-bridge-scheduler");
        thread.setDaemon(true);
        return thread;
    });
    private final AtomicInteger nextRequestId = new AtomicInteger(1);
    private final AtomicInteger connectionGeneration = new AtomicInteger();
    private final ConcurrentHashMap<Integer, PendingRequest> pendingById = new ConcurrentHashMap<>();
    private final ConcurrentLinkedQueue<Integer> requestOrder = new ConcurrentLinkedQueue<>();
    private final AtomicBoolean reconnectScheduled = new AtomicBoolean(false);
    private final Object socketGate = new Object();
    private final Object sendGate = new Object();

    private volatile WebSocket webSocket;
    private volatile String websocketUrl = "";
    private volatile boolean connected;
    private volatile boolean connecting;
    private volatile boolean closed;
    private volatile long reconnectDelayMs = RECONNECT_MIN_MS;

    MobileBridgeWebSocketClient(LocalNetworkBindingService localNetwork, Listener listener) {
        this.localNetwork = localNetwork;
        this.listener = listener;
        this.httpClient = new OkHttpClient.Builder()
                .pingInterval(20, TimeUnit.SECONDS)
                .connectTimeout(6, TimeUnit.SECONDS)
                .build();
    }

    boolean isConnected() {
        return connected && webSocket != null;
    }

    boolean isConnecting() {
        return connecting;
    }

    int pendingCount() {
        return pendingById.size();
    }

    void connect(String baseUrl) {
        String target = toWebSocketUrl(baseUrl);
        WebSocket supersededSocket = null;
        boolean changedTarget = false;
        int supersededGeneration = 0;
        int generation;
        synchronized (socketGate) {
            if (closed) return;
            if (target.equals(websocketUrl) && (connected || connecting)) return;
            if (!target.equals(websocketUrl)) {
                changedTarget = websocketUrl.length() > 0;
                supersededGeneration = connectionGeneration.get();
                supersededSocket = webSocket;
                webSocket = null;
                connected = false;
                connecting = false;
                websocketUrl = target;
            }
            if (connecting || connected) return;
            connecting = true;
            generation = connectionGeneration.incrementAndGet();
        }

        if (supersededSocket != null) supersededSocket.cancel();
        if (changedTarget) {
            failSentRequests(supersededGeneration, new IllegalStateException("The mobile bridge address changed while a request was in flight."));
        }

        Request request = new Request.Builder().url(target).build();
        OkHttpClient connectionClient = httpClient;
        try {
            String host = request.url().host();
            SocketFactory boundFactory = localNetwork == null ? null : localNetwork.socketFactoryForHost(host);
            if (boundFactory != null) {
                connectionClient = httpClient.newBuilder().socketFactory(boundFactory).build();
                listener.onProtocolDebug("Binding bridge socket for " + host + " to " + localNetwork.routeDescription(host) + ".");
            } else if (localNetwork != null) {
                listener.onProtocolDebug("No Wi-Fi LAN route was available for " + host + "; Android will use its default network.");
            }
        } catch (Exception routeError) {
            listener.onProtocolDebug("Could not bind bridge socket to Wi-Fi: " + routeError.getMessage());
        }
        connectionClient.newWebSocket(request, new SocketListener(target, generation));
    }

    CompletableFuture<JSONObject> request(String operation, JSONObject payload, long timeoutMs) {
        if (closed) {
            CompletableFuture<JSONObject> failed = new CompletableFuture<>();
            failed.completeExceptionally(new IllegalStateException("Mobile bridge WebSocket is closed."));
            return failed;
        }

        int requestId = allocateRequestId();
        JSONObject envelope = new JSONObject();
        try {
            envelope.put("type", "request");
            envelope.put("requestId", requestId);
            envelope.put("operation", operation == null ? "" : operation);
            envelope.put("payload", payload == null ? new JSONObject() : payload);
        } catch (Exception ex) {
            CompletableFuture<JSONObject> failed = new CompletableFuture<>();
            failed.completeExceptionally(ex);
            return failed;
        }

        PendingRequest pending = new PendingRequest(requestId, envelope.toString());
        pendingById.put(requestId, pending);
        requestOrder.add(requestId);
        long boundedTimeout = Math.max(1000L, timeoutMs);
        scheduler.schedule(() -> timeoutRequest(requestId, boundedTimeout), boundedTimeout, TimeUnit.MILLISECONDS);

        WebSocket socket = webSocket;
        if (connected && socket != null) {
            drainQueuedRequests(socket, connectionGeneration.get());
        } else {
            connectFromRememberedUrl();
        }
        return pending.future;
    }

    void subscribeAfter(long sequence) {
        WebSocket socket = webSocket;
        if (!connected || socket == null) return;
        JSONObject message = new JSONObject();
        try {
            message.put("type", "subscribe");
            message.put("afterSequence", Math.max(0L, sequence));
            synchronized (sendGate) {
                if (connected && webSocket == socket) socket.send(message.toString());
            }
        } catch (Exception ex) {
            listener.onProtocolDebug("Could not subscribe to missed bridge events: " + ex);
        }
    }

    private void connectFromRememberedUrl() {
        String target = websocketUrl;
        if (target.length() == 0) return;
        String base = target;
        if (base.startsWith("ws://")) base = "http://" + base.substring(5);
        else if (base.startsWith("wss://")) base = "https://" + base.substring(6);
        int endpoint = base.indexOf("/api/mobile/socket");
        if (endpoint >= 0) base = base.substring(0, endpoint);
        connect(base);
    }

    private int allocateRequestId() {
        while (true) {
            int current = nextRequestId.getAndUpdate(value -> value >= Integer.MAX_VALUE - 1 ? 1 : value + 1);
            if (current > 0 && !pendingById.containsKey(current)) return current;
        }
    }

    /**
     * Serializes calls to WebSocket.send() so requests cannot jump ahead of older queued requests
     * when the socket opens at the same moment another UI action is enqueued.
     */
    private void drainQueuedRequests(WebSocket socket, int generation) {
        synchronized (sendGate) {
            if (!connected || webSocket != socket || generation != connectionGeneration.get()) return;
            for (Integer requestId : new ArrayList<>(requestOrder)) {
                PendingRequest pending = pendingById.get(requestId);
                if (pending == null || !pending.markSending(generation)) continue;
                boolean accepted = socket.send(pending.envelope);
                if (!accepted) {
                    pending.resetUnsent();
                    failRequest(pending.requestId, new IllegalStateException("Desktop WebSocket send queue rejected the request."));
                    return;
                }
            }
        }
    }

    private void timeoutRequest(int requestId, long timeoutMs) {
        PendingRequest pending = pendingById.remove(requestId);
        if (pending == null) return;
        requestOrder.remove(requestId);
        pending.future.completeExceptionally(new TimeoutException("Mobile bridge request " + requestId + " timed out after " + timeoutMs + " ms."));
    }

    private void completeResponse(JSONObject message) {
        int requestId = message.optInt("requestId", 0);
        if (requestId <= 0) return;
        PendingRequest pending = pendingById.remove(requestId);
        if (pending == null) {
            listener.onProtocolDebug("Received a response for unknown or expired request #" + requestId + ".");
            return;
        }
        requestOrder.remove(requestId);
        boolean ok = message.optBoolean("ok", false);
        if (!ok) {
            pending.future.completeExceptionally(new RuntimeException(message.optString("error", "Desktop bridge request failed.")));
            return;
        }
        JSONObject payload = message.optJSONObject("payload");
        if (payload == null) payload = new JSONObject();
        try {
            payload.put("clientQueueMs", pending.queueElapsedMs());
            payload.put("clientSocketRoundTripMs", pending.roundTripElapsedMs());
            payload.put("clientAfterSendMs", pending.afterSendElapsedMs());
        } catch (Exception ignored) {
        }
        pending.future.complete(payload);
    }

    private void failSentRequests(int generation, Throwable error) {
        for (Map.Entry<Integer, PendingRequest> entry : pendingById.entrySet()) {
            PendingRequest candidate = entry.getValue();
            if (candidate == null || !candidate.wasSentOn(generation)) continue;
            PendingRequest removed = pendingById.remove(entry.getKey());
            if (removed != null) {
                requestOrder.remove(entry.getKey());
                removed.future.completeExceptionally(error);
            }
        }
    }

    private void failAllPending(Throwable error) {
        for (Map.Entry<Integer, PendingRequest> entry : pendingById.entrySet()) {
            PendingRequest removed = pendingById.remove(entry.getKey());
            if (removed != null) removed.future.completeExceptionally(error);
        }
        requestOrder.clear();
    }

    private void failRequest(int requestId, Throwable error) {
        PendingRequest pending = pendingById.remove(requestId);
        requestOrder.remove(requestId);
        if (pending != null) pending.future.completeExceptionally(error);
    }

    private void scheduleReconnect(String reason) {
        if (closed || websocketUrl.length() == 0 || !reconnectScheduled.compareAndSet(false, true)) return;
        long delay = reconnectDelayMs;
        reconnectDelayMs = Math.min(RECONNECT_MAX_MS, reconnectDelayMs * 2L);
        listener.onDisconnected(reason + " Reconnecting in " + delay + " ms.");
        scheduler.schedule(() -> {
            reconnectScheduled.set(false);
            connectFromRememberedUrl();
        }, delay, TimeUnit.MILLISECONDS);
    }

    private String toWebSocketUrl(String baseUrl) {
        String value = baseUrl == null ? "" : baseUrl.trim();
        while (value.endsWith("/")) value = value.substring(0, value.length() - 1);
        if (value.startsWith("https://")) value = "wss://" + value.substring(8);
        else if (value.startsWith("http://")) value = "ws://" + value.substring(7);
        else if (!value.startsWith("ws://") && !value.startsWith("wss://")) value = "ws://" + value;
        return value + "/api/mobile/socket";
    }

    @Override
    public void close() {
        closed = true;
        connected = false;
        connecting = false;
        connectionGeneration.incrementAndGet();
        WebSocket socket = webSocket;
        webSocket = null;
        if (socket != null) socket.close(1000, "Android activity closed");
        failAllPending(new IllegalStateException("Mobile bridge WebSocket closed."));
        scheduler.shutdownNow();
        httpClient.dispatcher().executorService().shutdown();
        httpClient.connectionPool().evictAll();
    }

    private final class SocketListener extends WebSocketListener {
        private final String target;
        private final int generation;

        private SocketListener(String target, int generation) {
            this.target = target;
            this.generation = generation;
        }

        @Override
        public void onOpen(WebSocket socket, Response response) {
            synchronized (socketGate) {
                if (closed || generation != connectionGeneration.get() || !target.equals(websocketUrl)) {
                    socket.close(1000, "Superseded connection");
                    return;
                }
                webSocket = socket;
                connected = true;
                connecting = false;
                reconnectDelayMs = RECONNECT_MIN_MS;
            }
            drainQueuedRequests(socket, generation);
        }

        @Override
        public void onMessage(WebSocket socket, String text) {
            try {
                JSONObject message = new JSONObject(text);
                String type = message.optString("type", "");
                if ("hello".equalsIgnoreCase(type)) {
                    listener.onConnected(message.optLong("latestSequence", 0L));
                    return;
                }
                if ("response".equalsIgnoreCase(type)) {
                    completeResponse(message);
                    return;
                }
                if ("event".equalsIgnoreCase(type)) {
                    JSONObject event = message.optJSONObject("payload");
                    if (event != null) listener.onEvent(event);
                    return;
                }
                if (!"pong".equalsIgnoreCase(type) && !"subscribed".equalsIgnoreCase(type)) {
                    listener.onProtocolDebug("Unknown mobile bridge socket message: " + text);
                }
            } catch (Exception ex) {
                listener.onProtocolDebug("Could not parse mobile bridge socket message: " + ex);
            }
        }

        @Override
        public void onClosing(WebSocket socket, int code, String reason) {
            socket.close(code, reason);
        }

        @Override
        public void onClosed(WebSocket socket, int code, String reason) {
            handleDisconnect(socket, "Desktop WebSocket closed (" + code + "): " + reason, null);
        }

        @Override
        public void onFailure(WebSocket socket, Throwable throwable, Response response) {
            handleDisconnect(socket, "Desktop WebSocket failed: " + throwable, throwable);
        }

        private void handleDisconnect(WebSocket socket, String reason, Throwable throwable) {
            synchronized (socketGate) {
                if (generation != connectionGeneration.get() || !target.equals(websocketUrl)) return;
                if (webSocket == socket) webSocket = null;
                connected = false;
                connecting = false;
            }
            // Requests already accepted by the old socket may still finish on the desktop and arrive
            // later as bridge events. Fail those promises instead of blindly resending and creating a
            // duplicate LLM response. Requests that never reached WebSocket.send() remain queued for
            // the reconnect.
            failSentRequests(generation, throwable == null ? new IllegalStateException(reason) : throwable);
            scheduleReconnect(reason);
        }
    }

    private static final class PendingRequest {
        private final int requestId;
        private final String envelope;
        private final CompletableFuture<JSONObject> future = new CompletableFuture<>();
        private final AtomicBoolean sent = new AtomicBoolean(false);
        private final long createdAtNanos = System.nanoTime();
        private volatile long sentAtNanos;
        private volatile int sentGeneration;

        private PendingRequest(int requestId, String envelope) {
            this.requestId = requestId;
            this.envelope = envelope;
        }

        private boolean markSending(int generation) {
            if (!sent.compareAndSet(false, true)) return false;
            sentGeneration = generation;
            sentAtNanos = System.nanoTime();
            return true;
        }

        private void resetUnsent() {
            sentAtNanos = 0L;
            sentGeneration = 0;
            sent.set(false);
        }

        private boolean wasSentOn(int generation) {
            return sent.get() && sentGeneration == generation;
        }

        private long queueElapsedMs() {
            long sentAt = sentAtNanos;
            return sentAt <= 0L ? 0L : nanosToMillis(sentAt - createdAtNanos);
        }

        private long roundTripElapsedMs() {
            return nanosToMillis(System.nanoTime() - createdAtNanos);
        }

        private long afterSendElapsedMs() {
            long sentAt = sentAtNanos;
            return sentAt <= 0L ? 0L : nanosToMillis(System.nanoTime() - sentAt);
        }

        private static long nanosToMillis(long nanos) {
            return Math.max(0L, TimeUnit.NANOSECONDS.toMillis(nanos));
        }
    }
}
