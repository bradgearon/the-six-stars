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
    try { await session.EnqueueAsync(Voice("Benaveil"), default); } catch (InvalidOperationException) { }
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
    public bool Completed, Cancelled;
    public Task PlayWavAsync(VoiceSynthesisResult audio, CancellationToken token) { Audio.Add(audio); return Task.CompletedTask; }
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
