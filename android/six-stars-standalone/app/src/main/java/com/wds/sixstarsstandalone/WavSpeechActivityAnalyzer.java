package com.wds.sixstarsstandalone;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/**
 * Android port of desktop MobileAudioSpeechActivityMarkers.
 *
 * One cheap pass over PCM16 WAV data estimates 10 ms RMS loudness windows and emits compact
 * start/duration speech activity ranges. These ranges drive the exact same talking/idle mouth
 * pulses used by the thin client. Analysis is optional and never blocks audio from playing.
 */
final class WavSpeechActivityAnalyzer {
    private static final int FRAME_MS = 10;
    private static final int MAX_SEGMENTS = 220;
    private static final int PAD_BEFORE_MS = 20;
    private static final int PAD_AFTER_MS = 55;
    private static final int MERGE_GAP_MS = 75;
    private static final int MIN_SEGMENT_MS = 70;
    private static final int MAX_SEGMENT_MS = 3500;

    static final class Analysis {
        static final Analysis EMPTY = new Analysis(new int[0], 0, 0.0, 0.0, 0.0, 0L);
        final int[] segmentsMs;
        final int count;
        final double peak;
        final double highThreshold;
        final double lowThreshold;
        final long durationMs;

        Analysis(int[] segmentsMs, int count, double peak, double highThreshold, double lowThreshold, long durationMs) {
            this.segmentsMs = segmentsMs == null ? new int[0] : segmentsMs;
            this.count = Math.max(0, count);
            this.peak = peak;
            this.highThreshold = highThreshold;
            this.lowThreshold = lowThreshold;
            this.durationMs = Math.max(0L, durationMs);
        }

        String compact() {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i + 1 < segmentsMs.length; i += 2) {
                if (b.length() > 0) b.append(';');
                b.append(segmentsMs[i]).append(':').append(segmentsMs[i + 1]);
            }
            return b.toString();
        }

        String debugSummary() {
            return "segments=" + count
                    + " peak=" + String.format(Locale.US, "%.4f", peak)
                    + " high=" + String.format(Locale.US, "%.4f", highThreshold)
                    + " low=" + String.format(Locale.US, "%.4f", lowThreshold)
                    + " durationMs=" + durationMs;
        }
    }

    private static final class WavInfo {
        int sampleRate = 24000;
        int channels = 1;
        int dataOffset;
        int dataLength;
    }

    private static final class Segment {
        final int startMs;
        final int durationMs;
        Segment(int startMs, int durationMs) {
            this.startMs = Math.max(0, startMs);
            this.durationMs = Math.max(0, durationMs);
        }
    }

    private WavSpeechActivityAnalyzer() { }

    static Analysis analyze(byte[] wavAudio) {
        try {
            WavInfo info = parsePcm16Wav(wavAudio);
            if (info == null) return Analysis.EMPTY;
            return analyzePcm16(wavAudio, info);
        } catch (Throwable ignored) {
            return Analysis.EMPTY;
        }
    }

    private static Analysis analyzePcm16(byte[] bytes, WavInfo info) {
        int bytesPerFrame = Math.max(2, info.channels * 2);
        int totalSamplesPerChannel = info.dataLength / bytesPerFrame;
        if (totalSamplesPerChannel <= 0 || info.sampleRate <= 0) return Analysis.EMPTY;

        long durationMs = totalSamplesPerChannel * 1000L / Math.max(1, info.sampleRate);
        int samplesPerWindow = Math.max(80, info.sampleRate * FRAME_MS / 1000);
        int windowCount = Math.max(1, (totalSamplesPerChannel + samplesPerWindow - 1) / samplesPerWindow);
        double[] rms = new double[windowCount];
        double peak = 0.0;
        double sum = 0.0;

        for (int window = 0; window < windowCount; window++) {
            int startSample = window * samplesPerWindow;
            int endSample = Math.min(totalSamplesPerChannel, startSample + samplesPerWindow);
            double squareSum = 0.0;
            int count = 0;

            for (int sample = startSample; sample < endSample; sample++) {
                int baseOffset = info.dataOffset + sample * bytesPerFrame;
                int mixed = 0;
                for (int channel = 0; channel < info.channels; channel++) {
                    int sampleOffset = baseOffset + channel * 2;
                    if (sampleOffset + 1 >= bytes.length) break;
                    mixed += readInt16Le(bytes, sampleOffset);
                }
                double normalized = (mixed / (double)Math.max(1, info.channels)) / 32768.0;
                squareSum += normalized * normalized;
                count++;
            }

            double valueRms = count == 0 ? 0.0 : Math.sqrt(squareSum / count);
            rms[window] = valueRms;
            peak = Math.max(peak, valueRms);
            sum += valueRms;
        }

        if (peak < 0.004) return new Analysis(new int[0], 0, peak, 0.0, 0.0, durationMs);

        double mean = sum / rms.length;
        double[] sorted = Arrays.copyOf(rms, rms.length);
        Arrays.sort(sorted);
        double lowFloor = sorted[Math.min(sorted.length - 1, Math.max(0, sorted.length / 5))];
        double highThreshold = Math.max(0.012, Math.max(mean * 1.40, lowFloor + peak * 0.20));
        highThreshold = Math.min(highThreshold, peak * 0.62);
        double lowThreshold = Math.max(0.006, highThreshold * 0.42);

        List<Segment> raw = new ArrayList<>();
        boolean active = false;
        int startWindow = 0;
        int quietRun = 0;

        for (int i = 0; i < rms.length; i++) {
            double value = rms[i];
            if (!active) {
                if (value >= highThreshold) {
                    active = true;
                    startWindow = i;
                    quietRun = 0;
                }
            } else if (value <= lowThreshold) {
                quietRun++;
                if (quietRun >= 2) {
                    int endWindow = Math.max(startWindow + 1, i - quietRun + 1);
                    addSegment(raw, startWindow * FRAME_MS, (endWindow - startWindow + 1) * FRAME_MS);
                    active = false;
                    quietRun = 0;
                }
            } else {
                quietRun = 0;
            }
        }
        if (active) addSegment(raw, startWindow * FRAME_MS, (rms.length - startWindow) * FRAME_MS);

        List<Segment> padded = new ArrayList<>();
        for (Segment segment : raw) {
            Segment p = new Segment(Math.max(0, segment.startMs - PAD_BEFORE_MS), segment.durationMs + PAD_BEFORE_MS + PAD_AFTER_MS);
            if (p.durationMs >= MIN_SEGMENT_MS) padded.add(p);
        }
        padded.sort(Comparator.comparingInt(x -> x.startMs));
        List<Segment> merged = mergeSegments(padded);
        if (merged.size() > MAX_SEGMENTS) merged = new ArrayList<>(merged.subList(0, MAX_SEGMENTS));

        int[] compact = new int[merged.size() * 2];
        for (int i = 0; i < merged.size(); i++) {
            compact[i * 2] = merged.get(i).startMs;
            compact[i * 2 + 1] = merged.get(i).durationMs;
        }
        return new Analysis(compact, merged.size(), peak, highThreshold, lowThreshold, durationMs);
    }

    private static void addSegment(List<Segment> segments, int startMs, int durationMs) {
        if (durationMs <= 0) return;
        segments.add(new Segment(Math.max(0, startMs), Math.max(MIN_SEGMENT_MS, Math.min(MAX_SEGMENT_MS, durationMs))));
    }

    private static List<Segment> mergeSegments(List<Segment> source) {
        List<Segment> merged = new ArrayList<>();
        for (Segment segment : source) {
            if (merged.isEmpty()) {
                merged.add(segment);
                continue;
            }
            Segment previous = merged.get(merged.size() - 1);
            int previousEnd = previous.startMs + previous.durationMs;
            int gap = segment.startMs - previousEnd;
            if (gap <= MERGE_GAP_MS) {
                int newEnd = Math.max(previousEnd, segment.startMs + segment.durationMs);
                merged.set(merged.size() - 1, new Segment(previous.startMs, Math.min(MAX_SEGMENT_MS, newEnd - previous.startMs)));
            } else {
                merged.add(segment);
            }
        }
        return merged;
    }

    private static WavInfo parsePcm16Wav(byte[] bytes) {
        if (bytes == null || bytes.length < 44 || !hasAscii(bytes, 0, "RIFF") || !hasAscii(bytes, 8, "WAVE")) return null;
        int offset = 12;
        boolean foundFmt = false;
        boolean foundData = false;
        int bitsPerSample = 16;
        int audioFormat = 1;
        WavInfo info = new WavInfo();

        while (offset + 8 <= bytes.length) {
            String id = new String(bytes, offset, 4, StandardCharsets.US_ASCII);
            int size = readInt32Le(bytes, offset + 4);
            offset += 8;
            if (size < 0 || offset + size > bytes.length) break;

            if ("fmt ".equals(id) && size >= 16) {
                audioFormat = readInt16Le(bytes, offset) & 0xffff;
                info.channels = Math.max(1, readInt16Le(bytes, offset + 2) & 0xffff);
                info.sampleRate = readInt32Le(bytes, offset + 4);
                bitsPerSample = readInt16Le(bytes, offset + 14) & 0xffff;
                foundFmt = true;
            } else if ("data".equals(id)) {
                info.dataOffset = offset;
                info.dataLength = size;
                foundData = true;
            }
            offset += size + (size % 2);
        }
        return foundFmt && foundData && audioFormat == 1 && bitsPerSample == 16
                && info.sampleRate > 0 && info.channels > 0 && info.dataLength > 0 ? info : null;
    }

    private static boolean hasAscii(byte[] bytes, int offset, String text) {
        if (bytes.length < offset + text.length()) return false;
        for (int i = 0; i < text.length(); i++) if (bytes[offset + i] != (byte)text.charAt(i)) return false;
        return true;
    }

    private static short readInt16Le(byte[] bytes, int offset) {
        int lo = bytes[offset] & 0xff;
        int hi = bytes[offset + 1] & 0xff;
        return (short)(lo | (hi << 8));
    }

    private static int readInt32Le(byte[] bytes, int offset) {
        return (bytes[offset] & 0xff)
                | ((bytes[offset + 1] & 0xff) << 8)
                | ((bytes[offset + 2] & 0xff) << 16)
                | ((bytes[offset + 3] & 0xff) << 24);
    }
}
