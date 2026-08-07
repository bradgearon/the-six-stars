package com.wds.sixstarsmobile;

import org.json.JSONObject;

import java.util.concurrent.CompletableFuture;

/** Typed entry point for foreground text, audio, photo, command, and state bridge calls. */
final class ConversationService {
    private final BridgeConnectionService bridge;

    ConversationService(BridgeConnectionService bridge) { this.bridge = bridge; }

    CompletableFuture<JSONObject> sendText(JSONObject request, long timeoutMs) {
        return bridge.request("message", request, timeoutMs);
    }
    CompletableFuture<JSONObject> sendAudio(JSONObject request, long timeoutMs) {
        return bridge.request("audio", request, timeoutMs);
    }
    CompletableFuture<JSONObject> sendPhoto(JSONObject request, long timeoutMs) {
        return bridge.request("dispatch", request, timeoutMs);
    }
    CompletableFuture<JSONObject> sendCommand(JSONObject request, long timeoutMs) {
        return bridge.request("dispatch", request, timeoutMs);
    }
    CompletableFuture<JSONObject> fetchState(JSONObject request, long timeoutMs) {
        return bridge.request("state", request, timeoutMs);
    }
    CompletableFuture<JSONObject> requestInitiative(JSONObject request, long timeoutMs) {
        return bridge.request("initiative", request, timeoutMs);
    }
    CompletableFuture<JSONObject> testAudio(JSONObject request, long timeoutMs) {
        return bridge.request("test-audio", request, timeoutMs);
    }
}
