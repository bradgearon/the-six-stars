package com.wds.sixstarsmobile;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Reads source-canvas lip anchors for Star animation frames.
 *
 * Coordinates are source pixels measured from the top-left of the base animation canvas.
 * The talking-lips GIF is expected to be cropped to the lip artwork itself; its intrinsic
 * width/height are scaled with the Star and placed at the configured x/y anchor.
 */
final class StarLipSyncConfig {
    static final String ASSET_PATH = "star_animations/lip-positions.json";

    static final class LipPosition {
        final int frame;
        final int x;
        final int y;
        final int durationMs;
        final boolean configured;

        LipPosition(int frame, int x, int y, int durationMs, boolean configured) {
            this.frame = frame;
            this.x = x;
            this.y = y;
            this.durationMs = Math.max(10, durationMs);
            this.configured = configured;
        }
    }

    static final class AnimationPositions {
        final int sourceWidth;
        final int sourceHeight;
        final List<LipPosition> frames;
        final int loopDurationMs;
        final boolean hasConfiguredPosition;

        AnimationPositions(int sourceWidth, int sourceHeight, List<LipPosition> frames) {
            this.sourceWidth = Math.max(1, sourceWidth);
            this.sourceHeight = Math.max(1, sourceHeight);
            this.frames = frames == null ? new ArrayList<>() : frames;

            int total = 0;
            boolean configured = false;
            for (LipPosition frame : this.frames) {
                total += Math.max(10, frame.durationMs);
                configured |= frame.configured;
            }
            loopDurationMs = Math.max(1, total);
            hasConfiguredPosition = configured;
        }

        LipPosition positionForElapsed(long elapsedMs) {
            if (frames.isEmpty() || !hasConfiguredPosition) return null;

            long loopTime = Math.floorMod(elapsedMs, (long) loopDurationMs);
            int cursor = 0;
            int selectedIndex = frames.size() - 1;
            for (int i = 0; i < frames.size(); i++) {
                cursor += Math.max(10, frames.get(i).durationMs);
                if (loopTime < cursor) {
                    selectedIndex = i;
                    break;
                }
            }

            LipPosition exact = frames.get(selectedIndex);
            if (exact.configured) return exact;

            // Partial JSON is useful while the artist is entering anchors. Hold the most
            // recent configured anchor instead of making the mouth vanish on unfinished frames.
            for (int offset = 1; offset < frames.size(); offset++) {
                int previous = Math.floorMod(selectedIndex - offset, frames.size());
                LipPosition candidate = frames.get(previous);
                if (candidate.configured) return candidate;
            }
            return null;
        }
    }

    private final Map<String, Map<String, AnimationPositions>> byStar = new HashMap<>();

    static StarLipSyncConfig load(Context context) {
        StarLipSyncConfig config = new StarLipSyncConfig();
        if (context == null) return config;

        try (InputStream input = context.getAssets().open(ASSET_PATH);
             BufferedReader reader = new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8))) {
            StringBuilder jsonText = new StringBuilder();
            String line;
            while ((line = reader.readLine()) != null) jsonText.append(line).append('\n');

            JSONObject root = new JSONObject(jsonText.toString());
            JSONObject stars = root.optJSONObject("stars");
            if (stars == null) return config;

            JSONArray starNames = stars.names();
            if (starNames == null) return config;
            for (int i = 0; i < starNames.length(); i++) {
                String starName = starNames.optString(i, "");
                JSONObject star = stars.optJSONObject(starName);
                if (star == null) continue;
                JSONObject animations = star.optJSONObject("animations");
                if (animations == null) continue;

                JSONArray animationNames = animations.names();
                if (animationNames == null) continue;
                for (int a = 0; a < animationNames.length(); a++) {
                    String animationName = animationNames.optString(a, "");
                    JSONObject animation = animations.optJSONObject(animationName);
                    if (animation == null) continue;

                    int sourceWidth = animation.optInt("sourceWidth", 128);
                    int sourceHeight = animation.optInt("sourceHeight", 128);
                    JSONArray frameArray = animation.optJSONArray("frames");
                    if (frameArray == null || frameArray.length() == 0) continue;

                    ArrayList<LipPosition> framePositions = new ArrayList<>();
                    for (int f = 0; f < frameArray.length(); f++) {
                        JSONObject frame = frameArray.optJSONObject(f);
                        if (frame == null) continue;
                        boolean configured = frame.has("x") && !frame.isNull("x")
                                && frame.has("y") && !frame.isNull("y");
                        framePositions.add(new LipPosition(
                                frame.optInt("frame", f),
                                configured ? frame.optInt("x", 0) : 0,
                                configured ? frame.optInt("y", 0) : 0,
                                frame.optInt("durationMs", 100),
                                configured));
                    }

                    config.byStar
                            .computeIfAbsent(normalize(starName), ignored -> new HashMap<>())
                            .put(normalize(animationName), new AnimationPositions(sourceWidth, sourceHeight, framePositions));
                }
            }
        } catch (Exception ignored) {
            // Lip compositing is optional. A missing/malformed config leaves the legacy
            // full-body talking GIF behavior intact rather than blocking the companion UI.
        }
        return config;
    }

    AnimationPositions animation(String starId, String animationState) {
        Map<String, AnimationPositions> animations = byStar.get(normalize(starId));
        if (animations == null) return null;
        return animations.get(normalize(animationState));
    }

    private static String normalize(String value) {
        if (value == null) return "";
        return value.trim().toLowerCase(Locale.US).replaceAll("[^a-z0-9_]+", "_");
    }
}
