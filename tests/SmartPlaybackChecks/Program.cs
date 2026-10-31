using NAudio.Wave;
using Wds.SixStars.Ui.Audio;
using Wds.SixStars.Api.Voice;

var root = new InvalidOperationException("Original device initialization failure");
var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var memory = new MemoryStream();
using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(memory), new WaveFormat(48000, 16, 1)))
    writer.Write(new byte[48000 * 2 * 3]);
var voice = new VoiceSynthesisResult { WavAudio = memory.ToArray() };
using var session = new SmartDirectSoundPcmPlaybackSession(new DesktopSmartPcmAdaptiveProfile(), "regression", "wasapi",
    () => new BrokenPlayer(root, attempted));
await session.EnqueueAsync(voice, default);
await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
bool observed = false;
for (int i = 0; i < 100 && !observed; i++)
{
    try { await session.EnqueueAsync(voice, default); }
    catch (Exception ex)
    {
        if (!ReferenceEquals(ex, root)) throw new Exception("Enqueue replaced the original device failure.", ex);
        observed = true;
    }
    if (!observed) await Task.Delay(10);
}
if (!observed) throw new Exception("Device failure was not surfaced.");
try { await session.CompleteAsync(default); throw new Exception("Complete unexpectedly succeeded."); }
catch (Exception ex) when (ReferenceEquals(ex, root)) { }
Console.WriteLine("PASS: native startup failure preserved by Enqueue and Complete; no secondary stopped-session error.");

sealed class BrokenPlayer(Exception error, TaskCompletionSource attempted) : IWavePlayer
{
    public float Volume { get; set; }
    public PlaybackState PlaybackState => PlaybackState.Stopped;
    public WaveFormat OutputWaveFormat => new(48000,16,1);
    public event EventHandler<StoppedEventArgs>? PlaybackStopped { add {} remove {} }
    public void Init(IWaveProvider provider) { attempted.TrySetResult(); throw error; }
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Dispose() { }
}
