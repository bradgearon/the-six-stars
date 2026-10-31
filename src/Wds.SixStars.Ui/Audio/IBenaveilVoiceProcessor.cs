namespace Wds.SixStars.Ui.Audio;

internal interface IBenaveilVoiceProcessor
{
    bool Enabled { get; }
    Task<IBenaveilVoiceSession> BeginAsync(CancellationToken token);
}

internal interface IBenaveilVoiceSession : IDisposable
{
    Task<byte[]> ProcessAsync(byte[] wav, CancellationToken token);
    Task<byte[]> EndAsync(CancellationToken token);
}
