package com.wds.sixstarsmobile;

import android.content.Context;
import android.net.Uri;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * Durable Android cache for server-prepared Star speech. WebSocket events are only fast hints;
 * this store reconciles against the authoritative HTTP manifest after every reconnect/resume.
 */
final class PreparedSpeechStore {
    private static final String ROOT_NAME = "prepared-star-speech";
    private static final String METADATA_NAME = "metadata.json";

    static final class CachedChunk {
        final int chunkIndex;
        final int totalChunks;
        final String sha256;
        final String speechActivitySegmentsMs;
        final File file;

        CachedChunk(int chunkIndex, int totalChunks, String sha256, String speechActivitySegmentsMs, File file) {
            this.chunkIndex = chunkIndex;
            this.totalChunks = totalChunks;
            this.sha256 = safeText(sha256);
            this.speechActivitySegmentsMs = safeText(speechActivitySegmentsMs);
            this.file = file;
        }
    }

    static final class CachedItem {
        final String intentId;
        final String artifactId;
        final String starId;
        final String priority;
        final String status;
        final String topic;
        final String spokenText;
        final double effectiveScore;
        final boolean completionPending;
        final List<CachedChunk> chunks;

        CachedItem(
                String intentId,
                String artifactId,
                String starId,
                String priority,
                String status,
                String topic,
                String spokenText,
                double effectiveScore,
                boolean completionPending,
                List<CachedChunk> chunks) {
            this.intentId = safeText(intentId);
            this.artifactId = safeText(artifactId);
            this.starId = safeText(starId);
            this.priority = normalizePriority(priority);
            this.status = safeText(status).toLowerCase(Locale.US);
            this.topic = safeText(topic);
            this.spokenText = safeText(spokenText);
            this.effectiveScore = Math.max(0.0, Math.min(100.0, effectiveScore));
            this.completionPending = completionPending;
            this.chunks = chunks == null ? Collections.emptyList() : chunks;
        }

        int priorityRank() {
            if ("critical".equals(priority)) return 4;
            if ("high".equals(priority)) return 3;
            if ("medium".equals(priority)) return 2;
            return 1;
        }

        double estimatedDurationSeconds() {
            double total = 0.0;
            for (CachedChunk chunk : chunks) total += wavDurationSeconds(chunk.file);
            return total;
        }
    }

    static final class SyncResult {
        final List<CachedItem> readyItems;
        final int preparingCount;
        final double highestEffectiveScore;
        final String highestPriority;
        final boolean speakBeforeReplyEnabled;
        final double speakBeforeReplyProbability;
        final double highPrioritySpeakBeforeReplyProbability;
        final int speakBeforeReplyMaximumSeconds;
        final int minimumPageDwellSeconds;
        final int mediumPriorityPageDwellSeconds;
        final double girlfriendMediumSpeakProbability;
        final double highConnectionMediumSpeakProbability;
        final double highConnectionThreshold;
        final int minimumMinutesBetweenAutoSpeech;
        final double highPriorityAutoSpeakThreshold;
        final double mediumPriorityAutoSpeakThreshold;

        SyncResult(List<CachedItem> readyItems, int preparingCount, double highestEffectiveScore, String highestPriority) {
            this(readyItems, preparingCount, highestEffectiveScore, highestPriority,
                    true, 0.30, 0.80, 12, 8, 18, 0.55, 0.30, 70.0, 8, 78.0, 55.0);
        }

        SyncResult(
                List<CachedItem> readyItems,
                int preparingCount,
                double highestEffectiveScore,
                String highestPriority,
                boolean speakBeforeReplyEnabled,
                double speakBeforeReplyProbability,
                double highPrioritySpeakBeforeReplyProbability,
                int speakBeforeReplyMaximumSeconds,
                int minimumPageDwellSeconds,
                int mediumPriorityPageDwellSeconds,
                double girlfriendMediumSpeakProbability,
                double highConnectionMediumSpeakProbability,
                double highConnectionThreshold,
                int minimumMinutesBetweenAutoSpeech,
                double highPriorityAutoSpeakThreshold,
                double mediumPriorityAutoSpeakThreshold) {
            this.readyItems = readyItems == null ? Collections.emptyList() : readyItems;
            this.preparingCount = Math.max(0, preparingCount);
            this.highestEffectiveScore = Math.max(0.0, Math.min(100.0, highestEffectiveScore));
            this.highestPriority = normalizePriority(highestPriority);
            this.speakBeforeReplyEnabled = speakBeforeReplyEnabled;
            this.speakBeforeReplyProbability = clamp01(speakBeforeReplyProbability);
            this.highPrioritySpeakBeforeReplyProbability = clamp01(highPrioritySpeakBeforeReplyProbability);
            this.speakBeforeReplyMaximumSeconds = Math.max(0, speakBeforeReplyMaximumSeconds);
            this.minimumPageDwellSeconds = Math.max(1, minimumPageDwellSeconds);
            this.mediumPriorityPageDwellSeconds = Math.max(this.minimumPageDwellSeconds, mediumPriorityPageDwellSeconds);
            this.girlfriendMediumSpeakProbability = clamp01(girlfriendMediumSpeakProbability);
            this.highConnectionMediumSpeakProbability = clamp01(highConnectionMediumSpeakProbability);
            this.highConnectionThreshold = Math.max(0.0, Math.min(100.0, highConnectionThreshold));
            this.minimumMinutesBetweenAutoSpeech = Math.max(0, minimumMinutesBetweenAutoSpeech);
            this.highPriorityAutoSpeakThreshold = Math.max(0.0, Math.min(100.0, highPriorityAutoSpeakThreshold));
            this.mediumPriorityAutoSpeakThreshold = Math.max(0.0, Math.min(100.0, mediumPriorityAutoSpeakThreshold));
        }

        CachedItem bestReady() {
            return readyItems.isEmpty() ? null : readyItems.get(0);
        }
    }

    private PreparedSpeechStore() { }

    static SyncResult sync(Context context, String baseUrl, String starId) throws Exception {
        LocalNetworkBindingService localNetwork = new LocalNetworkBindingService(context);
        retryPendingCompletions(context, localNetwork, baseUrl);
        String url = trimBase(baseUrl) + "/api/prepared-speech/manifest?starId=" + Uri.encode(safeText(starId));
        JSONObject manifest = new JSONObject(readText(localNetwork, url));
        JSONArray intents = manifest.optJSONArray("intents");
        JSONArray artifacts = manifest.optJSONArray("artifacts");
        Map<String, JSONObject> intentsById = new HashMap<>();
        int preparingCount = 0;
        if (intents != null) {
            for (int i = 0; i < intents.length(); i++) {
                JSONObject intent = intents.optJSONObject(i);
                if (intent == null) continue;
                String id = intent.optString("intentId", "");
                if (id.length() == 0) continue;
                intentsById.put(id.toLowerCase(Locale.US), intent);
                String status = intent.optString("status", "");
                if ("preparing".equalsIgnoreCase(status)) preparingCount++;
            }
        }

        File root = root(context);
        Set<String> activeFolders = new HashSet<>();
        if (artifacts != null) {
            for (int i = 0; i < artifacts.length(); i++) {
                JSONObject artifact = artifacts.optJSONObject(i);
                if (artifact == null) continue;
                String artifactStar = artifact.optString("starId", "");
                if (starId != null && starId.length() > 0 && !artifactStar.equalsIgnoreCase(starId)) continue;
                String intentId = artifact.optString("intentId", "");
                String artifactId = artifact.optString("artifactId", "");
                if (intentId.length() == 0 || artifactId.length() == 0) continue;
                JSONObject intent = intentsById.get(intentId.toLowerCase(Locale.US));
                if (intent == null) continue;
                String status = intent.optString("status", "");
                if (!"ready".equalsIgnoreCase(status)) continue;

                String folderName = safeName(intentId + "-" + artifactId);
                activeFolders.add(folderName);
                File folder = new File(root, folderName);
                if (!folder.exists() && !folder.mkdirs()) throw new IllegalStateException("Could not create prepared speech folder " + folder);

                JSONArray chunks = artifact.optJSONArray("chunks");
                JSONArray localChunks = new JSONArray();
                if (chunks != null) {
                    for (int c = 0; c < chunks.length(); c++) {
                        JSONObject chunk = chunks.optJSONObject(c);
                        if (chunk == null) continue;
                        int chunkIndex = chunk.optInt("chunkIndex", c);
                        int totalChunks = chunk.optInt("totalChunks", chunks.length());
                        int byteLength = chunk.optInt("byteLength", 0);
                        String sha = chunk.optString("sha256", "");
                        String chunkUrl = chunk.optString("url", "");
                        String segments = chunk.optString("speechActivitySegmentsMs", "");
                        File finalFile = new File(folder, String.format(Locale.US, "chunk-%03d.wav", chunkIndex));
                        if (!isValid(finalFile, byteLength, sha)) {
                            byte[] data = readBytes(localNetwork, resolveUrl(baseUrl, chunkUrl));
                            if (byteLength > 0 && data.length != byteLength) {
                                throw new IllegalStateException("Prepared speech byte length mismatch for " + artifactId + " chunk " + chunkIndex);
                            }
                            String actual = sha256(data);
                            if (sha.length() > 0 && !sha.equalsIgnoreCase(actual)) {
                                throw new IllegalStateException("Prepared speech checksum mismatch for " + artifactId + " chunk " + chunkIndex);
                            }
                            File part = new File(finalFile.getAbsolutePath() + ".part");
                            writeBytes(part, data);
                            if (finalFile.exists() && !finalFile.delete()) throw new IllegalStateException("Could not replace " + finalFile);
                            if (!part.renameTo(finalFile)) throw new IllegalStateException("Could not finish prepared speech download " + finalFile);
                        }
                        JSONObject local = new JSONObject();
                        local.put("chunkIndex", chunkIndex);
                        local.put("totalChunks", Math.max(1, totalChunks));
                        local.put("sha256", sha);
                        local.put("speechActivitySegmentsMs", segments);
                        local.put("fileName", finalFile.getName());
                        localChunks.put(local);
                    }
                }

                boolean completionPending = false;
                try {
                    CachedItem existingCached = readItem(folder);
                    completionPending = existingCached != null && existingCached.completionPending;
                } catch (Exception ignored) { }

                JSONObject localMetadata = new JSONObject();
                localMetadata.put("intentId", intentId);
                localMetadata.put("artifactId", artifactId);
                localMetadata.put("starId", artifactStar);
                localMetadata.put("priority", intent.optString("priority", "low"));
                localMetadata.put("status", status);
                localMetadata.put("topic", intent.optString("topic", ""));
                localMetadata.put("effectiveScore", intent.optDouble("effectiveScore", 0.0));
                localMetadata.put("spokenText", artifact.optString("spokenText", ""));
                localMetadata.put("completionPending", completionPending);
                localMetadata.put("chunks", localChunks);
                writeTextAtomically(new File(folder, METADATA_NAME), localMetadata.toString());
            }
        }

        cleanup(root, activeFolders, starId);
        List<CachedItem> ready = loadReady(context, starId);
        JSONObject clientPolicy = manifest.optJSONObject("clientPolicy");
        return new SyncResult(
                ready,
                preparingCount,
                manifest.optDouble("highestEffectiveScore", 0.0),
                manifest.optString("highestPriority", "low"),
                clientPolicy == null || clientPolicy.optBoolean("speakBeforeReplyEnabled", true),
                clientPolicy == null ? 0.30 : clientPolicy.optDouble("speakBeforeReplyProbability", 0.30),
                clientPolicy == null ? 0.80 : clientPolicy.optDouble("highPrioritySpeakBeforeReplyProbability", 0.80),
                clientPolicy == null ? 12 : clientPolicy.optInt("speakBeforeReplyMaximumSeconds", 12),
                clientPolicy == null ? 8 : clientPolicy.optInt("minimumPageDwellSeconds", 8),
                clientPolicy == null ? 18 : clientPolicy.optInt("mediumPriorityPageDwellSeconds", 18),
                clientPolicy == null ? 0.55 : clientPolicy.optDouble("girlfriendMediumSpeakProbability", 0.55),
                clientPolicy == null ? 0.30 : clientPolicy.optDouble("highConnectionMediumSpeakProbability", 0.30),
                clientPolicy == null ? 70.0 : clientPolicy.optDouble("highConnectionThreshold", 70.0),
                clientPolicy == null ? 8 : clientPolicy.optInt("minimumMinutesBetweenAutoSpeech", 8),
                clientPolicy == null ? 78.0 : clientPolicy.optDouble("highPriorityAutoSpeakThreshold", 78.0),
                clientPolicy == null ? 55.0 : clientPolicy.optDouble("mediumPriorityAutoSpeakThreshold", 55.0));
    }

    static List<CachedItem> loadReady(Context context, String starId) {
        File root = root(context);
        File[] folders = root.listFiles(File::isDirectory);
        if (folders == null) return new ArrayList<>();
        List<CachedItem> result = new ArrayList<>();
        for (File folder : folders) {
            try {
                CachedItem item = readItem(folder);
                if (item == null) continue;
                if (starId != null && starId.length() > 0 && !item.starId.equalsIgnoreCase(starId)) continue;
                if (!"ready".equals(item.status)) continue;
                if (item.completionPending || item.chunks.isEmpty()) continue;
                boolean allPresent = true;
                for (CachedChunk chunk : item.chunks) if (!chunk.file.isFile() || chunk.file.length() == 0L) allPresent = false;
                if (allPresent) result.add(item);
            } catch (Exception ignored) { }
        }
        result.sort(Comparator
                .comparingInt(CachedItem::priorityRank).reversed()
                .thenComparing(Comparator.comparingDouble((CachedItem x) -> x.effectiveScore).reversed()));
        return result;
    }

    static void deleteIntent(Context context, String intentId) {
        File[] folders = root(context).listFiles(File::isDirectory);
        if (folders == null) return;
        for (File folder : folders) {
            try {
                CachedItem item = readItem(folder);
                if (item != null && item.intentId.equalsIgnoreCase(intentId)) deleteRecursively(folder);
            } catch (Exception ignored) { }
        }
    }

    static JSONObject postAction(Context context, String baseUrl, String intentId, String action) throws Exception {
        return postAction(new LocalNetworkBindingService(context), baseUrl, intentId, action);
    }

    private static JSONObject postAction(LocalNetworkBindingService localNetwork, String baseUrl, String intentId, String action) throws Exception {
        String url = trimBase(baseUrl) + "/api/prepared-speech/" + Uri.encode(intentId) + "/" + Uri.encode(action);
        HttpURLConnection connection = localNetwork.openHttpConnection(new URL(url));
        connection.setConnectTimeout(10000);
        connection.setReadTimeout(20000);
        connection.setRequestMethod("POST");
        connection.setDoOutput(true);
        connection.setFixedLengthStreamingMode(0);
        connection.connect();
        try {
            int code = connection.getResponseCode();
            InputStream input = code >= 200 && code < 300 ? connection.getInputStream() : connection.getErrorStream();
            String body = input == null ? "" : new String(readAll(input), StandardCharsets.UTF_8);
            if (code < 200 || code >= 300) throw new IllegalStateException("HTTP " + code + ": " + body);
            return body.length() == 0 ? new JSONObject() : new JSONObject(body);
        } finally {
            connection.disconnect();
        }
    }

    static void markCompletionPending(Context context, String intentId, boolean pending) throws Exception {
        File[] folders = root(context).listFiles(File::isDirectory);
        if (folders == null) return;
        for (File folder : folders) {
            File metadata = new File(folder, METADATA_NAME);
            if (!metadata.isFile()) continue;
            JSONObject json;
            try {
                json = new JSONObject(new String(readFile(metadata), StandardCharsets.UTF_8));
            } catch (Exception ignored) {
                continue;
            }
            if (!json.optString("intentId", "").equalsIgnoreCase(safeText(intentId))) continue;
            json.put("completionPending", pending);
            writeTextAtomically(metadata, json.toString());
        }
    }

    private static void retryPendingCompletions(Context context, LocalNetworkBindingService localNetwork, String baseUrl) {
        File[] folders = root(context).listFiles(File::isDirectory);
        if (folders == null) return;
        for (File folder : folders) {
            try {
                CachedItem item = readItem(folder);
                if (item == null || !item.completionPending || item.intentId.length() == 0) continue;
                postAction(localNetwork, baseUrl, item.intentId, "completed");
                deleteRecursively(folder);
            } catch (Exception ignored) {
                // Keep the local marker. The next reconciliation will retry without replaying the audio.
            }
        }
    }

    private static CachedItem readItem(File folder) throws Exception {
        File metadata = new File(folder, METADATA_NAME);
        if (!metadata.isFile()) return null;
        JSONObject json = new JSONObject(new String(readFile(metadata), StandardCharsets.UTF_8));
        JSONArray chunks = json.optJSONArray("chunks");
        List<CachedChunk> cachedChunks = new ArrayList<>();
        if (chunks != null) {
            for (int i = 0; i < chunks.length(); i++) {
                JSONObject chunk = chunks.optJSONObject(i);
                if (chunk == null) continue;
                File file = new File(folder, chunk.optString("fileName", ""));
                cachedChunks.add(new CachedChunk(
                        chunk.optInt("chunkIndex", i),
                        chunk.optInt("totalChunks", chunks.length()),
                        chunk.optString("sha256", ""),
                        chunk.optString("speechActivitySegmentsMs", ""),
                        file));
            }
        }
        cachedChunks.sort(Comparator.comparingInt(x -> x.chunkIndex));
        return new CachedItem(
                json.optString("intentId", ""),
                json.optString("artifactId", ""),
                json.optString("starId", ""),
                json.optString("priority", "low"),
                json.optString("status", "ready"),
                json.optString("topic", ""),
                json.optString("spokenText", ""),
                json.optDouble("effectiveScore", 0.0),
                json.optBoolean("completionPending", false),
                cachedChunks);
    }

    private static boolean isValid(File file, int expectedLength, String expectedSha) {
        try {
            if (!file.isFile() || file.length() <= 0L) return false;
            if (expectedLength > 0 && file.length() != expectedLength) return false;
            if (expectedSha != null && expectedSha.length() > 0) return expectedSha.equalsIgnoreCase(sha256(readFile(file)));
            return true;
        } catch (Exception ex) {
            return false;
        }
    }

    private static void cleanup(File root, Set<String> activeFolders, String synchronizedStarId) {
        File[] folders = root.listFiles(File::isDirectory);
        if (folders == null) return;
        for (File folder : folders) {
            if (activeFolders.contains(folder.getName())) continue;
            try {
                CachedItem item = readItem(folder);
                if (item != null && synchronizedStarId != null && synchronizedStarId.length() > 0
                        && !item.starId.equalsIgnoreCase(synchronizedStarId)) {
                    continue;
                }
            } catch (Exception ignored) { }
            deleteRecursively(folder);
        }
    }

    private static String resolveUrl(String baseUrl, String relative) {
        if (relative == null || relative.length() == 0) return trimBase(baseUrl);
        if (relative.startsWith("http://") || relative.startsWith("https://")) return relative;
        return trimBase(baseUrl) + (relative.startsWith("/") ? relative : "/" + relative);
    }

    private static String readText(LocalNetworkBindingService localNetwork, String url) throws Exception {
        return new String(readBytes(localNetwork, url), StandardCharsets.UTF_8);
    }

    private static byte[] readBytes(LocalNetworkBindingService localNetwork, String url) throws Exception {
        HttpURLConnection connection = localNetwork.openHttpConnection(new URL(url));
        connection.setConnectTimeout(10000);
        connection.setReadTimeout(45000);
        connection.setRequestMethod("GET");
        try {
            int code = connection.getResponseCode();
            InputStream input = code >= 200 && code < 300 ? connection.getInputStream() : connection.getErrorStream();
            byte[] bytes = input == null ? new byte[0] : readAll(input);
            if (code < 200 || code >= 300) throw new IllegalStateException("HTTP " + code + ": " + new String(bytes, StandardCharsets.UTF_8));
            return bytes;
        } finally {
            connection.disconnect();
        }
    }

    private static byte[] readAll(InputStream input) throws Exception {
        try (InputStream source = input; ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = source.read(buffer)) >= 0) output.write(buffer, 0, read);
            return output.toByteArray();
        }
    }

    private static byte[] readFile(File file) throws Exception {
        try (FileInputStream input = new FileInputStream(file)) {
            return readAll(input);
        }
    }

    private static void writeBytes(File file, byte[] bytes) throws Exception {
        try (FileOutputStream output = new FileOutputStream(file, false)) {
            output.write(bytes);
            output.flush();
            output.getFD().sync();
        }
    }

    private static void writeTextAtomically(File file, String text) throws Exception {
        File part = new File(file.getAbsolutePath() + ".part");
        writeBytes(part, safeText(text).getBytes(StandardCharsets.UTF_8));
        if (file.exists() && !file.delete()) throw new IllegalStateException("Could not replace " + file);
        if (!part.renameTo(file)) throw new IllegalStateException("Could not finish " + file);
    }

    private static String sha256(byte[] bytes) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        byte[] hash = digest.digest(bytes);
        StringBuilder out = new StringBuilder(hash.length * 2);
        for (byte value : hash) out.append(String.format(Locale.US, "%02x", value & 0xff));
        return out.toString();
    }

    private static File root(Context context) {
        File root = new File(context.getFilesDir(), ROOT_NAME);
        if (!root.exists()) root.mkdirs();
        return root;
    }

    private static void deleteRecursively(File file) {
        if (file == null || !file.exists()) return;
        if (file.isDirectory()) {
            File[] children = file.listFiles();
            if (children != null) for (File child : children) deleteRecursively(child);
        }
        file.delete();
    }

    private static double wavDurationSeconds(File file) {
        try {
            byte[] bytes = readFile(file);
            if (bytes.length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return 0.0;
            int byteRate = 0;
            int dataLength = 0;
            int offset = 12;
            while (offset + 8 <= bytes.length) {
                String chunkId = new String(bytes, offset, 4, StandardCharsets.US_ASCII);
                int chunkLength = littleEndianInt(bytes, offset + 4);
                int dataOffset = offset + 8;
                if (chunkLength < 0 || dataOffset + chunkLength > bytes.length) break;
                if ("fmt ".equals(chunkId) && chunkLength >= 12) byteRate = littleEndianInt(bytes, dataOffset + 8);
                if ("data".equals(chunkId)) dataLength = chunkLength;
                if (byteRate > 0 && dataLength > 0) return (double)dataLength / (double)byteRate;
                offset = dataOffset + chunkLength + (chunkLength & 1);
            }
        } catch (Exception ignored) { }
        return 0.0;
    }

    private static int littleEndianInt(byte[] bytes, int offset) {
        if (bytes == null || offset < 0 || offset + 4 > bytes.length) return 0;
        return (bytes[offset] & 0xff)
                | ((bytes[offset + 1] & 0xff) << 8)
                | ((bytes[offset + 2] & 0xff) << 16)
                | ((bytes[offset + 3] & 0xff) << 24);
    }

    private static double clamp01(double value) {
        return Math.max(0.0, Math.min(1.0, value));
    }

    private static String safeName(String value) {
        String safe = safeText(value).replaceAll("[^A-Za-z0-9._-]", "_");
        return safe.length() == 0 ? "prepared" : safe;
    }

    private static String safeText(String value) {
        return value == null ? "" : value.trim();
    }

    private static String normalizePriority(String value) {
        String normalized = safeText(value).toLowerCase(Locale.US);
        if ("critical".equals(normalized) || "high".equals(normalized) || "medium".equals(normalized)) return normalized;
        return "low";
    }

    private static String trimBase(String baseUrl) {
        String value = safeText(baseUrl);
        while (value.endsWith("/")) value = value.substring(0, value.length() - 1);
        return value;
    }
}
