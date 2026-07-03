using System;
using System.Threading;

namespace Wds.SixStars.Api.Voice
{
    /// <summary>
    /// Optional playback endpoint capability for desktop/dev hosts that need to keep the OS audio
    /// device open while TTS is generating and between spoken chunks. This is especially useful for
    /// digital outputs that audibly click, sleep, or re-handshake when the stream repeatedly opens
    /// and closes. Unity can ignore this and keep its own AudioSource/music bus alive instead.
    /// </summary>
    public interface IAudioOutputKeepAliveEndpoint
    {
        /// <summary>
        /// Starts a silent output stream and returns a disposable handle. Disposing the handle stops
        /// the silent stream when the speech operation is fully finished or cancelled.
        /// </summary>
        IDisposable StartOutputKeepAlive(string reason, CancellationToken cancellationToken);
    }
}
