package com.wds.sixstarsmobile;

import android.content.res.AssetManager;
import android.graphics.Bitmap;
import com.bumptech.glide.gifdecoder.GifDecoder;
import com.bumptech.glide.gifdecoder.GifHeader;
import com.bumptech.glide.gifdecoder.GifHeaderParser;
import com.bumptech.glide.gifdecoder.StandardGifDecoder;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;

/** Precomposited GIF frames: the view owns the displayed frame index, not RenderThread. */
final class IndexedStarGif {
    final Bitmap[] frames;
    final int width;
    final int height;
    private final int[] endsMs;
    private final int durationMs;

    private IndexedStarGif(Bitmap[] frames, int[] delays, int width, int height) {
        this.frames = frames;
        this.width = width;
        this.height = height;
        endsMs = new int[delays.length];
        int total = 0;
        for (int i = 0; i < delays.length; i++) {
            total += Math.max(10, delays[i]);
            endsMs[i] = total;
        }
        durationMs = total;
    }

    int frameAt(long elapsedMs) {
        long time = Math.floorMod(elapsedMs, (long) durationMs);
        for (int i = 0; i < endsMs.length; i++) {
            if (time < endsMs[i]) return i;
        }
        return frames.length - 1;
    }

    static IndexedStarGif load(AssetManager assets, String path) throws IOException {
        byte[] data;
        try (InputStream input = assets.open(path);
             ByteArrayOutputStream output = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192];
            int count;
            while ((count = input.read(buffer)) != -1) {
                if (output.size() + count > 4 * 1024 * 1024) {
                    throw new IOException("Idle GIF exceeds compressed size limit");
                }
                output.write(buffer, 0, count);
            }
            data = output.toByteArray();
        }
        GifHeaderParser parser = new GifHeaderParser();
        GifHeader header;
        try {
            header = parser.setData(data).parseHeader();
        } finally {
            parser.clear();
        }
        int count = header.getNumFrames();
        long pixels = (long) header.getWidth() * header.getHeight();
        if (header.getStatus() != GifDecoder.STATUS_OK || count < 1 || count > 256
                || pixels <= 0 || pixels * count > 2 * 1024 * 1024) {
            throw new IOException("Invalid or oversized idle GIF");
        }
        StandardGifDecoder decoder = new StandardGifDecoder(new GifDecoder.BitmapProvider() {
            @Override public Bitmap obtain(int w, int h, Bitmap.Config config) {
                return Bitmap.createBitmap(w, h, config);
            }
            @Override public void release(Bitmap bitmap) { bitmap.recycle(); }
            @Override public byte[] obtainByteArray(int size) { return new byte[size]; }
            @Override public void release(byte[] bytes) { }
            @Override public int[] obtainIntArray(int size) { return new int[size]; }
            @Override public void release(int[] array) { }
        });
        Bitmap[] frames = new Bitmap[count];
        int[] delays = new int[count];
        boolean completed = false;
        try {
            decoder.setData(header, data);
            for (int i = 0; i < count; i++) {
                decoder.advance();
                frames[i] = decoder.getNextFrame();
                if (frames[i] == null || decoder.getStatus() != GifDecoder.STATUS_OK) {
                    throw new IOException("Could not decode idle frame " + i);
                }
                frames[i].setDensity(Bitmap.DENSITY_NONE);
                delays[i] = decoder.getDelay(i);
            }
            IndexedStarGif result = new IndexedStarGif(frames, delays, header.getWidth(), header.getHeight());
            completed = true;
            return result;
        } finally {
            decoder.clear();
            if (!completed) {
                for (Bitmap frame : frames) if (frame != null) frame.recycle();
            }
        }
    }
}
