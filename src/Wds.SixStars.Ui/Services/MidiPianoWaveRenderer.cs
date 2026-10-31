using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Midi;

namespace Wds.SixStars.Ui.Services;

/// <summary>Portable piano-style preview synthesis. No sound bank, audio device or VST is required.</summary>
public static class MidiPianoWaveRenderer
{
    private const int Rate = 24000;
    private sealed record Tone(int Pitch, int Velocity, double Start, double End);
    public sealed record PcmChunk(byte[] Pcm, int SampleRate, int Channels, int TotalFrames);

    public static byte[] Render(byte[] midiBytes, CancellationToken token)
    {
        using var pcm = new MemoryStream();
        foreach (var chunk in RenderChunks(midiBytes, token)) pcm.Write(chunk.Pcm, 0, chunk.Pcm.Length);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + (int)pcm.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(Rate); writer.Write(Rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write((int)pcm.Length);
        writer.Write(pcm.ToArray());
        return output.ToArray();
    }

    // One 250ms block at a time. Oscillator age and release use absolute sample positions,
    // so notes and their tails remain continuous across block boundaries.
    public static IEnumerable<PcmChunk> RenderChunks(byte[] midiBytes, CancellationToken token)
    {
        using var input = new MemoryStream(midiBytes, false);
        var midi = new MidiFile(input, false);
        if (midi.DeltaTicksPerQuarterNote <= 0 || midi.FileFormat == 2) throw new InvalidDataException("Music preview requires a synchronous MIDI file with musical tick timing.");
        var events = Enumerable.Range(0, midi.Tracks).SelectMany(t => midi.Events[t]).OrderBy(e => e.AbsoluteTime).ToArray();
        if (events.Length > 50000) throw new InvalidDataException("MIDI preview is too large.");
        var active = new Dictionary<(int, int), Queue<(double time, int velocity)>>();
        var tones = new List<Tone>();
        long lastTick = 0; double time = 0, tempo = 500000;
        foreach (var e in events) {
            token.ThrowIfCancellationRequested();
            time += (e.AbsoluteTime - lastTick) * tempo / 1000000d / midi.DeltaTicksPerQuarterNote;
            lastTick = e.AbsoluteTime;
            if (time > 180 || time < 0) throw new InvalidDataException("Music preview exceeds three minutes.");
            if (e is TempoEvent te) { tempo = te.MicrosecondsPerQuarterNote; continue; }
            if (e is not NoteEvent note) continue;
            var key = (note.Channel, note.NoteNumber);
            if (e.CommandCode == MidiCommandCode.NoteOn && note.Velocity > 0) {
                if (!active.TryGetValue(key, out var queue)) active[key] = queue = new();
                queue.Enqueue((time, note.Velocity));
            } else if (e.CommandCode == MidiCommandCode.NoteOff || (e.CommandCode == MidiCommandCode.NoteOn && note.Velocity == 0)) {
                if (active.TryGetValue(key, out var queue) && queue.Count > 0) {
                    var start = queue.Dequeue(); tones.Add(new Tone(note.NoteNumber, start.velocity, start.time, time));
                }
            }
        }
        foreach (var item in active) foreach (var start in item.Value) tones.Add(new Tone(item.Key.Item2, start.velocity, start.time, Math.Max(start.time + .1, time)));
        if (tones.Count == 0 || tones.Count > 8192) throw new InvalidDataException("MIDI contains no playable notes or too many notes.");
        int totalFrames = (int)Math.Ceiling((Math.Max(time, tones.Max(t => t.End)) + .4) * Rate);
        long work = 0;
        for (int offset = 0; offset < totalFrames; offset += Rate / 4) {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(Rate / 4, totalFrames - offset);
            var samples = new float[count];
            foreach (var tone in tones) {
                int first = Math.Max(offset, (int)(tone.Start * Rate));
                int end = Math.Min(offset + count, (int)((tone.End + .35) * Rate));
                if (end <= first) continue;
                work += end - first;
                if (work > 120000000) throw new InvalidDataException("MIDI polyphony exceeds the preview limit.");
                double frequency = 440 * Math.Pow(2, (tone.Pitch - 69) / 12d);
                for (int i = first; i < end; i++) {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    double age = i / (double)Rate - tone.Start;
                    double release = Math.Max(0, i / (double)Rate - tone.End);
                    double envelope = Math.Min(1, age / .006) * Math.Exp(-age * 1.4) * Math.Exp(-release * 22);
                    double phase = 2 * Math.PI * frequency * age;
                    double wave = Math.Sin(phase);
                    if (frequency * 2 < Rate / 2) wave += .35 * Math.Sin(2 * phase) * Math.Exp(-age * 3);
                    if (frequency * 3 < Rate / 2) wave += .15 * Math.Sin(3 * phase) * Math.Exp(-age * 5);
                    samples[i - offset] += (float)(wave * envelope * tone.Velocity / 127d * .22);
                }
            }
            var bytes = new byte[count * 2];
            for (int i = 0; i < count; i++) {
                // Fixed, continuous soft limiting avoids a full-song peak-analysis pass and
                // avoids per-chunk gain changes at boundaries.
                short sample = (short)(Math.Tanh(samples[i]) * .95 * 32767);
                bytes[i * 2] = (byte)sample; bytes[i * 2 + 1] = (byte)(sample >> 8);
            }
            yield return new PcmChunk(bytes, Rate, 1, totalFrames);
        }
    }
}
