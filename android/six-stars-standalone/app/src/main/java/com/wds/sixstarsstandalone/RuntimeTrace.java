package com.wds.sixstarsstandalone;

import android.content.Context;

import java.io.File;
import java.io.FileWriter;
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
}
