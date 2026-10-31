using System.Net;
using System.Text;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.Music;

static void Check(bool success, string message) { if (!success) throw new Exception(message); }
var request = new MusicCompositionRequest { Description = "Welcome, tender, heroic, then an ascending major sixth", Bars = 2, Tempo = 96 };
const string valid = "tempo=96 4/4 key=Am\n| A4 C5 E5 D5 | / A4 F#5: E5 |";
Check(ResonatorNotationValidator.Validate(valid, request).Count == 0, "Valid score rejected");
foreach (string invalid in new[] {
    valid.Replace("A4 C5 E5 D5", "- C5 E5 D5"),
    valid.Replace("A4 C5 E5 D5", "A4 _ - D5"),
    valid.Replace("A4 C5 E5 D5", "payoff(A4) C5 E5 D5"),
    valid.Replace("A4 C5 E5 D5", "[Am] C5 E5 D5"),
    valid.Replace("A4 C5 E5 D5", "A4:v128 C5 E5 D5"),
    valid.Replace("A4 C5 E5 D5", "A4@-12t C5 E5 D5"),
    "```text\n" + valid + "\n```"
}) Check(ResonatorNotationValidator.Validate(invalid, request).Count > 0, "Invalid score accepted: " + invalid);
Check(ResonatorNotationValidator.Validate("tempo=96 4/4 key=Am | p [A3 C4 E4]: A4,@+1/8q:85%:v96> B4, C5 | A4:: |", request).Count == 0, "Chord/offset/gate/dynamic parsing");
var sixEight = new MusicCompositionRequest { Description = "Tender", Bars = 1, Tempo = 96, Meter = "6/8" };
Check(ResonatorNotationValidator.Validate("tempo=96 6/8 key=Am | A4, C5, E5, D5, C5, A4, |", sixEight).Count == 0, "6/8 timing");
var library = new InstructionLibrary(args[0]);

const string reported = "tempo=96 4/4 key=C major\n| C4 E4 G4 | /\n| C5 E5 G5 | /\n| F4 A4 C5 | /\n| G4 B4 D5 | /\n| C5 E5 G5 | /\n| F4 A4 C5 | /\n| G4 B4 D5 | /\n| C5 E5 G5 | |";
var reportedRequest = new MusicCompositionRequest { Description = "Hello, happy, epic", Bars = 8 };
var reportedErrors = ResonatorNotationValidator.Validate(reported, reportedRequest);
Check(reportedErrors.Any(x => x.Contains("Unsupported event major")), "Header must not hide the eight short bars");
const string corrected = "tempo=96 4/4 key=C | C4 E4 G4: / | C5 E5 G5: / | F4 A4 C5: / | G4 B4 D5: / | C5 E5 G5: / | F4 A4 C5: / | G4 B4 D5: / | C5 E5 G5: |";
Check(ResonatorNotationValidator.Validate(corrected, reportedRequest).Count == 0, "Corrected eight-bar notation rejected");
var regressionModel = new FakeModel(reported, corrected);
await new MusicCompositionService(regressionModel, library).ComposeAsync(reportedRequest, CancellationToken.None);
Check(regressionModel.Repair.Contains("Unsupported event major") && regressionModel.Repair.Contains("Bar separators | are optional"), "Repair prompt must explain all reported failures");
try { await new MusicCompositionService(new FakeModel(reported, reported), library).ComposeAsync(reportedRequest, CancellationToken.None); throw new Exception("Invalid score accepted"); }
catch (MusicGenerationException e) { Check(e.Message.Contains("Unsupported event major"), "Final error hides validation details"); }

var mock = new FakeModel("not music", valid);
request.ExistingNotation = valid; request.OriginalBrief = "Welcome";
var composer = new MusicCompositionService(mock, library);
var result = await composer.ComposeAsync(request, CancellationToken.None);
Check(result.Attempts == 2 && result.Notation == valid, "Repair must replace entire notation");
Check(mock.Prompt.Contains("Reintroducing the original dissonant note") && mock.Prompt.Contains("## Major 6th — 9 semitones") && mock.Prompt.Contains("Exact attack-time offset"), "Long notation references and concise AHD included");
Check(!mock.Prompt.Contains("# 3. Horizontal emotion") && mock.Prompt.Contains("REFERENCE: resonator_api_v0.1.md") && mock.Prompt.Contains("REFERENCE: resonator_note_string_language.md"), "Expected section-2 guide and long notation references");
Check(mock.User.Contains("ExistingNotation") && mock.User.Contains("Welcome"), "Revision context included");
try { await new MusicCompositionService(new FakeModel("bad", "still bad"), library).ComposeAsync(request, CancellationToken.None); throw new Exception("Bad generation accepted"); } catch (MusicGenerationException) { }
using (var cancelled = new CancellationTokenSource()) {
    cancelled.Cancel();
    try { await composer.ComposeAsync(request, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
}
using (var lateCancel = new CancellationTokenSource()) {
    var lateModel = new FakeModel(valid) { BeforeReply = () => lateCancel.Cancel() };
    try { await new MusicCompositionService(lateModel, library).ComposeAsync(request, lateCancel.Token); throw new Exception("Late result survived cancellation"); }
    catch (OperationCanceledException) { }
}
string folder = Path.Combine(Path.GetTempPath(), "music-checks-" + Guid.NewGuid());
try {
    var store = new SongWorkStore(folder);
    Check(store.Load() == null, "New song store not empty");
    store.Save(new SongWork { Notation = valid, OriginalBrief = "Welcome" });
    Check(new SongWorkStore(folder).Load()!.Notation == valid, "Song did not survive reopening");
    store.Save(new SongWork { Notation = valid.Replace("D5", "B4"), OriginalBrief = "Welcome" });
    Check(store.Load()!.Notation.Contains("B4"), "Atomic replacement failed");
    store.Clear(); Check(new SongWorkStore(folder).Load() == null, "Cleared song reappeared");
} finally { Directory.Delete(folder, true); }
// Small valid type-0 SMF, one empty track ending immediately.
byte[] smf = Convert.FromHexString("4D546864000000060000000101E04D54726B0000000400FF2F00");
foreach (string mode in new[] { "Text", "JsonString", "JsonProperty" }) {
    var transport = new FakeHandler(smf);
    using var http = new HttpClient(transport);
    var midi = new ResonatorMidiClient(http, new ResonatorMidiOptions { Enabled = true, EndpointUrl = "http://localhost/render", BodyMode = mode, NotationProperty = "score" });
    byte[] bytes = await midi.RenderAsync(new MusicRenderRequest { Notation = valid, Bars = 2 }, CancellationToken.None);
    Check(bytes.SequenceEqual(smf), "MIDI bytes changed");
    Check(mode != "Text" || transport.Body == valid, "Notation text changed in transit");
    Check(mode != "JsonProperty" || transport.Body.Contains("score"), "Configured JSON property ignored");
}
using (var http = new HttpClient(new FakeHandler(smf))) {
    try { await new ResonatorMidiClient(http, new ResonatorMidiOptions { Enabled = false }).RenderAsync(new MusicRenderRequest(), CancellationToken.None); throw new Exception("Unconfigured renderer called"); }
    catch (MidiRenderNotConfiguredException) { }
}
using (var http = new HttpClient(new FakeHandler(Encoding.UTF8.GetBytes("{\"url\":\"not-a-midi-file\"}")))) {
    try { await new ResonatorMidiClient(http, new ResonatorMidiOptions { Enabled = true, EndpointUrl = "http://localhost/render", BodyMode = "Text" }).RenderAsync(new MusicRenderRequest { Notation = valid, Bars = 2 }, CancellationToken.None); throw new Exception("JSON renderer response accepted as MIDI"); }
    catch (InvalidDataException) { }
}
var profile = Resonator.Core.ProfileLoader.Load(args[1]);
const string sparse = "tempo=96 4/4 key=C\n| C4 E4 G4 C5 / | E4 G4 C5 E5 / | G4 C5 E4 G4 / | C5 E5 G4 C4 / | C4 G4 C5 E5 / E5 G4 C5 E4 / G4 C5 E4 G4 / C5 E5 G4 C4 |";
foreach (var score in new[] { sparse, sparse.Replace("|", ""), sparse.Replace(" | ", "|") }) {
    Check(ResonatorNotationValidator.Validate(score, reportedRequest).Count == 0, "Optional separators rejected");
    var actual = new Resonator.Core.ResonatorParser(profile).Parse(score);
    Check(actual.Composition.LengthTicks == 32L * profile.Ppq, "Optional separators changed MIDI length");
    var events = actual.Composition.Events.OfType<Resonator.Core.NoteEvent>().ToArray();
    Check(events.Length == 32 && events.Select((n, i) => n.Tick == (long)i * profile.Ppq).All(x => x), "Optional separators changed note ordering/timing");
}
Check(ResonatorNotationValidator.Validate(sparse.Replace("C4 E4 G4 C5", "C4 E4 G4"), reportedRequest).Count == 0, "Short valid score rejected");
Check(ResonatorNotationValidator.Validate("tempo=96 4/4 key=C C4 C4:: C4 C4 C4", request).Count == 0, "Cross-bar sustained note rejected");

// Preserve arbitrary generated length and round UP only with trailing rests.
foreach (var example in new[] {
    (score: "tempo=96 4/4 key=C C3 E3 G3", meter: "4/4", bars: 1, beats: 4),
    (score: "tempo=96 4/4 key=C C3:: C4", meter: "4/4", bars: 2, beats: 8),
    (score: "tempo=96 6/8 key=C C3 E3,", meter: "6/8", bars: 1, beats: 3),
    (score: "tempo=96 3/16 key=C C3.", meter: "3/16", bars: 1, beats: 0),
}) {
    var spec = new MusicCompositionRequest { Description = "Any length", Bars = 8, Meter = example.meter };
    string padded = ResonatorNotationValidator.CompleteFinalMeasure(example.score, spec, out int actualBars);
    Check(actualBars == example.bars && padded.StartsWith(example.score), "Padding changed original events or bar count");
    Check(ResonatorNotationValidator.CompleteFinalMeasure(padded, spec, out _) == padded, "Padding repeated on rerender");
    var original = new Resonator.Core.ResonatorParser(profile).Parse(example.score).Composition;
    var completed = new Resonator.Core.ResonatorParser(profile).Parse(padded).Composition;
    Check(original.Events.OfType<Resonator.Core.NoteEvent>().SequenceEqual(completed.Events.OfType<Resonator.Core.NoteEvent>()), "Notes changed during measure completion");
    var meterParts = example.meter.Split('/');
    Check(completed.LengthTicks == actualBars * int.Parse(meterParts[0]) * 4L * profile.Ppq / int.Parse(meterParts[1]), "Not padded to whole measure");
}
var flexibleModel = new FakeModel("tempo=96 4/4 key=C C3 E3 G3");
var flexible = await new MusicCompositionService(flexibleModel, library).ComposeAsync(reportedRequest, CancellationToken.None);
Check(flexible.Attempts == 1 && flexible.ActualBars == 1 && flexible.Notation.EndsWith(" _"), "Short valid generation must play without a repair");
Check(flexibleModel.User.Contains("length target"), "Prompt still requires exact duration");
var parsed = new Resonator.Core.ResonatorParser(profile).Parse(valid);
var realNotes = parsed.Composition.Events.OfType<Resonator.Core.NoteEvent>().ToArray();
Check(parsed.Composition.Tempo == 96 && parsed.Composition.Numerator == 4 && parsed.Composition.Denominator == 4, "Real API meter parsing");
Check(parsed.Composition.LengthTicks == profile.Ppq * 8L, "Real API rhythmic length");
Check(realNotes[5].MidiNote - realNotes[4].MidiNote == 9, "Actual MIDI interval is not an ascending major sixth");
Check(realNotes[0].MidiNote == 81, "Shipped VSL octave mapping was not loaded");
var precision = new Resonator.Core.ResonatorParser(profile).Parse("tempo=96 4/4 | A3,@+1/8q:85% B3, C4 D4 E4 |");
var precisionNotes = precision.Composition.Events.OfType<Resonator.Core.NoteEvent>().ToArray();
Check(precisionNotes[0].Tick == 60 && precisionNotes[1].Tick == 240, "Fractional onset shifted the rhythmic grid");
Check(precisionNotes[0].DurationTicks == 204, "Gate length differs from parser contract");
if (args.Length > 2) {
    using var liveHttp = new HttpClient();
    var adapter = new ResonatorMidiClient(liveHttp, new ResonatorMidiOptions { EndpointUrl = args[2] });
    byte[] actual = await adapter.RenderAsync(new MusicRenderRequest { Notation = valid, Bars = 2 }, CancellationToken.None);
    Check(actual.Length > 40 && Encoding.ASCII.GetString(actual, 0, 4) == "MThd", "Real HTTP API did not return MIDI");
    File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "six-stars-music-integration.mid"), actual);
}
Console.WriteLine("PASS: notation/bars/modifiers, full references, revision context, repair, cancellation, persistent save/replace/clear, MIDI body modes, disabled renderer and non-MIDI rejection.");

sealed class FakeModel(params string[] replies) : ILocalChatModelClient {
    private int _index;
    public string Prompt = "", User = "", Repair = "";
    public Action? BeforeReply;
    public Task<string> CompleteTextStreamingAsync(IReadOnlyList<ChatMessage> messages, int? maxTokens, string debugLabel, Action<string>? onTextDelta, CancellationToken token) {
        Prompt = messages[0].Content; User = messages[1].Content; if (messages.Count > 2) Repair = messages[messages.Count - 1].Content;
        BeforeReply?.Invoke();
        return Task.FromResult(replies[_index++]);
    }
    public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, CancellationToken token) => throw new NotSupportedException();
    public Task<string> CompleteJsonAsync(IReadOnlyList<ChatMessage> messages, object? responseFormat, int? maxTokens, string debugLabel, CancellationToken token) => throw new NotSupportedException();
}
sealed class FakeHandler(byte[] bytes) : HttpMessageHandler {
    public string Body = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        Body = await request.Content!.ReadAsStringAsync(token);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
}
