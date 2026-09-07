package com.wds.sixstarsstandalone;

import android.content.Context;

import java.io.File;
import java.io.FileWriter;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.time.OffsetDateTime;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Small opt-in lifecycle trace with one global file plus automatically routed per-Star files. */
final class RuntimeTrace {
    private static final Pattern STAR_FIELD = Pattern.compile("(?:^|\\s)star=([A-Za-z]+)(?:\\s|$)", Pattern.CASE_INSENSITIVE);
    private static final String[] STAR_IDS = {"Elusiaa","Kaelia","Maera","Khavia","Auerlis","Nyxari"};

    private final boolean enabled;
    private final File dir;
    private final File file;

    RuntimeTrace(Context context, StandaloneSettings settings) {
        enabled = settings.traceEnabled();
        dir = new File(context.getFilesDir(), "logs");
        if (!dir.exists()) dir.mkdirs();
        file = new File(dir, "standalone-runtime.log");
    }

    synchronized void log(String event, String detail) {
        if (!enabled) return;
        String line = OffsetDateTime.now() + " | " + event
                + (detail == null || detail.isEmpty() ? "" : " | " + detail.replace('\n',' ')) + "\n";
        append(file, line);
        String star = detectStar(detail);
        if (!star.isEmpty()) append(fileForStar(star), line);
    }

    File file() { return file; }
    File fileForStar(String starId) {
        String safe = normalizeStar(starId).toLowerCase(Locale.US);
        if (safe.isEmpty()) safe = "unknown";
        return new File(dir, "standalone-runtime-" + safe + ".log");
    }

    synchronized String readTail(int maxBytes) { return readTail(file, maxBytes); }
    synchronized String readTailForStar(String starId, int maxBytes) {
        File starFile = fileForStar(starId);
        if (!starFile.exists()) return enabled
                ? "No trace entries have been written for " + normalizeStar(starId) + " yet."
                : "Runtime trace file logging is disabled.";
        return readTail(starFile, maxBytes);
    }

    private String readTail(File source, int maxBytes) {
        if (!source.exists()) return enabled ? "Trace is enabled, but no events have been written yet." : "Runtime trace file logging is disabled.";
        int cap = Math.max(4096, maxBytes);
        try (RandomAccessFile in = new RandomAccessFile(source, "r")) {
            long length = in.length();
            long start = Math.max(0L, length - cap);
            in.seek(start);
            byte[] bytes = new byte[(int)(length - start)];
            in.readFully(bytes);
            String text = new String(bytes, StandardCharsets.UTF_8);
            if (start > 0) {
                int newline = text.indexOf('\n');
                if (newline >= 0 && newline + 1 < text.length()) text = text.substring(newline + 1);
                text = "… older trace entries omitted …\n" + text;
            }
            return text.trim().isEmpty() ? "Trace file is empty." : text;
        } catch (Exception e) {
            return "Unable to read trace: " + e.getMessage();
        }
    }

    private static void append(File target, String line) {
        try (FileWriter writer = new FileWriter(target, true)) {
            writer.write(line);
        } catch (Exception ignored) { }
    }

    private static String detectStar(String detail) {
        if (detail == null || detail.isEmpty()) return "";
        Matcher field = STAR_FIELD.matcher(detail);
        if (field.find()) return normalizeStar(field.group(1));
        String trimmed = detail.trim();
        for (String star : STAR_IDS) {
            if (trimmed.regionMatches(true, 0, star + " •", 0, star.length() + 2)
                    || trimmed.regionMatches(true, 0, star + ":", 0, star.length() + 1)) return star;
        }
        return "";
    }

    private static String normalizeStar(String value) {
        if (value == null) return "";
        for (String star : STAR_IDS) if (star.equalsIgnoreCase(value.trim())) return star;
        return value.trim().replaceAll("[^A-Za-z0-9_-]", "");
    }
}
