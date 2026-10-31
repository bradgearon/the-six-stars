namespace Wds.SixStars.VstProtocol;

// Commands run serially on the dedicated native VST thread.
public sealed class VstRequest
{
    public string Command { get; set; } = "";
    public string PluginPath { get; set; } = "";
    public string PresetPath { get; set; } = "";
    public int SampleRate { get; set; } = 48000;
    public int BlockSize { get; set; } = 1024;
    public double MaxTailSeconds { get; set; } = 5;
    public byte[] Audio { get; set; } = System.Array.Empty<byte>();
}

public sealed class VstReply
{
    public string? Error { get; set; }
    public int SampleRate { get; set; }
    public int InputChannels { get; set; }
    public int OutputChannels { get; set; }
    public byte[] Audio { get; set; } = System.Array.Empty<byte>();
}
