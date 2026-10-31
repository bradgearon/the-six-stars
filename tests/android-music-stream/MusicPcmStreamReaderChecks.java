package com.wds.sixstarsmobile;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public final class MusicPcmStreamReaderChecks {
    static class Sink implements MusicPcmStreamReader.Sink {
        int chunks, expectedFrames; boolean ended, cancelled;
        ByteArrayOutputStream pcm = new ByteArrayOutputStream();
        public void begin(int rate, int channels, int total) { expectedFrames = total; }
        public void chunk(byte[] data, int rate, int channels) throws Exception { chunks++; pcm.write(data); }
        public void end() { ended = true; }
    }
    static void check(boolean success, String message) { if (!success) throw new AssertionError(message); }
    static DataInputStream fragmented(byte[] wire) {
        return new DataInputStream(new FilterInputStream(new ByteArrayInputStream(wire)) {
            public int read(byte[] b, int o, int n) throws IOException { return super.read(b, o, Math.min(n, 7)); }
        });
    }
    public static void main(String[] args) throws Exception {
        byte[] wire = Files.readAllBytes(Paths.get(args[0]));
        Sink complete = new Sink();
        MusicPcmStreamReader.read(fragmented(wire), () -> false, complete);
        check(complete.ended && complete.chunks > 1 && complete.pcm.size() == complete.expectedFrames * 2, "Fragmented valid delivery failed");
        Sink partial = new Sink();
        try { MusicPcmStreamReader.read(fragmented(Arrays.copyOf(wire, 100)), () -> false, partial); throw new AssertionError("Truncated first chunk accepted"); } catch (EOFException expected) { }
        check(partial.chunks == 0 && !partial.ended, "Partial audio was published");
        Sink missingEnd = new Sink();
        try { MusicPcmStreamReader.read(fragmented(Arrays.copyOf(wire, wire.length - 8)), () -> false, missingEnd); throw new AssertionError("Missing terminal accepted"); } catch (EOFException expected) { }
        check(!missingEnd.ended, "Unexpected EOF treated as success");
        byte[] duplicate = wire.clone(); int second = 16 + 8 + 12000;
        Arrays.fill(duplicate, second, second + 4, (byte)0);
        Sink reordered = new Sink();
        try { MusicPcmStreamReader.read(fragmented(duplicate), () -> false, reordered); throw new AssertionError("Duplicate chunk accepted"); } catch (IOException expected) { }
        check(reordered.chunks == 1, "Duplicate audio published");
        Sink cancelled = new Sink();
        try { MusicPcmStreamReader.read(fragmented(wire), () -> cancelled.chunks > 0, cancelled); throw new AssertionError("Cancellation ignored"); } catch (IOException expected) { }
        check(cancelled.chunks == 1 && !cancelled.ended, "Cancellation replayed or completed audio");
        System.out.println("PASS: real host wire format, fragmented reads, complete-chunk gating, missing end, duplicate/out-of-order rejection and cancellation.");
    }
}
