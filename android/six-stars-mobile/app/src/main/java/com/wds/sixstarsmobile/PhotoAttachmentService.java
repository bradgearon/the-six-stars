package com.wds.sixstarsmobile;

import android.content.ContentResolver;
import android.content.Context;
import android.net.Uri;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.util.concurrent.CompletableFuture;

/** Loads and validates user-selected images away from the Activity and UI thread. */
final class PhotoAttachmentService {
    static final class Attachment {
        final byte[] bytes;
        final String mimeType;
        final String displayName;

        Attachment(byte[] bytes, String mimeType, String displayName) {
            this.bytes = bytes == null ? new byte[0] : bytes;
            this.mimeType = mimeType == null || mimeType.isEmpty() ? "image/jpeg" : mimeType;
            this.displayName = displayName == null ? "mobile-photo" : displayName;
        }
    }

    private final Context context;

    PhotoAttachmentService(Context context) { this.context = context.getApplicationContext(); }

    CompletableFuture<Attachment> load(Uri uri) {
        return CompletableFuture.supplyAsync(() -> {
            if (uri == null) throw new IllegalArgumentException("No image URI was selected.");
            ContentResolver resolver = context.getContentResolver();
            String mime = resolver.getType(uri);
            try (InputStream input = resolver.openInputStream(uri)) {
                if (input == null) throw new IllegalStateException("Android could not open the selected image.");
                byte[] bytes = readAll(input, 16 * 1024 * 1024);
                if (bytes.length == 0) throw new IllegalStateException("The selected image was empty.");
                return new Attachment(bytes, mime, uri.getLastPathSegment());
            } catch (Exception error) {
                throw new RuntimeException(error);
            }
        });
    }

    private static byte[] readAll(InputStream input, int maxBytes) throws Exception {
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = input.read(buffer)) >= 0) {
            if (output.size() + read > maxBytes) throw new IllegalStateException("The selected image is larger than 16 MB.");
            output.write(buffer, 0, read);
        }
        return output.toByteArray();
    }
}
