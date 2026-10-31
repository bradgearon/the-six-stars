using NAudio.Wave;
using Wds.SixStars.Api.Voice;
using Wds.SixStars.Ui.Audio;

static byte[] Wav(short value)
{
    using var memory = new MemoryStream();
    using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(48000, 16, 1)))
    {
        writer.Write(BitConverter.GetBytes(value));
    }
    return memory.ToArray();
}
static VoiceSynthesisResult Voice(string star) => new() { StarId = star, WavAudio = Wav(10), VoiceId = "clone", Source = "tts" };
static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
static short[] Samples(byte[] wav)
{
    using var reader = new WaveFileReader(new MemoryStream(wav));
    using var pcm = new MemoryStream(); reader.CopyTo(pcm);
    byte[] bytes = pcm.ToArray();
    return Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2)).ToArray();
}

var processor = new FakeProcessor(Wav(100), Wav(200));
var sink = new Sink();
var endpoint = new BenaveilProcessingPlaybackEndpoint(sink, processor);
var other = Voice("Elusiaa");
await endpoint.PlayWavAsync(other, default);
Check(processor.Begins == 0 && ReferenceEquals(sink.Audio.Single(), other), "Other Stars must bypass VST unchanged.");
sink.Audio.Clear(); processor.Enabled = false;
var dry = Voice("Benaveil");
await endpoint.PlayWavAsync(dry, default);
Check(processor.Begins == 0 && ReferenceEquals(sink.Audio.Single(), dry), "Disabled Benaveil must use original WAV.");
sink.Audio.Clear(); processor.Enabled = true;
await endpoint.PlayWavAsync(dry, default);
Check(Samples(sink.Audio.Single().WavAudio).SequenceEqual(new short[] {100, 200}), "One-shot output must concatenate effected body and tail under one WAV header.");
Check(Samples(dry.WavAudio).Single() == 10, "Processing must not mutate cached TTS input.");
sink.Audio.Clear();
using (var session = endpoint.CreateStreamingSession("test"))
{
    await session.EnqueueAsync(Voice("benaveil"), default);
    await session.EnqueueAsync(Voice("benaveil"), default);
    await session.CompleteAsync(default);
    Check(sink.Audio.SelectMany(a => Samples(a.WavAudio)).SequenceEqual(new short[] {100, 100, 200}), "Streaming order must be body/body/tail.");
    Check(processor.Begins == 2 && processor.Processed == 3 && processor.Ends == 2, "One VST lease per line, not per chunk.");
    Check(sink.Completed, "Sink must complete after tail delivery.");
}
sink.Audio.Clear(); processor.Fail = true;
using (var session = endpoint.CreateStreamingSession("failure"))
{
    Exception? originalFailure = null;
    try { await session.EnqueueAsync(Voice("Benaveil"), default); } catch (InvalidOperationException ex) { originalFailure = ex; }
    for (int retry = 0; retry < 3; retry++)
    {
        try { await session.EnqueueAsync(Voice("Benaveil"), default); }
        catch (Exception ex) { Check(ReferenceEquals(ex, originalFailure), "Repeated enqueue must preserve the first exception without nesting."); }
    }
    bool failed = false;
    try { await session.CompleteAsync(default); } catch (InvalidOperationException) { failed = true; }
    Check(failed && sink.Audio.Count == 0 && sink.Cancelled, "A swallowed chunk error must fail completion, not deliver dry audio.");
}
processor.Fail = false;
using (var session = endpoint.CreateStreamingSession("cancel"))
{
    session.Cancel();
    bool cancelled = false;
    try { await session.EnqueueAsync(Voice("Benaveil"), default); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "Cancelled sessions must not process more chunks.");
}
Check(processor.Disposed == processor.Begins, "Every acquired VST session must be released.");
Console.WriteLine("PASS: bypass, one-shot WAV/tail, immutable source, streaming continuity, failure propagation, cancellation, lease cleanup.");

// The same generator is reused after a failed Benaveil request and then switched to Elusiaa.
var raw = new ScriptedGenerator(Wav(10));
var effect = new FakeProcessor(Wav(100), Wav(200));
using var generated = new BenaveilProcessingVoiceGenerator(raw, effect);
var sent = new List<AudioChunkEventArgs>();
GenerationCompleteEventArgs? completed = null;
generated.AudioChunkReady += (_, chunk) => { sent.Add(chunk); raw.Release.TrySetResult(); };
generated.GenerationComplete += (_, done) => completed = done;
raw.WaitForConsumer = true;
await generated.GenerateStreamingAsync(new VoiceSynthesisRequest { StarId = "Benaveil" }, default).WaitAsync(TimeSpan.FromSeconds(5));
Check(sent.Count == 3 && completed?.TotalChunks == 3, "Client completion must include the VST tail.");
Check(sent.Select(c => c.ChunkIndex).SequenceEqual(new[] {0,1,2}), "Processed client indices must be contiguous.");
Check(sent.SelectMany(c => Samples(c.AudioData)).SequenceEqual(new short[] {100,100,200}), "Clients must receive effected chunks before reassembly.");
Check(effect.Begins == 1, "VST instance must span all outgoing chunks.");
effect.Fail = true; raw.WaitForConsumer = false;
try { await generated.GenerateStreamingAsync(new VoiceSynthesisRequest { StarId = "Benaveil" }, default); } catch (InvalidOperationException) { }
sent.Clear();
await generated.GenerateStreamingAsync(new VoiceSynthesisRequest { StarId = "Elusiaa" }, default);
Check(sent.Count == 2 && sent.All(c => c.StarId == "Elusiaa" && Samples(c.AudioData).Single() == 10),
    "Elusiaa after failed Benaveil must bypass the effect and retain her StarId without restarting.");
Console.WriteLine("PASS: processed client events before source completion, tail/count metadata, failed Benaveil to Elusiaa reuse.");

var failedSink = new Sink { Fail = true };
var nativePipeline = new StreamingStarVoiceResponsePipeline(new ScriptedGenerator(Wav(10)), failedSink);
using var service = new Wds.SixStars.Ui.Services.DesktopVoiceOutputService(true, nativePipeline, null, failedSink);
var ben = new Wds.SixStars.Api.Models.StarInteractionResponse { StarId = "Benaveil" };
ben.SetSpokenParts(new[] { "test" });
try { await service.SpeakAsync("test", ben, default).WaitAsync(TimeSpan.FromSeconds(5)); }
catch (InvalidOperationException ex) { Check(ex.Message == "Device failed", "Pipeline should retain the device failure."); }
failedSink.Fail = false;
var elusiaa = new Wds.SixStars.Api.Models.StarInteractionResponse { StarId = "Elusiaa" };
elusiaa.SetSpokenParts(new[] { "test" });
await service.SpeakAsync("test", elusiaa, default).WaitAsync(TimeSpan.FromSeconds(5));
Check(failedSink.Audio.Count == 2 && failedSink.Audio.All(a => a.StarId == "Elusiaa"), "Desktop pipeline must recover and use the new Star after failure.");
Console.WriteLine("PASS: actual desktop pipeline failure then Elusiaa playback without restart.");

sealed class FakeProcessor(byte[] body, byte[] tail) : IBenaveilVoiceProcessor
{
    public bool Enabled { get; set; } = true;
    public bool Fail;
    public int Begins, Processed, Ends, Disposed;
    public Task<IBenaveilVoiceSession> BeginAsync(CancellationToken token)
    { Begins++; return Task.FromResult<IBenaveilVoiceSession>(new Session(this, body, tail)); }
    private sealed class Session(FakeProcessor owner, byte[] body, byte[] tail) : IBenaveilVoiceSession
    {
        public Task<byte[]> ProcessAsync(byte[] wav, CancellationToken token)
        {
            if (owner.Fail) throw new InvalidOperationException("Simulated native processing failure");
            owner.Processed++; return Task.FromResult(body);
        }
        public Task<byte[]> EndAsync(CancellationToken token) { owner.Ends++; return Task.FromResult(tail); }
        public void Dispose() { owner.Disposed++; }
    }
}
sealed class Sink : IWavePlaybackEndpoint, IStreamingWavePlaybackEndpoint, IAudioOutputKeepAliveEndpoint
{
    public readonly List<VoiceSynthesisResult> Audio = new();
    public bool Completed, Cancelled, Fail;
    public Task PlayWavAsync(VoiceSynthesisResult audio, CancellationToken token) { if (Fail) throw new InvalidOperationException("Device failed"); Audio.Add(audio); return Task.CompletedTask; }
    public IStreamingWavePlaybackSession CreateStreamingSession(string reason) => new Session(this);
    public IDisposable StartOutputKeepAlive(string reason, CancellationToken token) => new Session(this);
    private sealed class Session(Sink owner) : IStreamingWavePlaybackSession
    {
        public Task EnqueueAsync(VoiceSynthesisResult audio, CancellationToken token) => owner.PlayWavAsync(audio, token);
        public Task CompleteAsync(CancellationToken token) { owner.Completed = true; return Task.CompletedTask; }
        public void Cancel() => owner.Cancelled = true;
        public void Dispose() { }
    }
}

sealed class ScriptedGenerator(byte[] wav) : IStreamingVoiceGenerator
{
    public bool WaitForConsumer;
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsConnected => true;
    public event EventHandler<AudioChunkEventArgs>? AudioChunkReady;
    public event EventHandler<GenerationCompleteEventArgs>? GenerationComplete;
    public event EventHandler<GenerationErrorEventArgs>? GenerationError { add {} remove {} }
    public async Task GenerateStreamingAsync(VoiceSynthesisRequest request, CancellationToken token)
    {
        for (int i = 0; i < 2; i++)
        {
            token.ThrowIfCancellationRequested();
            AudioChunkReady?.Invoke(this, new AudioChunkEventArgs { ChunkIndex = i, TotalChunks = 2,
                StarId = request.StarId, VoiceId = request.StarId, AudioData = wav, IsStreamingPcmWindow = true });
            if (WaitForConsumer && i == 0) await Release.Task.WaitAsync(token);
        }
        GenerationComplete?.Invoke(this, new GenerationCompleteEventArgs { StarId = request.StarId, TotalChunks = 2 });
    }
    public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
    public Task DisconnectAsync() => Task.CompletedTask;
    public void CancelGeneration() { }
    public void Dispose() { }
}
