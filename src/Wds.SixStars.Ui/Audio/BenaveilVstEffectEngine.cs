using System.IO;
using System.Text;
using NAudio.Vst3;
using NAudio.Wave;
using Wds.SixStars.VstProtocol;

namespace Wds.SixStars.Ui.Audio;

internal sealed class EffectEngine : IDisposable
{
    private Vst3Module? _module;
    private Vst3Plugin? _plugin;
    private VstRequest? _settings;
    private ChunkSource? _source;
    private Vst3EffectSampleProvider? _effect;
    private int _skipFrames;
    private bool _used;

    public VstReply Handle(VstRequest request)
    {
        switch (request.Command)
        {
            case "load":
                if (request.SampleRate < 8000 || request.SampleRate > 192000 || request.BlockSize < 64 || request.BlockSize > 8192
                    || !double.IsFinite(request.MaxTailSeconds) || request.MaxTailSeconds < 0 || request.MaxTailSeconds > 30)
                    throw new InvalidDataException("Invalid Benaveil sample rate, block size or tail duration.");
                Dispose();
                if (!File.Exists(request.PresetPath)) throw new FileNotFoundException("Benaveil preset was not found.", request.PresetPath);
                _settings = request;
                _module = Vst3Module.Load(request.PluginPath);
                CreatePlugin();
                return FormatReply();
            case "begin":
                if (_module == null || _settings == null) throw new InvalidOperationException("VocalSynth has not loaded.");
                // Use the prewarmed instance for the first line. Fresh instances after each use
                // keep delay/vocoder state from a cancelled or completed line out of the next one.
                if (_used) CreatePlugin();
                _used = true;
                _source = new ChunkSource(_plugin!.SampleRate, _plugin.InputChannelCount);
                _effect = new Vst3EffectSampleProvider(_source, _plugin)
                {
                    CompensateLatency = false, // trimmed once below without reading ahead across network chunks
                    MaxTailDuration = TimeSpan.FromSeconds(Math.Max(_settings.MaxTailSeconds,
                        _plugin.LatencySamples / (double)_plugin.SampleRate + 0.25)),
                    TailSilenceBlocks = Math.Max(4, checked((int)_plugin.LatencySamples / _plugin.MaxBlockSize + 4))
                };
                _skipFrames = checked((int)_plugin.LatencySamples);
                return FormatReply();
            case "chunk":
                RequireSession();
                if (request.Audio.Length % (sizeof(float) * _plugin!.InputChannelCount) != 0)
                    throw new InvalidDataException("Unaligned float PCM input.");
                float[] input = new float[request.Audio.Length / sizeof(float)];
                Buffer.BlockCopy(request.Audio, 0, input, 0, request.Audio.Length);
                _source!.SetChunk(input);
                return Render(input.Length / _plugin.InputChannelCount, false);
            case "end":
                RequireSession();
                VstReply tail = Render(0, true);
                _source = null;
                _effect = null;
                return tail;
            default: throw new InvalidDataException("Unknown VST command.");
        }
    }

    private void CreatePlugin()
    {
        _plugin?.Dispose();
        _plugin = null;
        string id = Vst3Preset.ReadClassId(_settings!.PresetPath);
        var info = _module!.GetClasses().SingleOrDefault(c => string.Equals(c.ClassId, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The Benaveil preset belongs to a different VST class than the configured module.");
        _plugin = _module.CreatePlugin(info, _settings.SampleRate, _settings.BlockSize);
        _plugin.LoadPreset(_settings.PresetPath);
        if (_plugin.InputChannelCount < 1 || _plugin.InputChannelCount > 2 || _plugin.OutputChannelCount < 1 || _plugin.OutputChannelCount > 2)
            throw new InvalidOperationException("Benaveil processing requires a mono/stereo effect input and output.");
    }

    private VstReply Render(int frames, bool tail)
    {
        using var pcm = new MemoryStream();
        using var writer = new BinaryWriter(pcm, Encoding.UTF8, true);
        int channels = _plugin!.OutputChannelCount;
        float[] buffer = new float[_plugin.MaxBlockSize * channels];
        while (tail || frames > 0)
        {
            int requested = tail ? _plugin.MaxBlockSize : Math.Min(frames, _plugin.MaxBlockSize);
            int read = _effect!.Read(buffer.AsSpan(0, requested * channels));
            if (read == 0) break;
            int producedFrames = read / channels;
            int skipped = Math.Min(_skipFrames, producedFrames);
            _skipFrames -= skipped;
            for (int i = skipped * channels; i < read; i++)
            {
                float sample = float.IsFinite(buffer[i]) ? Math.Clamp(buffer[i], -1f, 1f) : 0f;
                writer.Write((short)Math.Round(sample * 32767f));
            }
            if (!tail) frames -= producedFrames;
        }
        var reply = FormatReply();
        if (pcm.Length != 0) reply.Audio = MakeWav(pcm.ToArray(), reply.SampleRate, channels);
        return reply;
    }

    private VstReply FormatReply() => new()
    {
        SampleRate = _plugin!.SampleRate, InputChannels = _plugin.InputChannelCount, OutputChannels = _plugin.OutputChannelCount
    };
    private void RequireSession()
    {
        if (_effect == null || _source == null) throw new InvalidOperationException("No active Benaveil effect session.");
    }
    internal static byte[] MakeWav(byte[] pcm, int rate, int channels)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
        writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }
    public void Dispose()
    {
        _effect = null; _source = null;
        _plugin?.Dispose(); _plugin = null;
        _module?.Dispose(); _module = null;
        _used = false;
    }

    private sealed class ChunkSource(int rate, int channels) : ISampleProvider
    {
        private float[] _data = [];
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public void SetChunk(float[] data)
        {
            if (_position != _data.Length) throw new InvalidOperationException("Previous VST input was not consumed.");
            _data = data; _position = 0;
        }
        public int Read(Span<float> buffer)
        {
            int count = Math.Min(buffer.Length, _data.Length - _position);
            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
    }
}
