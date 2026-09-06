using System;
using NAudio.Wave;

namespace Wds.SixStars.Ui.Audio;

/// <summary>
/// NAudio 2.x DirectSoundOut pins IWaveProvider.WaveFormat inside InitializeDirectSound.
/// WaveFileReader commonly materializes a fmt chunk as WaveFormatExtraData, which owns a
/// managed byte[] and therefore cannot be pinned by modern .NET (GCHandle throws
/// "Object contains references"). DirectSound only needs the unmanaged WAVEFORMATEX fields,
/// so re-present byte-compatible PCM/IEEE-float audio with a plain WaveFormat instance.
/// </summary>
internal static class DirectSoundWaveFormatHelper
{
    public static WaveFormat Canonicalize(WaveFormat source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        // Handles WAVE_FORMAT_EXTENSIBLE as well as WaveFormatExtraData read from a WAV.
        WaveFormat standard = source.AsStandardWaveFormat();

        if (standard.Encoding == WaveFormatEncoding.Pcm)
        {
            return new WaveFormat(standard.SampleRate, standard.BitsPerSample, standard.Channels);
        }

        if (standard.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return WaveFormat.CreateCustomFormat(
                WaveFormatEncoding.IeeeFloat,
                standard.SampleRate,
                standard.Channels,
                standard.AverageBytesPerSecond,
                standard.BlockAlign,
                standard.BitsPerSample);
        }

        throw new InvalidOperationException(
            "DirectSound streaming requires decoded PCM or IEEE-float audio, but received " +
            standard.Encoding + " (" + standard.SampleRate + "Hz/" + standard.BitsPerSample +
            "bit/" + standard.Channels + "ch, runtime type " + source.GetType().Name + ").");
    }
}
