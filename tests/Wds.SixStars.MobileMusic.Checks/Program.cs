using Wds.SixStars.Api.Music;
using Wds.SixStars.Ui.Services;
using Resonator.Core;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var request = new MusicCompositionRequest { Bars = 8, Meter = "4/4" };
Check(MusicCompositionService.BuildDurationInstruction(request).Contains("about 32 quarter-note beats"), "4/4 duration prompt");
request.Meter = "6/8";
Check(MusicCompositionService.BuildDurationInstruction(request).Contains("about 24 quarter-note beats"), "6/8 duration prompt");
var result = new ResonatorMidiGenerator().Generate("tempo=120 4/4 key=C C3 E3 G3 C4", new ResonatorProfile());
byte[] wav = MidiPianoWaveRenderer.Render(result.MidiBytes, CancellationToken.None);
Check(System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF", "WAV header");
Check(BitConverter.ToInt32(wav, 24) == 24000 && BitConverter.ToInt16(wav, 34) == 16, "PCM output format");
Check(wav.Length > 24000 * 2 * 2 && wav.Length < 24000 * 2 * 3, "MIDI tempo/duration lost");
Check(wav.Skip(44).Any(x => x != 0), "Silent synthesis");
var chord = new ResonatorMidiGenerator().Generate("tempo=120 4/4 key=C [C3 E3 G3]: _ C4 -", new ResonatorProfile());
Check(MidiPianoWaveRenderer.Render(chord.MidiBytes, CancellationToken.None).Length > wav.Length, "Chord/hold rendering");
var padded = new ResonatorMidiGenerator().Generate("tempo=120 4/4 key=C C3 _ _ _", new ResonatorProfile());
byte[] paddedWav = MidiPianoWaveRenderer.Render(padded.MidiBytes, CancellationToken.None);
Check(paddedWav.Length > 24000 * 2 * 2, "Final-measure rests lost in phone WAV");
using var cancel = new CancellationTokenSource(); cancel.Cancel();
try { MidiPianoWaveRenderer.Render(result.MidiBytes, cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
try { MidiPianoWaveRenderer.Render(new byte[44], CancellationToken.None); throw new Exception("Invalid MIDI accepted"); } catch (Exception e) when (e.Message != "Invalid MIDI accepted") { }
var blocks = MidiPianoWaveRenderer.RenderChunks(result.MidiBytes, CancellationToken.None).ToArray();
Check(blocks.Length > 1 && blocks[0].Pcm.Length == 12000, "First complete 250ms chunk missing");
Check(blocks.SelectMany(b => b.Pcm).SequenceEqual(wav.Skip(44)), "Streaming boundaries changed audio samples");
using var wire = new MemoryStream();
bool progressive = false;
IEnumerable<MidiPianoWaveRenderer.PcmChunk> SlowProducer() {
    yield return blocks[0];
    progressive = wire.Length > blocks[0].Pcm.Length; // First chunk already delivered before requesting second.
    foreach (var block in blocks.Skip(1)) yield return block;
}
await MusicPcmStreamProtocol.WriteAsync(wire, SlowProducer(), CancellationToken.None);
Check(progressive, "Server buffered whole music before sending");
byte[] all = wire.ToArray();
int start = System.Text.Encoding.ASCII.GetString(all).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
using var reader = new BinaryReader(new MemoryStream(all, start, all.Length - start));
Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)) == "RSM1", "Protocol version");
Check(reader.ReadInt32() == 24000 && reader.ReadInt32() == 1, "Protocol format");
Check(reader.ReadInt32() == (wav.Length - 44) / 2, "Protocol total frames");
using var reassembled = new MemoryStream();
int seq = 0;
while (true) {
    Check(reader.ReadInt32() == seq++, "Server chunk sequence");
    int length = reader.ReadInt32();
    if (length == 0) break;
    reassembled.Write(reader.ReadBytes(length));
}
Check(reassembled.ToArray().SequenceEqual(wav.Skip(44)), "Protocol altered PCM");
File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "resone-stream-test.bin"), all.Skip(start).ToArray());
using var failed = new MemoryStream();
IEnumerable<MidiPianoWaveRenderer.PcmChunk> BrokenProducer() { yield return blocks[0]; throw new IOException("Injected rendering failure"); }
await MusicPcmStreamProtocol.WriteAsync(failed, BrokenProducer(), CancellationToken.None);
Check(failed.Length < all.Length, "Failure incorrectly completed stream");
Console.WriteLine("PASS: progressive first-chunk delivery, seamless PCM blocks, ordered protocol and failure truncation.");
Console.WriteLine("PASS: duration instructions, real Resonator MIDI to audible PCM WAV, tempo, chords, holds, cancellation and invalid input.");
