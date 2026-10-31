using System.Text;

namespace Resonator.Core;

public static class MidiFileWriter
{
    private sealed record RawEvent(long Tick, int Priority, byte[] Data);

    public static byte[] Write(ResonatorComposition composition, ResonatorProfile profile)
    {
        var events = new List<RawEvent>();

        int microsPerQuarter = (int)Math.Round(60_000_000.0 / composition.Tempo);
        events.Add(new RawEvent(0, -100, [0xFF, 0x51, 0x03,
            (byte)((microsPerQuarter >> 16) & 0xFF),
            (byte)((microsPerQuarter >> 8) & 0xFF),
            (byte)(microsPerQuarter & 0xFF)]));

        int denominatorPower = 0;
        int den = Math.Max(1, composition.Denominator);
        while ((1 << denominatorPower) < den && denominatorPower < 7) denominatorPower++;
        events.Add(new RawEvent(0, -99, [0xFF, 0x58, 0x04,
            (byte)Math.Clamp(composition.Numerator, 1, 255),
            (byte)denominatorPower, 24, 8]));

        byte[] trackName = Encoding.UTF8.GetBytes("Resonator");
        events.Add(new RawEvent(0, -98, [0xFF, 0x03, (byte)trackName.Length, .. trackName]));

        var bentChannels = composition.Events.OfType<NoteEvent>()
            .Where(n => Math.Abs(n.Cents) > 0.0001 || n.BendTargetCents.HasValue)
            .Select(n => n.Channel)
            .Distinct()
            .ToArray();

        foreach (int channel in bentChannels)
            AddPitchBendRange(events, channel, profile.PitchBendRangeSemitones);

        foreach (var cc in composition.Events.OfType<ControlChangeEvent>())
        {
            events.Add(new RawEvent(cc.Tick, 1,
                [(byte)(0xB0 | ClampChannel(cc.Channel)), (byte)Math.Clamp(cc.Controller, 0, 127), (byte)Math.Clamp(cc.Value, 0, 127)]));
        }

        foreach (var note in composition.Events.OfType<NoteEvent>())
        {
            int channel = ClampChannel(note.Channel);
            long end = note.Tick + Math.Max(1, note.DurationTicks);

            if (Math.Abs(note.Cents) > 0.0001 || note.BendTargetCents.HasValue)
            {
                int initialBend = CentsToPitchBend(note.Cents, profile.PitchBendRangeSemitones);
                events.Add(new RawEvent(note.Tick, 0, PitchBendMessage(channel, initialBend)));

                if (note.BendTargetCents.HasValue)
                {
                    const int steps = 12;
                    for (int i = 1; i <= steps; i++)
                    {
                        double amount = i / (double)steps;
                        double cents = note.Cents + (note.BendTargetCents.Value - note.Cents) * amount;
                        long tick = note.Tick + (long)Math.Round(note.DurationTicks * amount);
                        if (tick >= end) tick = Math.Max(note.Tick, end - 1);
                        int bend = CentsToPitchBend(cents, profile.PitchBendRangeSemitones);
                        events.Add(new RawEvent(tick, 1, PitchBendMessage(channel, bend)));
                    }
                }
            }

            events.Add(new RawEvent(note.Tick, 3,
                [(byte)(0x90 | channel), (byte)Math.Clamp(note.MidiNote, 0, 127), (byte)Math.Clamp(note.Velocity, 1, 127)]));
            events.Add(new RawEvent(end, -2,
                [(byte)(0x80 | channel), (byte)Math.Clamp(note.MidiNote, 0, 127), 0]));

            if (Math.Abs(note.Cents) > 0.0001 || note.BendTargetCents.HasValue)
                events.Add(new RawEvent(end, -1, PitchBendMessage(channel, 8192)));
        }

        events.Sort((a, b) =>
        {
            int c = a.Tick.CompareTo(b.Tick);
            return c != 0 ? c : a.Priority.CompareTo(b.Priority);
        });

        using var trackStream = new MemoryStream();
        long lastTick = 0;
        foreach (var evt in events)
        {
            long delta = Math.Max(0, evt.Tick - lastTick);
            WriteVlq(trackStream, delta);
            trackStream.Write(evt.Data);
            lastTick = evt.Tick;
        }

        long targetEnd = Math.Max(composition.LengthTicks, lastTick);
        WriteVlq(trackStream, Math.Max(0, targetEnd - lastTick));
        trackStream.Write([0xFF, 0x2F, 0x00]);

        byte[] track = trackStream.ToArray();
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("MThd"));
        WriteInt32BigEndian(output, 6);
        WriteInt16BigEndian(output, 0); // Format 0: one track, multiple MIDI channels allowed.
        WriteInt16BigEndian(output, 1);
        WriteInt16BigEndian(output, composition.Ppq);
        output.Write(Encoding.ASCII.GetBytes("MTrk"));
        WriteInt32BigEndian(output, track.Length);
        output.Write(track);
        return output.ToArray();
    }

    private static void AddPitchBendRange(List<RawEvent> events, int channel, int semitones)
    {
        channel = ClampChannel(channel);
        byte status = (byte)(0xB0 | channel);
        int range = Math.Clamp(semitones, 1, 24);
        events.Add(new RawEvent(0, -95, [status, 101, 0])); // RPN MSB
        events.Add(new RawEvent(0, -94, [status, 100, 0])); // RPN LSB
        events.Add(new RawEvent(0, -93, [status, 6, (byte)range]));
        events.Add(new RawEvent(0, -92, [status, 38, 0]));
        events.Add(new RawEvent(0, -91, [status, 101, 127]));
        events.Add(new RawEvent(0, -90, [status, 100, 127]));
    }

    private static byte[] PitchBendMessage(int channel, int value)
    {
        value = Math.Clamp(value, 0, 16383);
        return [(byte)(0xE0 | ClampChannel(channel)), (byte)(value & 0x7F), (byte)((value >> 7) & 0x7F)];
    }

    private static int CentsToPitchBend(double cents, int rangeSemitones)
    {
        double rangeCents = Math.Max(1, rangeSemitones) * 100.0;
        double normalized = Math.Clamp(cents / rangeCents, -1.0, 1.0);
        return (int)Math.Round(8192 + normalized * 8191);
    }

    private static int ClampChannel(int channel) => Math.Clamp(channel, 0, 15);

    private static void WriteVlq(Stream stream, long value)
    {
        value = Math.Max(0, value);
        Span<byte> buffer = stackalloc byte[10];
        int index = buffer.Length - 1;
        buffer[index] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0)
        {
            index--;
            buffer[index] = (byte)((value & 0x7F) | 0x80);
        }
        stream.Write(buffer[index..]);
    }

    private static void WriteInt16BigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static void WriteInt32BigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }
}
