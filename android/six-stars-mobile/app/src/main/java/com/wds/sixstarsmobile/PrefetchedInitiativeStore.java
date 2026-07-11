package com.wds.sixstarsmobile;

import android.content.Context;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

/**
 * Disk-backed one-item cache for the selected Star's next at-rest Listen response.
 * The activity mirrors this object in memory while it is alive; the files let the prepared
 * voice survive activity/process recreation without asking the LLM again.
 */
final class PrefetchedInitiativeStore {
    private static final String DIRECTORY_NAME = "prefetched-star-listen";
    private static final String METADATA_FILE = "prepared.json";
    private static final long MAX_AGE_MS = 2L * 60L * 60L * 1000L;

    private PrefetchedInitiativeStore() { }

    static synchronized boolean save(
            Context context,
            String sessionId,
            String starId,
            JSONObject response) throws Exception {
        if (context == null || response == null) return false;
        String spokenText = optStringEither(response, "spokenText", "SpokenText").trim();
        String responseStarId = optStringEither(response, "starId", "StarId").trim();
        boolean shouldSpeak = optBooleanEither(response, "initiativeShouldSpeak", "InitiativeShouldSpeak", spokenText.length() > 0);
        JSONArray chunks = optArrayEither(response, "inlineVoiceChunks", "InlineVoiceChunks");
        if (!optBooleanEither(response, "ok", "Ok", true)
                || (!responseStarId.isEmpty() && !safe(starId).equalsIgnoreCase(responseStarId))
                || !shouldSpeak
                || spokenText.length() == 0
                || chunks == null
                || chunks.length() == 0) {
            return false;
        }

        File directory = directory(context);
        clearDirectory(directory);
        if (!directory.exists() && !directory.mkdirs()) {
            throw new IllegalStateException("Could not create prepared Listen cache directory: " + directory);
        }

        JSONArray compactChunks = new JSONArray();
        int written = 0;
        try {
            for (int i = 0; i < chunks.length(); i++) {
                JSONObject source = chunks.optJSONObject(i);
                if (source == null) continue;
                String base64 = optStringEither(source, "audioBase64", "AudioBase64");
                if (base64.length() == 0) continue;
                byte[] wav = android.util.Base64.decode(base64, android.util.Base64.DEFAULT);
                if (wav.length == 0) continue;

                int chunkIndex = optIntEither(source, "chunkIndex", "ChunkIndex", written);
                String fileName = String.format(java.util.Locale.US, "chunk-%03d.wav", chunkIndex);
                File audioFile = new File(directory, fileName);
                writeBytes(audioFile, wav);

                JSONObject compact = new JSONObject();
                compact.put("chunkIndex", chunkIndex);
                compact.put("totalChunks", optIntEither(source, "totalChunks", "TotalChunks", chunks.length()));
                compact.put("fileName", fileName);
                compact.put("sha256", optStringEither(source, "sha256", "Sha256"));
                compact.put("speechActivitySegmentsMs", optStringEither(source, "speechActivitySegmentsMs", "SpeechActivitySegmentsMs"));
                compact.put("byteLength", wav.length);
                compactChunks.put(compact);
                written++;
            }

            if (written == 0) {
                clearDirectory(directory);
                return false;
            }

            JSONObject responseWithoutAudio = new JSONObject(response.toString());
            responseWithoutAudio.remove("inlineVoiceChunks");
            responseWithoutAudio.remove("InlineVoiceChunks");
            responseWithoutAudio.remove("state");
            responseWithoutAudio.remove("State");
            responseWithoutAudio.remove("recentConversationHistory");
            responseWithoutAudio.remove("RecentConversationHistory");
            responseWithoutAudio.put("voiceMessage", "Prepared voice loaded from the Android Listen cache.");
            responseWithoutAudio.put("voiceChunksCached", written);

            JSONObject metadata = new JSONObject();
            metadata.put("version", 1);
            metadata.put("createdAtMs", System.currentTimeMillis());
            metadata.put("sessionId", safe(sessionId));
            metadata.put("starId", safe(starId));
            metadata.put("response", responseWithoutAudio);
            metadata.put("chunks", compactChunks);
            writeBytes(new File(directory, METADATA_FILE), metadata.toString().getBytes(StandardCharsets.UTF_8));
            return true;
        } catch (Exception ex) {
            clearDirectory(directory);
            throw ex;
        }
    }

    static synchronized CachedInitiative load(Context context, String sessionId, String starId) {
        try {
            File directory = directory(context);
            File metadataFile = new File(directory, METADATA_FILE);
            if (!metadataFile.isFile()) return null;
            JSONObject metadata = new JSONObject(new String(readBytes(metadataFile), StandardCharsets.UTF_8));
            long createdAtMs = metadata.optLong("createdAtMs", 0L);
            if (createdAtMs <= 0L || System.currentTimeMillis() - createdAtMs > MAX_AGE_MS) {
                clearDirectory(directory);
                return null;
            }
            if (!safe(sessionId).equalsIgnoreCase(metadata.optString("sessionId", ""))
                    || !safe(starId).equalsIgnoreCase(metadata.optString("starId", ""))) {
                return null;
            }

            JSONObject response = metadata.optJSONObject("response");
            JSONArray chunksJson = metadata.optJSONArray("chunks");
            if (response == null || chunksJson == null || chunksJson.length() == 0) {
                clearDirectory(directory);
                return null;
            }

            ArrayList<CachedVoiceChunk> chunks = new ArrayList<>();
            for (int i = 0; i < chunksJson.length(); i++) {
                JSONObject chunk = chunksJson.optJSONObject(i);
                if (chunk == null) continue;
                File audioFile = new File(directory, chunk.optString("fileName", ""));
                if (!audioFile.isFile()) {
                    clearDirectory(directory);
                    return null;
                }
                byte[] wav = readBytes(audioFile);
                if (wav.length == 0) {
                    clearDirectory(directory);
                    return null;
                }
                chunks.add(new CachedVoiceChunk(
                        chunk.optInt("chunkIndex", i),
                        chunk.optInt("totalChunks", chunksJson.length()),
                        wav,
                        chunk.optString("sha256", ""),
                        chunk.optString("speechActivitySegmentsMs", "")));
            }
            chunks.sort(Comparator.comparingInt(x -> x.chunkIndex));
            if (chunks.isEmpty()) return null;
            return new CachedInitiative(
                    metadata.optString("sessionId", ""),
                    metadata.optString("starId", ""),
                    createdAtMs,
                    response,
                    chunks);
        } catch (Exception ignored) {
            clear(context);
            return null;
        }
    }

    static synchronized boolean hasFresh(Context context, String sessionId, String starId) {
        if (context == null) return false;
        try {
            File directory = directory(context);
            File metadataFile = new File(directory, METADATA_FILE);
            if (!metadataFile.isFile()) return false;
            JSONObject metadata = new JSONObject(new String(readBytes(metadataFile), StandardCharsets.UTF_8));
            long createdAtMs = metadata.optLong("createdAtMs", 0L);
            if (createdAtMs <= 0L || System.currentTimeMillis() - createdAtMs > MAX_AGE_MS) {
                clearDirectory(directory);
                return false;
            }
            if (!safe(sessionId).equalsIgnoreCase(metadata.optString("sessionId", ""))
                    || !safe(starId).equalsIgnoreCase(metadata.optString("starId", ""))) {
                return false;
            }

            JSONArray chunks = metadata.optJSONArray("chunks");
            if (metadata.optJSONObject("response") == null || chunks == null || chunks.length() == 0) {
                clearDirectory(directory);
                return false;
            }
            for (int i = 0; i < chunks.length(); i++) {
                JSONObject chunk = chunks.optJSONObject(i);
                if (chunk == null) return false;
                File audioFile = new File(directory, chunk.optString("fileName", ""));
                if (!audioFile.isFile() || audioFile.length() <= 0L) {
                    clearDirectory(directory);
                    return false;
                }
            }
            return true;
        } catch (Exception ignored) {
            clear(context);
            return false;
        }
    }

    static synchronized void clear(Context context) {
        if (context != null) clearDirectory(directory(context));
    }

    private static File directory(Context context) {
        return new File(context.getFilesDir(), DIRECTORY_NAME);
    }

    private static void clearDirectory(File directory) {
        if (directory == null || !directory.exists()) return;
        File[] children = directory.listFiles();
        if (children != null) {
            for (File child : children) {
                if (child.isDirectory()) clearDirectory(child);
                if (!child.delete()) child.deleteOnExit();
            }
        }
        if (!directory.delete()) directory.deleteOnExit();
    }

    private static void writeBytes(File file, byte[] bytes) throws Exception {
        try (FileOutputStream out = new FileOutputStream(file)) {
            out.write(bytes);
            out.flush();
            out.getFD().sync();
        }
    }

    private static byte[] readBytes(File file) throws Exception {
        try (FileInputStream in = new FileInputStream(file);
             ByteArrayOutputStream out = new ByteArrayOutputStream((int) Math.min(Integer.MAX_VALUE, Math.max(1024L, file.length())))) {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = in.read(buffer)) >= 0) out.write(buffer, 0, read);
            return out.toByteArray();
        }
    }

    private static String safe(String value) {
        return value == null ? "" : value.trim();
    }

    private static String optStringEither(JSONObject json, String camel, String pascal) {
        String value = json.optString(camel, "");
        return value.length() > 0 ? value : json.optString(pascal, "");
    }

    private static boolean optBooleanEither(JSONObject json, String camel, String pascal, boolean fallback) {
        if (json.has(camel)) return json.optBoolean(camel, fallback);
        if (json.has(pascal)) return json.optBoolean(pascal, fallback);
        return fallback;
    }

    private static int optIntEither(JSONObject json, String camel, String pascal, int fallback) {
        if (json.has(camel)) return json.optInt(camel, fallback);
        if (json.has(pascal)) return json.optInt(pascal, fallback);
        return fallback;
    }

    private static JSONArray optArrayEither(JSONObject json, String camel, String pascal) {
        JSONArray value = json.optJSONArray(camel);
        return value != null ? value : json.optJSONArray(pascal);
    }

    static final class CachedInitiative {
        final String sessionId;
        final String starId;
        final long createdAtMs;
        final JSONObject response;
        final List<CachedVoiceChunk> chunks;

        CachedInitiative(String sessionId, String starId, long createdAtMs, JSONObject response, List<CachedVoiceChunk> chunks) {
            this.sessionId = sessionId;
            this.starId = starId;
            this.createdAtMs = createdAtMs;
            this.response = response;
            this.chunks = chunks;
        }
    }

    static final class CachedVoiceChunk {
        final int chunkIndex;
        final int totalChunks;
        final byte[] wav;
        final String sha256;
        final String speechActivitySegmentsMs;

        CachedVoiceChunk(int chunkIndex, int totalChunks, byte[] wav, String sha256, String speechActivitySegmentsMs) {
            this.chunkIndex = chunkIndex;
            this.totalChunks = totalChunks;
            this.wav = wav;
            this.sha256 = sha256 == null ? "" : sha256;
            this.speechActivitySegmentsMs = speechActivitySegmentsMs == null ? "" : speechActivitySegmentsMs;
        }
    }
}
