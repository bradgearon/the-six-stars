using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Wds.SixStars.Ui.Networking;

/// <summary>
/// Tiny WAV amplitude envelope analyzer for Android sprite mouth/talking pulses.
/// It runs in one pass over PCM16 data and returns compact start:duration millisecond pairs.
/// The analysis intentionally stays simple so it is cheap enough to run inline while streaming TTS chunks.
/// </summary>
internal static class MobileAudioSpeechActivityMarkers
{
    private const int FrameMs = 10;
    private const int MaxSegments = 220;
    private const int PadBeforeMs = 20;
    private const int PadAfterMs = 55;
    private const int MergeGapMs = 75;
    private const int MinSegmentMs = 70;
    private const int MaxSegmentMs = 3500;

    public static MobileSpeechActivityResult AnalyzeWav(byte[] wavAudio)
    {
        try
        {
            if (!TryParsePcm16Wav(wavAudio, out int sampleRate, out int channels, out int dataOffset, out int dataLength))
            {
                return MobileSpeechActivityResult.Empty;
            }

            return AnalyzePcm16(wavAudio, dataOffset, dataLength, sampleRate, channels);
        }
        catch
        {
            // Animation markers are optional. Audio should never fail because visualization analysis failed.
            return MobileSpeechActivityResult.Empty;
        }
    }

    private static MobileSpeechActivityResult AnalyzePcm16(byte[] bytes, int dataOffset, int dataLength, int sampleRate, int channels)
    {
        int bytesPerFrame = Math.Max(2, channels * 2);
        int totalSamplesPerChannel = dataLength / bytesPerFrame;
        if (totalSamplesPerChannel <= 0 || sampleRate <= 0) return MobileSpeechActivityResult.Empty;

        int samplesPerWindow = Math.Max(80, sampleRate * FrameMs / 1000);
        int windowCount = Math.Max(1, (totalSamplesPerChannel + samplesPerWindow - 1) / samplesPerWindow);
        double[] rms = new double[windowCount];
        double peak = 0.0;
        double sum = 0.0;

        for (int window = 0; window < windowCount; window++)
        {
            int startSample = window * samplesPerWindow;
            int endSample = Math.Min(totalSamplesPerChannel, startSample + samplesPerWindow);
            double squareSum = 0.0;
            int count = 0;

            for (int sample = startSample; sample < endSample; sample++)
            {
                int baseOffset = dataOffset + sample * bytesPerFrame;
                int mixed = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = baseOffset + channel * 2;
                    if (sampleOffset + 1 >= bytes.Length) break;
                    short value = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(sampleOffset, 2));
                    mixed += value;
                }

                double normalized = (mixed / (double)Math.Max(1, channels)) / 32768.0;
                squareSum += normalized * normalized;
                count++;
            }

            double valueRms = count == 0 ? 0.0 : Math.Sqrt(squareSum / count);
            rms[window] = valueRms;
            peak = Math.Max(peak, valueRms);
            sum += valueRms;
        }

        if (peak < 0.004) return MobileSpeechActivityResult.Empty;

        double mean = sum / rms.Length;
        double[] sorted = rms.ToArray();
        Array.Sort(sorted);
        double lowFloor = sorted[Math.Min(sorted.Length - 1, Math.Max(0, sorted.Length / 5))];
        double highThreshold = Math.Max(0.012, Math.Max(mean * 1.40, lowFloor + peak * 0.20));
        highThreshold = Math.Min(highThreshold, peak * 0.62);
        double lowThreshold = Math.Max(0.006, highThreshold * 0.42);

        List<MobileSpeechActivitySegment> raw = new List<MobileSpeechActivitySegment>();
        bool active = false;
        int startWindow = 0;
        int quietRun = 0;

        for (int i = 0; i < rms.Length; i++)
        {
            double v = rms[i];
            if (!active)
            {
                if (v >= highThreshold)
                {
                    active = true;
                    startWindow = i;
                    quietRun = 0;
                }
            }
            else
            {
                if (v <= lowThreshold)
                {
                    quietRun++;
                    if (quietRun >= 2)
                    {
                        int endWindow = Math.Max(startWindow + 1, i - quietRun + 1);
                        AddSegment(raw, startWindow * FrameMs, (endWindow - startWindow + 1) * FrameMs);
                        active = false;
                        quietRun = 0;
                    }
                }
                else
                {
                    quietRun = 0;
                }
            }
        }

        if (active)
        {
            AddSegment(raw, startWindow * FrameMs, (rms.Length - startWindow) * FrameMs);
        }

        List<MobileSpeechActivitySegment> padded = raw
            .Select(x => new MobileSpeechActivitySegment(Math.Max(0, x.StartMs - PadBeforeMs), x.DurationMs + PadBeforeMs + PadAfterMs))
            .Where(x => x.DurationMs >= MinSegmentMs)
            .OrderBy(x => x.StartMs)
            .ToList();

        List<MobileSpeechActivitySegment> merged = MergeSegments(padded);
        if (merged.Count == 0) return MobileSpeechActivityResult.Empty;

        if (merged.Count > MaxSegments)
        {
            merged = merged.Take(MaxSegments).ToList();
        }

        string compact = string.Join(";", merged.Select(x => x.StartMs.ToString(CultureInfo.InvariantCulture) + ":" + x.DurationMs.ToString(CultureInfo.InvariantCulture)));
        return new MobileSpeechActivityResult(compact, merged.Count, FrameMs, peak, highThreshold, lowThreshold);
    }

    private static void AddSegment(List<MobileSpeechActivitySegment> segments, int startMs, int durationMs)
    {
        if (durationMs <= 0) return;
        segments.Add(new MobileSpeechActivitySegment(Math.Max(0, startMs), Math.Max(MinSegmentMs, Math.Min(MaxSegmentMs, durationMs))));
    }

    private static List<MobileSpeechActivitySegment> MergeSegments(List<MobileSpeechActivitySegment> source)
    {
        List<MobileSpeechActivitySegment> merged = new List<MobileSpeechActivitySegment>();
        foreach (MobileSpeechActivitySegment segment in source)
        {
            if (merged.Count == 0)
            {
                merged.Add(segment);
                continue;
            }

            MobileSpeechActivitySegment previous = merged[merged.Count - 1];
            int previousEnd = previous.StartMs + previous.DurationMs;
            int gap = segment.StartMs - previousEnd;
            if (gap <= MergeGapMs)
            {
                int newEnd = Math.Max(previousEnd, segment.StartMs + segment.DurationMs);
                merged[merged.Count - 1] = new MobileSpeechActivitySegment(previous.StartMs, Math.Min(MaxSegmentMs, newEnd - previous.StartMs));
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    private static bool TryParsePcm16Wav(byte[] bytes, out int sampleRate, out int channels, out int dataOffset, out int dataLength)
    {
        sampleRate = 24000;
        channels = 1;
        dataOffset = 0;
        dataLength = 0;

        if (bytes == null || bytes.Length < 44) return false;
        if (!HasAscii(bytes, 0, "RIFF") || !HasAscii(bytes, 8, "WAVE")) return false;

        int offset = 12;
        bool foundFmt = false;
        bool foundData = false;
        int bitsPerSample = 16;
        int audioFormat = 1;

        while (offset + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;
            if (size < 0 || offset + size > bytes.Length) break;

            if (id == "fmt " && size >= 16)
            {
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2));
                channels = Math.Max(1, (int) BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 2, 2)));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset + 14, 2));
                foundFmt = true;
            }
            else if (id == "data")
            {
                dataOffset = offset;
                dataLength = size;
                foundData = true;
            }

            offset += size + (size % 2);
        }

        return foundFmt && foundData && audioFormat == 1 && bitsPerSample == 16 && sampleRate > 0 && channels > 0 && dataLength > 0;
    }

    private static bool HasAscii(byte[] bytes, int offset, string text)
    {
        if (bytes.Length < offset + text.Length) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (bytes[offset + i] != (byte)text[i]) return false;
        }
        return true;
    }

    private readonly record struct MobileSpeechActivitySegment(int StartMs, int DurationMs);
}

internal sealed class MobileSpeechActivityResult
{
    public static readonly MobileSpeechActivityResult Empty = new MobileSpeechActivityResult(string.Empty, 0, 0, 0.0, 0.0, 0.0);

    public MobileSpeechActivityResult(string segments, int count, int frameMs, double peak, double highThreshold, double lowThreshold)
    {
        Segments = segments ?? string.Empty;
        Count = count;
        FrameMs = frameMs;
        Peak = peak;
        HighThreshold = highThreshold;
        LowThreshold = lowThreshold;
    }

    public string Segments { get; }
    public int Count { get; }
    public int FrameMs { get; }
    public double Peak { get; }
    public double HighThreshold { get; }
    public double LowThreshold { get; }
}
