package com.wds.sixstarsmobile;

import java.io.*;
import java.util.function.BooleanSupplier;

/** Pure transport decoder: never releases partial, duplicate, or out-of-order PCM. */
final class MusicPcmStreamReader {
    interface Sink {
        void begin(int rate, int channels, int totalFrames) throws Exception;
        void chunk(byte[] pcm, int rate, int channels) throws Exception;
        void end() throws Exception;
    }
    static void read(DataInputStream in, BooleanSupplier cancelled, Sink sink) throws Exception {
        if (in.readInt() != 0x52534D31) throw new IOException("Unsupported music stream format.");
        int rate = little(in), channels = little(in), totalFrames = little(in);
        if (rate != 24000 || channels != 1 || totalFrames < 1 || totalFrames > 24000 * 181) throw new IOException("Invalid music stream metadata.");
        if (cancelled.getAsBoolean()) throw new IOException("Music cancelled.");
        sink.begin(rate, channels, totalFrames);
        int expectedSequence = 0; long received = 0;
        while (!cancelled.getAsBoolean()) {
            int sequence = little(in), length = little(in);
            if (sequence != expectedSequence++) throw new IOException("Music chunks arrived out of order.");
            if (length == 0) {
                if (received != (long)totalFrames * channels * 2) throw new IOException("Music stream ended before all samples arrived.");
                sink.end(); return;
            }
            if (length < 0 || length > 256000 || length % (channels * 2) != 0 || received + length > (long)totalFrames * channels * 2)
                throw new IOException("Invalid music chunk size.");
            byte[] pcm = new byte[length]; in.readFully(pcm);
            if (cancelled.getAsBoolean()) throw new IOException("Music cancelled.");
            sink.chunk(pcm, rate, channels); received += length;
        }
        throw new IOException("Music cancelled.");
    }
    private static int little(DataInputStream in) throws IOException { return Integer.reverseBytes(in.readInt()); }
}
