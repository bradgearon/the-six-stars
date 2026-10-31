using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Wds.SixStars.Ui.Services;

/// <summary>Windows sequencer preview through the default MIDI mapper; no DAW required.</summary>
internal sealed class MidiPreviewPlayer : IDisposable
{
    private readonly string _alias = "sixstarsmusic" + Guid.NewGuid().ToString("N");
    private bool _open;
    public void Play(string path)
    {
        Stop();
        string file = Path.GetFullPath(path);
        if (!File.Exists(file)) throw new FileNotFoundException("Render this song to MIDI first.", file);
        Send("open \"" + file + "\" type sequencer alias " + _alias);
        _open = true;
        try { Send("set " + _alias + " port mapper"); Send("play " + _alias + " from 0"); }
        catch { Stop(); throw; }
    }
    public bool IsPlaying()
    {
        if (!_open) return false;
        var result = new StringBuilder(128);
        if (mciSendString("status " + _alias + " mode", result, result.Capacity, IntPtr.Zero) != 0) { Stop(); return false; }
        bool playing = result.ToString().Equals("playing", StringComparison.OrdinalIgnoreCase);
        if (!playing) Stop();
        return playing;
    }
    public void Stop()
    {
        if (!_open) return;
        mciSendString("stop " + _alias, null, 0, IntPtr.Zero);
        mciSendString("close " + _alias, null, 0, IntPtr.Zero);
        _open = false;
    }
    public void Dispose() => Stop();
    private static void Send(string command)
    {
        uint error = mciSendString(command, null, 0, IntPtr.Zero);
        if (error == 0) return;
        var message = new StringBuilder(512);
        mciGetErrorString(error, message, message.Capacity);
        throw new InvalidOperationException("Windows MIDI preview: " + message + " (" + error + "). Check that a MIDI output synthesizer is available.");
    }
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint mciSendString(string command, StringBuilder? result, int length, IntPtr callback);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool mciGetErrorString(uint error, StringBuilder result, int length);
}
