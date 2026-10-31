using NAudio.Wave;
using Wds.SixStars.Ui.Audio;
using Wds.SixStars.Ui.Services;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static MidiPianoWaveRenderer.PcmChunk Chunk(int n) => new(Enumerable.Repeat((byte)(n + 1),12000).ToArray(),24000,1,48000);

// Real adaptive session, delayed producer and a fake device consuming actual PCM bytes.
var device = new FakeOutput();
using (var service = new DesktopMusicPlaybackService(() => device))
{
    IEnumerable<MidiPianoWaveRenderer.PcmChunk> Delayed()
    {
        for (int i = 0; i < 4; i++) yield return Chunk(i);
        Check(device.Started.Wait(3000), "Did not start before producer completion");
        Check(device.Paused.Wait(4000), "Did not pause on producer stall");
        Thread.Sleep(120);
        Check(device.PlaybackState == PlaybackState.Paused, "Resumed without refill");
        for (int i = 4; i < 8; i++) yield return Chunk(i);
    }
    await service.PlayChunksAsync(Delayed(), Environment.TickCount64 - 5000, default).WaitAsync(TimeSpan.FromSeconds(10));
    Check(device.Plays >= 2, "Did not resume after refill");
    Check(device.Captured.ToArray().SequenceEqual(Enumerable.Range(0,8).SelectMany(i=>Chunk(i).Pcm)), "Samples lost/repeated/reordered at refill");
}
Console.WriteLine("PASS: playback before producer completion; stalled producer pauses and refills; exact PCM continuity.");

// Music must not use speech's short idle-phrase startup escape.
var shortDevice = new FakeOutput();
using (var session = new SmartDirectSoundPcmPlaybackSession(new(500), "check", outputFactory: () => shortDevice, musicMode:true, requestStartedAtMs:Environment.TickCount64-5000))
{
    await session.EnqueuePcmAsync(Chunk(0).Pcm,new WaveFormat(24000,16,1),default);
    await Task.Delay(400);
    Check(!shortDevice.Started.IsSet,"Music started on idle before its adaptive reserve was ready");
    await session.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
    Check(shortDevice.Captured.Count == 12000,"Short final music was cut off");
}
Console.WriteLine("PASS: latency-based startup waits; short completed input drains fully.");

var frozen = new FakeOutput { Consume = false };
using (var service = new DesktopMusicPlaybackService(() => frozen))
{
    int produced=0;
    IEnumerable<MidiPianoWaveRenderer.PcmChunk> LongSong()
    { for(int i=0;i<100;i++){ Interlocked.Increment(ref produced); yield return Chunk(i); } }
    var running=service.PlayChunksAsync(LongSong(),Environment.TickCount64,default);
    Check(frozen.Started.Wait(3000),"No playback for long song");
    await Task.Delay(100);
    Check(produced <= 12, "Producer ignored bounded lookahead");
    service.Stop();
    try { await running.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Cancelled playback succeeded"); }
    catch(OperationCanceledException) { }
}
Console.WriteLine("PASS: bounded synthesis lookahead and cancellation while producer is blocked.");

using (var service = new DesktopMusicPlaybackService(() => new FakeOutput { FailInit = true }))
{
    try { await service.PlayChunksAsync(Enumerable.Range(0,4).Select(Chunk),Environment.TickCount64,default).WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Device failure hidden"); }
    catch(InvalidOperationException e) when(e.Message == "device failure") { }
}
Console.WriteLine("PASS: output failure propagates to caller.");

var oldDevice = new FakeOutput { Consume = false };
var newDevice = new FakeOutput();
int opened = 0;
using (var service = new DesktopMusicPlaybackService(() => Interlocked.Increment(ref opened)==1 ? oldDevice : newDevice))
{
    var previous = service.PlayChunksAsync(Enumerable.Range(0,40).Select(Chunk),Environment.TickCount64,default);
    Check(oldDevice.Started.Wait(3000),"Previous session never started");
    var next = service.PlayChunksAsync(new[]{Chunk(10)},Environment.TickCount64,default);
    try { await previous.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Previous session survived replay"); }
    catch(OperationCanceledException) { }
    await next.WaitAsync(TimeSpan.FromSeconds(3));
    Check(newDevice.Captured.SequenceEqual(Chunk(10).Pcm),"Replay includes stale samples");
}
Console.WriteLine("PASS: replay cancels the previous session and starts with fresh PCM.");

sealed class FakeOutput : IWavePlayer
{
    private IWaveProvider? provider;
    private volatile bool disposed;
    private volatile PlaybackState state;
    public bool Consume=true, FailInit;
    public int Plays;
    public readonly ManualResetEventSlim Started=new(), Paused=new();
    public readonly List<byte> Captured=new();
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState => state;
    public float Volume {get;set;}=1;
    public WaveFormat OutputWaveFormat => provider!.WaveFormat;
    public void Init(IWaveProvider source)
    {
        if(FailInit) throw new InvalidOperationException("device failure");
        provider=source;
        Task.Run(async()=> {
            while(!disposed) {
                if(Consume && state == PlaybackState.Playing && source is BufferedWaveProvider buffer) {
                    int size=Math.Min(480,buffer.BufferedBytes);
                    if(size>0){var bytes=new byte[size];int read=source.Read(bytes.AsSpan(0,size));lock(Captured) Captured.AddRange(bytes.Take(read));}
                }
                await Task.Delay(10);
            }
        });
    }
    public void Play(){state=PlaybackState.Playing;Interlocked.Increment(ref Plays);Started.Set();}
    public void Pause(){state=PlaybackState.Paused;Paused.Set();}
    public void Stop(){state=PlaybackState.Stopped;PlaybackStopped?.Invoke(this,new StoppedEventArgs());}
    public void Dispose(){disposed=true;state=PlaybackState.Stopped;}
}
