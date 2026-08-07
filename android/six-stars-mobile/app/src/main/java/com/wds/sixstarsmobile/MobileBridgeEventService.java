package com.wds.sixstarsmobile;

import org.json.JSONObject;

import java.util.Locale;

/** Parses the wire event envelope once and classifies it for UI/domain handlers. */
final class MobileBridgeEventService {
    enum Kind { CONVERSATION, TRANSCRIPT, STAR_STATE, GIRLFRIEND, INITIATIVE, TOP_EMOTIONS, INTERSTAR, DAILY_STATE, PHOTO, STORY, UNKNOWN }

    static final class Event {
        final Kind kind;
        final String type;
        final long sequence;
        final String starId;
        final String message;
        final JSONObject payload;

        Event(Kind kind, String type, long sequence, String starId, String message, JSONObject payload) {
            this.kind = kind;
            this.type = type;
            this.sequence = sequence;
            this.starId = starId;
            this.message = message;
            this.payload = payload;
        }
    }

    Event parse(JSONObject payload) {
        JSONObject safe = payload == null ? new JSONObject() : payload;
        String type = first(safe.optString("eventType", ""), safe.optString("EventType", ""));
        long sequence = safe.has("sequence") ? safe.optLong("sequence", 0L) : safe.optLong("Sequence", 0L);
        String starId = first(safe.optString("starId", ""), safe.optString("StarId", ""));
        String message = first(safe.optString("message", ""), safe.optString("Message", ""));
        return new Event(classify(type), type, sequence, starId, message, safe);
    }

    private Kind classify(String type) {
        String value = type == null ? "" : type.toLowerCase(Locale.US);
        if (value.contains("transcript")) return Kind.TRANSCRIPT;
        if (value.contains("conversation") || value.contains("response") || value.contains("requestfailed")) return Kind.CONVERSATION;
        if (value.contains("girlfriend") || value.contains("proposal")) return Kind.GIRLFRIEND;
        if (value.contains("initiative") || value.contains("atrest")) return Kind.INITIATIVE;
        if (value.contains("topaffected") || value.contains("emotionupdate")) return Kind.TOP_EMOTIONS;
        if (value.contains("interstar")) return Kind.INTERSTAR;
        if (value.contains("dailystar") || value.contains("dailystate")) return Kind.DAILY_STATE;
        if (value.contains("photo")) return Kind.PHOTO;
        if (value.contains("story")) return Kind.STORY;
        if (value.contains("starstate") || value.contains("selectedstar") || value.contains("metchanged") || value.contains("allychanged")) return Kind.STAR_STATE;
        return Kind.UNKNOWN;
    }

    private static String first(String a, String b) { return a != null && !a.isEmpty() ? a : b == null ? "" : b; }
}
