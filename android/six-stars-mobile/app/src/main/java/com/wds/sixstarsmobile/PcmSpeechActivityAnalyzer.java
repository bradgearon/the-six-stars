package com.wds.sixstarsmobile;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;

/**
 * Small forward-only PCM16 RMS analyzer used by SmartPcmPlaybackWriter.
 *
 * The constants intentionally mirror the desktop MobileAudioSpeechActivityMarkers analyzer so
 * moving live lip sync onto Android does not change the shape of the mouth/talking envelope.
 */
final class PcmSpeechActivityAnalyzer {
    private static final int FRAME_MS = 10;
    private static final int MAX_SEGMENTS = 220;
    private static final int PAD_BEFORE_MS = 20;
    private static final int PAD_AFTER_MS = 55;
    private static final int MERGE_GAP_MS = 75;
    private static final int MIN_SEGMENT_MS = 70;
    private static final int MAX_SEGMENT_MS = 3500;

    static final class Analysis {
        final int[] segmentsMs;
        final double peak;
        final double highThreshold;
        final double lowThreshold;

        Analysis(int[] segmentsMs, double peak, double highThreshold, double lowThreshold) {
            this.segmentsMs = segmentsMs == null ? new int[0] : segmentsMs;
            this.peak = peak;
            this.highThreshold = highThreshold;
            this.lowThreshold = lowThreshold;
        }
    }

    private static final class Segment {
        int startMs;
        int durationMs;

        Segment(int startMs, int durationMs) {
            this.startMs = startMs;
            this.durationMs = durationMs;
        }
    }

    private PcmSpeechActivityAnalyzer() { }

    static Analysis analyzePcm16(byte[] pcm, int sampleRate, int channels) {
        try {
            if (pcm == null || pcm.length < 2 || sampleRate <= 0 || channels <= 0) {
                return new Analysis(new int[0], 0.0, 0.0, 0.0);
            }

            int bytesPerFrame = Math.max(2, channels * 2);
            int totalSamplesPerChannel = pcm.length / bytesPerFrame;
            if (totalSamplesPerChannel <= 0) return new Analysis(new int[0], 0.0, 0.0, 0.0);

            int samplesPerWindow = Math.max(80, sampleRate * FRAME_MS / 1000);
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
                    int baseOffset = sample * bytesPerFrame;
                    int mixed = 0;
                    for (int channel = 0; channel < channels; channel++) {
                        int sampleOffset = baseOffset + channel * 2;
                        if (sampleOffset + 1 >= pcm.length) break;
                        int lo = pcm[sampleOffset] & 0xff;
                        int hi = pcm[sampleOffset + 1];
                        short value = (short) ((hi << 8) | lo);
                        mixed += value;
                    }

                    double normalized = (mixed / (double) Math.max(1, channels)) / 32768.0;
                    squareSum += normalized * normalized;
                    count++;
                }

                double valueRms = count == 0 ? 0.0 : Math.sqrt(squareSum / count);
                rms[window] = valueRms;
                peak = Math.max(peak, valueRms);
                sum += valueRms;
            }

            // Very quiet transport tails intentionally return no marker. SmartPcmPlaybackWriter
            // distinguishes a genuinely quiet block from an audible block whose envelope was too
            // unstable to segment, so silence no longer forces a whole-block talking fallback.
            if (peak < 0.004) return new Analysis(new int[0], peak, 0.0, 0.0);

            double mean = sum / rms.length;
            double[] sorted = Arrays.copyOf(rms, rms.length);
            Arrays.sort(sorted);
            double lowFloor = sorted[Math.min(sorted.length - 1, Math.max(0, sorted.length / 5))];
            double highThreshold = Math.max(0.012, Math.max(mean * 1.40, lowFloor + peak * 0.20));
            highThreshold = Math.min(highThreshold, peak * 0.62);
            double lowThreshold = Math.max(0.006, highThreshold * 0.42);

            ArrayList<Segment> raw = new ArrayList<>();
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

            // Padding may make the mouth open a little before/after the detected syllable, but it
            // must never extend beyond the PCM that was actually analyzed.
            int analyzedDurationMs = (int) Math.max(1L,
                    (totalSamplesPerChannel * 1000L + sampleRate - 1L) / sampleRate);
            ArrayList<Segment> padded = new ArrayList<>();
            for (Segment segment : raw) {
                int paddedStartMs = Math.max(0, segment.startMs - PAD_BEFORE_MS);
                int paddedEndMs = Math.min(
                        analyzedDurationMs,
                        segment.startMs + segment.durationMs + PAD_AFTER_MS);
                int paddedDurationMs = Math.max(0, paddedEndMs - paddedStartMs);
                if (paddedDurationMs >= MIN_SEGMENT_MS) {
                    padded.add(new Segment(paddedStartMs, paddedDurationMs));
                }
            }
            padded.sort(Comparator.comparingInt(x -> x.startMs));

            ArrayList<Segment> merged = mergeSegments(padded);
            if (merged.size() > MAX_SEGMENTS) {
                merged = new ArrayList<>(merged.subList(0, MAX_SEGMENTS));
            }

            int[] compact = new int[merged.size() * 2];
            for (int i = 0; i < merged.size(); i++) {
                compact[i * 2] = merged.get(i).startMs;
                compact[i * 2 + 1] = merged.get(i).durationMs;
            }
            return new Analysis(compact, peak, highThreshold, lowThreshold);
        } catch (Throwable ignored) {
            // Lip sync must never be allowed to break audio playback.
            return new Analysis(new int[0], 0.0, 0.0, 0.0);
        }
    }

    private static void addSegment(ArrayList<Segment> segments, int startMs, int durationMs) {
        if (durationMs <= 0) return;
        segments.add(new Segment(
                Math.max(0, startMs),
                Math.max(MIN_SEGMENT_MS, Math.min(MAX_SEGMENT_MS, durationMs))));
    }

    private static ArrayList<Segment> mergeSegments(ArrayList<Segment> source) {
        ArrayList<Segment> merged = new ArrayList<>();
        for (Segment segment : source) {
            if (merged.isEmpty()) {
                merged.add(new Segment(segment.startMs, segment.durationMs));
                continue;
            }

            Segment previous = merged.get(merged.size() - 1);
            int previousEnd = previous.startMs + previous.durationMs;
            int gap = segment.startMs - previousEnd;
            if (gap <= MERGE_GAP_MS) {
                int newEnd = Math.max(previousEnd, segment.startMs + segment.durationMs);
                previous.durationMs = Math.min(MAX_SEGMENT_MS, newEnd - previous.startMs);
            } else {
                merged.add(new Segment(segment.startMs, segment.durationMs));
            }
        }
        return merged;
    }
}
