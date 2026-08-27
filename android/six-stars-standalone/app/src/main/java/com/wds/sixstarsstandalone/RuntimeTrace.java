package com.wds.sixstarsstandalone;

import android.content.Context;

import java.io.File;
import java.io.FileWriter;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.time.OffsetDateTime;

/** Small opt-in lifecycle trace mirroring the desktop background-analysis trace philosophy. */
final class RuntimeTrace {
    private final boolean enabled;
    private final File file;

    RuntimeTrace(Context context, StandaloneSettings settings) {
        enabled = settings.traceEnabled();
        File dir = new File(context.getFilesDir(), "logs");
        if (!dir.exists()) dir.mkdirs();
        file = new File(dir, "standalone-runtime.log");
    }

    synchronized void log(String event, String detail) {
        if (!enabled) return;
        try (FileWriter writer = new FileWriter(file, true)) {
            writer.write(OffsetDateTime.now() + " | " + event + (detail == null || detail.isEmpty() ? "" : " | " + detail.replace('\n',' ')) + "\n");
        } catch (Exception ignored) { }
    }

    File file() { return file; }

    synchronized String readTail(int maxBytes) {
        if (!file.exists()) return enabled ? "Trace is enabled, but no events have been written yet." : "Runtime trace file logging is disabled.";
        int cap = Math.max(4096, maxBytes);
        try (RandomAccessFile in = new RandomAccessFile(file, "r")) {
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
}
