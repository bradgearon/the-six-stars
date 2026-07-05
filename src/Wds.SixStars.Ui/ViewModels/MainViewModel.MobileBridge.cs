using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Wds.SixStars.Api.Audio;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Mobile;
using Wds.SixStars.Api.Speech;
using Wds.SixStars.Api.Voice;
using Wds.SixStars.Ui.Infrastructure;
using Wds.SixStars.Ui.Networking;
using Wds.SixStars.Api.State;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private MobileBridgeHttpServer? _mobileBridgeServer;
    private MobileBridgeAudioSocketHost? _mobileBridgeAudioHost;
    private readonly SemaphoreSlim _mobileBridgeRuntimeGate = new SemaphoreSlim(1, 1);
    private string _mobileBridgeHostName = "grasshoppah";
    private string _mobileBridgePort = "8066";
    private bool _mobileBridgeEnabled;
    private string _mobileBridgeStatus = "Android phone bridge stopped.";
    private string _mobileBridgeLastRequest = "No Android request yet.";
    private string _mobileBridgeVoiceDebugLog = "Elusiaa is listening for Android voice stream events.";

    public ICommand StartMobileBridgeCommand { get; private set; } = null!;
    public ICommand StopMobileBridgeCommand { get; private set; } = null!;

    public string MobileBridgeHostName
    {
        get => _mobileBridgeHostName;
        set
        {
            if (SetProperty(ref _mobileBridgeHostName, value))
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));
            }
        }
    }

    public string MobileBridgePort
    {
        get => _mobileBridgePort;
        set
        {
            if (SetProperty(ref _mobileBridgePort, value))
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));
            }
        }
    }

    public string MobileBridgeUrl => "http://" + (string.IsNullOrWhiteSpace(MobileBridgeHostName) ? Environment.MachineName : MobileBridgeHostName.Trim()) + ":" + ResolveMobileBridgePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string MobileBridgeAudioUrl => "tcp://" + (string.IsNullOrWhiteSpace(MobileBridgeHostName) ? Environment.MachineName : MobileBridgeHostName.Trim()) + ":" + ResolveMobileBridgeAudioPort().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool MobileBridgeEnabled
    {
        get => _mobileBridgeEnabled;
        set => SetProperty(ref _mobileBridgeEnabled, value);
    }

    public string MobileBridgeStatus
    {
        get => _mobileBridgeStatus;
        set => SetProperty(ref _mobileBridgeStatus, value);
    }

    public string MobileBridgeLastRequest
    {
        get => _mobileBridgeLastRequest;
        set => SetProperty(ref _mobileBridgeLastRequest, value);
    }

    public string MobileBridgeVoiceDebugLog
    {
        get => _mobileBridgeVoiceDebugLog;
        set => SetProperty(ref _mobileBridgeVoiceDebugLog, value);
    }

    private void InitializeMobileBridgeCommandsAndMaybeStart()
    {
        _mobileBridgeHostName = string.IsNullOrWhiteSpace(_appConfig.MobileBridge.HostName) ? Environment.MachineName : _appConfig.MobileBridge.HostName;
        _mobileBridgePort = (_appConfig.MobileBridge.Port <= 0 ? 8066 : _appConfig.MobileBridge.Port).ToString(System.Globalization.CultureInfo.InvariantCulture);
        StartMobileBridgeCommand = new RelayCommand(_ => StartMobileBridgeServer());
        StopMobileBridgeCommand = new RelayCommand(_ => StopMobileBridgeServer("Stopped by user."));

        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeHostName)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgePort)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeUrl)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MobileBridgeAudioUrl)));

        if (_appConfig.MobileBridge.EnabledOnStartup || IsTruthy(Environment.GetEnvironmentVariable("SIX_STARS_MOBILE_BRIDGE_ENABLED")))
        {
            StartMobileBridgeServer();
        }
    }

    private void StartMobileBridgeServer()
    {
        if (_mobileBridgeServer != null && _mobileBridgeServer.IsRunning)
        {
            MobileBridgeStatus = "Android bridge is already running. HTTP " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + ".";
            return;
        }

        int port = ResolveMobileBridgePort();
        int audioPort = ResolveMobileBridgeAudioPort();
        _mobileBridgeAudioHost?.Dispose();
        _mobileBridgeAudioHost = new MobileBridgeAudioSocketHost();
        _mobileBridgeAudioHost.StatusChanged += (_, message) => OnUiThread(() =>
        {
            MobileBridgeStatus = message + " HTTP " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + ".";
            AppendMobileBridgeVoiceDebug("Audio socket status", message);
        });
        _mobileBridgeAudioHost.Start(audioPort);

        _mobileBridgeServer?.Dispose();
        _mobileBridgeServer = new MobileBridgeHttpServer(
            port,
            ProcessMobileMessageAsync,
            ProcessMobileAudioAsync,
            ProcessMobileTestAudioAsync,
            CreateMobileHealthPayload,
            CreateMobileStarsPayload,
            CreateMobileStatePayloadAsync,
            _mobileBridgeDispatcher);
        _mobileBridgeServer.StatusChanged += (_, message) => OnUiThread(() =>
        {
            MobileBridgeStatus = message + " HTTP " + MobileBridgeUrl + "; audio " + MobileBridgeAudioUrl + ".";
            AppendMobileBridgeVoiceDebug("HTTP bridge status", message);
        });
        _mobileBridgeServer.Start();

        MobileBridgeEnabled = true;
        MobileBridgeStatus = "Android bridge running. HTTP " + MobileBridgeUrl + "; phone audio socket " + MobileBridgeAudioUrl + ". Use the HTTP URL in the Android app.";
    }

    private void StopMobileBridgeServer(string reason)
    {
        try { _mobileBridgeServer?.Dispose(); } catch { }
        try { _mobileBridgeAudioHost?.Dispose(); } catch { }
        _mobileBridgeServer = null;
        _mobileBridgeAudioHost = null;
        MobileBridgeEnabled = false;
        MobileBridgeStatus = reason;
    }

    private int ResolveMobileBridgePort()
    {
        if (int.TryParse(MobileBridgePort, out int port) && port > 0 && port <= 65535)
        {
            return port;
        }
        return 8066;
    }

    private int ResolveMobileBridgeAudioPort()
    {
        int httpPort = ResolveMobileBridgePort();
        return httpPort >= 65535 ? httpPort - 1 : httpPort + 1;
    }

    private object CreateMobileHealthPayload()
    {
        int clients = _mobileBridgeAudioHost?.ClientCount ?? 0;
        return new
        {
            ok = true,
            service = "six-stars-mobile-bridge",
            machineName = Environment.MachineName,
            hostName = MobileBridgeHostName,
            port = ResolveMobileBridgePort(),
            audioPort = ResolveMobileBridgeAudioPort(),
            audioSocketConnected = clients > 0,
            audioClientCount = clients,
            selectedStarId = SelectedStarId,
            streamingTts = _useStreamingTts,
            time = DateTimeOffset.Now
        };
    }

    private object CreateMobileStarsPayload()
    {
        return new
        {
            ok = true,
            stars = StarIds.ToArray(),
            requestTypes = RequestTypes.Select(x => x.ToString()).ToArray()
        };
    }

    private async Task<object> CreateMobileStatePayloadAsync(CancellationToken cancellationToken)
    {
        List<object> states = new List<object>();
        foreach (string starId in StarIds)
        {
            StarState state = await _runtime.GetStarStateAsync("android-phone-dev", starId, cancellationToken).ConfigureAwait(false);
            states.Add(CreateMobileStarStateDto(state));
        }

        return new
        {
            ok = true,
            selectedStarId = SelectedStarId,
            sessionId = "android-phone-dev",
            stars = states,
            latestEventSequence = _mobileBridgeDispatcher.GetEventsAfter(long.MaxValue).LatestSequence,
            time = DateTimeOffset.Now
        };
    }

    private async Task<MobileBridgeDispatchResult> HandleMobileBridgeCommandAsync(MobileBridgeDispatchCommand command, CancellationToken cancellationToken)
    {
        string commandType = string.IsNullOrWhiteSpace(command.CommandType) ? "RefreshState" : command.CommandType;
        string sessionId = string.IsNullOrWhiteSpace(command.SessionId) ? "android-phone-dev" : command.SessionId;
        string starId = ResolveDefaultStarId(command.StarId);

        switch (commandType)
        {
            case "SelectStar":
            {
                OnUiThread(() =>
                {
                    _suppressMobileBridgeSelectedStarBroadcast = true;
                    try
                    {
                        SelectedStarId = starId;
                    }
                    finally
                    {
                        _suppressMobileBridgeSelectedStarBroadcast = false;
                    }
                });
                StarState state = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, "Android selected " + starId + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, "Elusiaa shifted the phone focus to " + starId + ".");
            }
            case "SetMet":
            {
                bool isMet = command.IsMet ?? true;
                StarState state = await _runtime.SetStarMetAsync(sessionId, starId, isMet, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " met=" + state.IsMet + ", ally=" + state.IsAlly + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + (state.IsMet ? " is now marked met." : " is no longer marked met."));
            }
            case "SetAlly":
            {
                bool isAlly = command.IsAlly ?? true;
                StarState state = await _runtime.SetStarWorldStateAsync(new StarWorldStateUpdateRequest
                {
                    SessionId = sessionId,
                    StarId = starId,
                    IsAlly = isAlly,
                    IsMet = isAlly ? true : (bool?)null,
                    Reason = string.IsNullOrWhiteSpace(command.Reason) ? "Android bridge ally toggle" : command.Reason
                }, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, starId + " ally=" + state.IsAlly + ", met=" + state.IsMet + "."));
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, starId + (state.IsAlly ? " joined as an ally." : " ally flag was removed."));
            }
            case "ClearHistory":
            {
                StarState state = await _runtime.ResetStarMemoryAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                _stateChangeTracker.Reset(starId);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(state, "Cleared Android bridge history for " + starId + ".");
                    if (string.Equals(SelectedStarId, starId, StringComparison.OrdinalIgnoreCase))
                    {
                        StarOutput = string.Empty;
                        LastTranscript = string.Empty;
                    }
                });
                return PublishMobileCommandResult(command, true, commandType, starId, sessionId, state, "Khavia washed the Android conversation history clean for " + starId + ".");
            }

            case "ClearAllData":
            {
                StarState selectedState = null!;
                foreach (string star in StarIds.ToList())
                {
                    StarState reset = await _runtime.ResetStarMemoryAsync(sessionId, star, cancellationToken).ConfigureAwait(false);
                    _stateChangeTracker.Reset(star);
                    if (string.Equals(star, starId, StringComparison.OrdinalIgnoreCase) || string.Equals(star, SelectedStarId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedState = reset;
                    }
                    _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
                    {
                        EventType = "StarHistoryCleared",
                        CommandId = command.CommandId,
                        SourceClientId = command.SourceClientId,
                        SessionId = sessionId,
                        StarId = star,
                        SelectedStarId = SelectedStarId,
                        IsMet = reset.IsMet,
                        IsAlly = reset.IsAlly,
                        Heart = reset.Heart,
                        Trust = reset.Trust,
                        Romance = reset.Romance,
                        EmotionalConnectionPercent = reset.EmotionalConnectionPercent,
                        IsGirlfriendReady = reset.IsGirlfriendReady,
                        Message = "Elusiaa helped clear all local Android Star data for " + star + "."
                    });
                }
                if (selectedState == null) selectedState = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() =>
                {
                    ApplyMobileStateToSelectedStarUi(selectedState, "Cleared all Android bridge Star data.");
                    StarOutput = string.Empty;
                    LastTranscript = string.Empty;
                });
                return PublishMobileCommandResult(command, true, commandType, selectedState.StarId, sessionId, selectedState, "Elusiaa swept every Android Star memory shelf clean and refreshed the state.");
            }
            case "RefreshState":
            default:
            {
                StarState state = await _runtime.GetStarStateAsync(sessionId, starId, cancellationToken).ConfigureAwait(false);
                OnUiThread(() => ApplyMobileStateToSelectedStarUi(state, "Refreshed Android bridge state for " + starId + "."));
                return PublishMobileCommandResult(command, true, "RefreshState", starId, sessionId, state, "Nyxari checked the current state for " + starId + ".");
            }
        }
    }

    private void ApplyMobileStateToSelectedStarUi(StarState state, string status)
    {
        if (state == null) return;
        if (string.Equals(SelectedStarId, state.StarId, StringComparison.OrdinalIgnoreCase))
        {
            _isLoadingSelectedStarState = true;
            try
            {
                SelectedStarIsMet = state.IsMet;
                SelectedStarIsAlly = state.IsAlly;
            }
            finally
            {
                _isLoadingSelectedStarState = false;
            }
        }
        MobileBridgeStatus = status;
        AppendMobileBridgeVoiceDebug("Mobile state update", status + " Heart=" + state.Heart + ", Trust=" + state.Trust + ", Romance=" + state.Romance + ", Connection=" + state.EmotionalConnectionPercent.ToString("0.0") + "%");
    }

    private MobileBridgeDispatchResult PublishMobileCommandResult(MobileBridgeDispatchCommand command, bool ok, string commandType, string starId, string sessionId, StarState state, string message)
    {
        MobileBridgeDispatchResult result = new MobileBridgeDispatchResult
        {
            Ok = ok,
            CommandId = command.CommandId,
            CommandType = commandType,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            State = state,
            Message = message
        };

        result.Event = _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
        {
            EventType = commandType == "SelectStar" ? "SelectedStarChanged" : (commandType == "ClearHistory" || commandType == "ClearAllData") ? "StarHistoryCleared" : "StarStateChanged",
            CommandId = command.CommandId,
            SourceClientId = command.SourceClientId,
            SessionId = sessionId,
            StarId = starId,
            SelectedStarId = SelectedStarId,
            IsMet = state.IsMet,
            IsAlly = state.IsAlly,
            Heart = state.Heart,
            Trust = state.Trust,
            Romance = state.Romance,
            EmotionalConnectionPercent = state.EmotionalConnectionPercent,
            IsGirlfriendReady = state.IsGirlfriendReady,
            Message = message
        });
        return result;
    }

    private async Task PublishMobileSelectedStarUpdateAsync(string reason)
    {
        try
        {
            string starId = ResolveDefaultStarId(SelectedStarId);
            StarState state = await _runtime.GetStarStateAsync("android-phone-dev", starId, CancellationToken.None).ConfigureAwait(false);
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = "SelectedStarChanged",
                SourceClientId = "desktop-ui",
                SessionId = "android-phone-dev",
                StarId = starId,
                SelectedStarId = starId,
                IsMet = state.IsMet,
                IsAlly = state.IsAlly,
                Heart = state.Heart,
                Trust = state.Trust,
                Romance = state.Romance,
                EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                IsGirlfriendReady = state.IsGirlfriendReady,
                Message = "Elusiaa noticed the desktop selected Star changed to " + starId + " (" + reason + ")."
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish selected Star update: " + ex.Message);
        }
    }

    private static object CreateMobileStarStateDto(StarState state)
    {
        return new
        {
            starId = state.StarId,
            isMet = state.IsMet,
            isAlly = state.IsAlly,
            heart = state.Heart,
            trust = state.Trust,
            romance = state.Romance,
            emotionalConnectionPercent = state.EmotionalConnectionPercent,
            isGirlfriendReady = state.IsGirlfriendReady,
            interactionCount = state.InteractionCount
        };
    }

    private void PublishMobileBridgeStateEventFromDesktop(string eventType, StarState state, string message)
    {
        if (state == null) return;
        try
        {
            _mobileBridgeDispatcher.Publish(new MobileBridgeDispatchEvent
            {
                EventType = string.IsNullOrWhiteSpace(eventType) ? "StarStateChanged" : eventType,
                SourceClientId = "desktop-ui",
                SessionId = DefaultSessionId,
                StarId = state.StarId,
                SelectedStarId = SelectedStarId,
                IsMet = state.IsMet,
                IsAlly = state.IsAlly,
                Heart = state.Heart,
                Trust = state.Trust,
                Romance = state.Romance,
                EmotionalConnectionPercent = state.EmotionalConnectionPercent,
                IsGirlfriendReady = state.IsGirlfriendReady,
                Message = message
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[MobileBridge] Could not publish desktop state event: " + ex.Message);
        }
    }

    private void AppendMobileBridgeVoiceDebug(string title, string details)
    {
        string line = "[" + DateTimeOffset.Now.ToString("HH:mm:ss") + "] Elusiaa whispers: " + title;
        if (!string.IsNullOrWhiteSpace(details))
        {
            line += " — " + details.Replace("\r", " ").Replace("\n", " ");
        }

        string next = line + Environment.NewLine + MobileBridgeVoiceDebugLog;
        const int maxChars = 9000;
        if (next.Length > maxChars) next = next.Substring(0, maxChars);
        MobileBridgeVoiceDebugLog = next;
    }

    private static string Sha256Hex(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        StringBuilder builder = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) builder.Append(b.ToString("x2"));
        return builder.ToString();
    }

    private static string ShortHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return "none";
        return hash.Length <= 12 ? hash : hash.Substring(0, 12);
    }


    private async Task<object> ProcessMobileTestAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_mobileBridgeAudioHost == null || _mobileBridgeAudioHost.ClientCount == 0)
            {
                return new
                {
                    ok = false,
                    error = "No Android phone audio socket is connected. Open the Android app and wait for the phone voice socket to connect first.",
                    audioPort = ResolveMobileBridgeAudioPort(),
                    audioClientCount = 0
                };
            }

            byte[] wav = CreateMobileTestToneWav(sampleRate: 24000, seconds: 1.25, frequencyHz: 660.0);
            string checksum = Sha256Hex(wav);
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa is sending the desktop test tone. sha256=" + ShortHash(checksum), cancellationToken).ConfigureAwait(false);
            VoiceSynthesisResult result = new VoiceSynthesisResult
            {
                StarId = SelectedStarId ?? "Elusiaa",
                VoiceId = "android_phone_audio_socket_test",
                Instructions = "Synthetic 24 kHz PCM WAV test tone from desktop to Android phone audio queue.",
                WavAudio = wav,
                MimeType = "audio/wav",
                GeneratedAt = DateTimeOffset.UtcNow,
                DurationMs = 1250,
                Source = "mobile-bridge-test-tone"
            };

            MobileAudioSendResult sent = await _mobileBridgeAudioHost.SendVoiceChunkAsync(result, 0, 1, cancellationToken).ConfigureAwait(false);
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa finished the desktop test tone send. sha256=" + ShortHash(checksum) + ", clients=" + sent.SentClients, cancellationToken).ConfigureAwait(false);
            OnUiThread(() =>
            {
                MobileBridgeStatus = "Sent Android phone audio test tone. Clients=" + sent.SentClients + ", bytes=" + wav.Length + ", sha256=" + ShortHash(checksum) + ".";
                AppendMobileBridgeVoiceDebug("Desktop test tone sent", "clients=" + sent.SentClients + ", bytes=" + wav.Length + ", sha256=" + ShortHash(checksum));
            });
            return new
            {
                ok = sent.SentClients > 0,
                message = "Desktop sent a synthetic 24 kHz PCM WAV test tone to the Android voice socket.",
                sent.SentClients,
                sent.ConnectedClients,
                sent.FailedClients,
                wavBytes = wav.Length,
                sha256 = checksum,
                sha256Short = ShortHash(checksum),
                audioPort = ResolveMobileBridgeAudioPort()
            };
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android phone audio test failed: " + ex.Message);
            return new { ok = false, error = ex.Message, type = ex.GetType().Name };
        }
    }

    private static byte[] CreateMobileTestToneWav(int sampleRate, double seconds, double frequencyHz)
    {
        int sampleCount = Math.Max(1, (int)(sampleRate * seconds));
        byte[] pcm = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            double envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.05), (sampleCount - i) / (sampleRate * 0.08)));
            short sample = (short)(Math.Sin(2.0 * Math.PI * frequencyHz * t) * 0.22 * envelope * short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xff);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
        }

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(ms);
        writer.Write(new[] { 'R', 'I', 'F', 'F' }.Select(c => (byte)c).ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write(new[] { 'W', 'A', 'V', 'E' }.Select(c => (byte)c).ToArray());
        writer.Write(new[] { 'f', 'm', 't', ' ' }.Select(c => (byte)c).ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(new[] { 'd', 'a', 't', 'a' }.Select(c => (byte)c).ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return ms.ToArray();
    }

    private async Task<MobileBridgeResponse> ProcessMobileMessageAsync(MobileBridgeRequest request, CancellationToken cancellationToken)
    {
        return await ProcessMobileConversationAsync(request, transcript: string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MobileBridgeResponse> ProcessMobileAudioAsync(MobileBridgeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AudioBase64))
        {
            return new MobileBridgeResponse { Ok = false, VoiceError = "No audioBase64 was supplied.", Notes = { "Android did not send audio data." } };
        }

        string transcriptText;
        try
        {
            byte[] wavBytes = Convert.FromBase64String(request.AudioBase64);
            PcmWavData pcm = DecodePcm16WavOrRaw(wavBytes, request.SampleRate, request.Channels, request.BitsPerSample);
            string segmentId = "android-" + Guid.NewGuid().ToString("N");
            IWhisperSpeechStreamer createdStreamer = CreateWhisperStreamer();
            using DisposableWhisperWrapper streamer = new DisposableWhisperWrapper(createdStreamer);

            await streamer.BeginSegmentAsync(segmentId, pcm.Format, new WhisperTranscriptionOptions
            {
                Language = Environment.GetEnvironmentVariable("SIX_STARS_WHISPER_LANGUAGE") ?? _appConfig.Whisper.Language
            }, cancellationToken).ConfigureAwait(false);
            await streamer.PushAudioAsync(segmentId, pcm.Pcm16, 0, pcm.Pcm16.Length, cancellationToken).ConfigureAwait(false);
            SpeechTranscript transcript = await streamer.EndSegmentAsync(segmentId, cancellationToken).ConfigureAwait(false);
            transcriptText = transcript.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            return new MobileBridgeResponse { Ok = false, VoiceError = "Android audio transcription failed: " + ex.Message };
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return new MobileBridgeResponse { Ok = false, Transcript = string.Empty, VoiceError = "Whisper returned an empty transcript." };
        }

        request.PlayerText = transcriptText;
        MobileBridgeResponse response = await ProcessMobileConversationAsync(request, transcriptText, cancellationToken).ConfigureAwait(false);
        response.Transcript = transcriptText;
        return response;
    }

    private async Task<MobileBridgeResponse> ProcessMobileConversationAsync(MobileBridgeRequest mobileRequest, string transcript, CancellationToken cancellationToken)
    {
        await _mobileBridgeRuntimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string starId = ResolveDefaultStarId(mobileRequest.StarId);
            RequestType requestType = ParseMobileRequestType(mobileRequest.RequestType);
            bool wantsPhoneAudio = mobileRequest.ReturnAudio;
            DateTimeOffset started = DateTimeOffset.Now;

            OnUiThread(() =>
            {
                MobileBridgeLastRequest = started.ToString("HH:mm:ss") + " " + mobileRequest.ClientName + " -> " + starId + " " + requestType + ": " + TruncateForUi(mobileRequest.PlayerText, 180);
                MobileBridgeStatus = "Android request is using the desktop runtime for " + starId + ".";
                AppendMobileBridgeVoiceDebug("Phone request arrived", starId + " " + requestType + ", phoneAudio=" + wantsPhoneAudio + ", textChars=" + (mobileRequest.PlayerText?.Length ?? 0));
                Status = "Android bridge request received for " + starId + ".";
            });

            StarInteractionRequest request = new StarInteractionRequest
            {
                SessionId = string.IsNullOrWhiteSpace(mobileRequest.SessionId) ? "android-phone-dev" : mobileRequest.SessionId,
                StarId = starId,
                Speaker = "LastCat",
                RequestType = requestType,
                PlayerText = mobileRequest.PlayerText ?? string.Empty,
                GameContext = CreateMobileGameContext(mobileRequest, starId),
                StoryOptions = requestType == RequestType.Story ? CreateStoryOptions(false) : null
            };

            ResetLlmDebugLog(request);
            CancellationToken runtimeToken = _operationController.BeginNewOperation("android bridge request to " + starId);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeToken);
            StarInteractionResponse starResponse = await _runtime.ProcessAsync(request, linkedCts.Token).ConfigureAwait(false);

            _stateChangeTracker.RecordStateChange(
                starResponse.StarId,
                starResponse.StateDeltas.Heart,
                starResponse.StateDeltas.Trust,
                starResponse.StateDeltas.Romance,
                starResponse.StateDeltas.BenaveilInfluence,
                starResponse.StateDeltas.EmotionalConnectionPercent);
            TrackStoryFromResponse(starResponse);

            MobileBridgeResponse response = CreateMobileResponse(mobileRequest, requestType, transcript, starResponse);

            OnUiThread(() => AppendMobileBridgeVoiceDebug("Text response ready", (starResponse.StarId ?? starId) + " spokenChars=" + (response.SpokenText?.Length ?? 0) + ", phoneAudio=" + wantsPhoneAudio));

            OnUiThread(() =>
            {
                StarOutput = FormatStarOutput(starResponse);
                Status = "Android bridge response ready for " + starResponse.StarId + ". Heart Δ " + starResponse.StateDeltas.Heart + ", Trust Δ " + starResponse.StateDeltas.Trust + ", Romance Δ " + starResponse.StateDeltas.Romance + ".";
                DebugJson = _llmDebugLog.ToString()
                    + "\n================ ANDROID BRIDGE STAR RESPONSE ================\n"
                    + JsonSerializer.Serialize(new
                    {
                        mobile = true,
                        response.StarId,
                        response.RequestType,
                        transcriptLength = transcript?.Length ?? 0,
                        spokenChars = response.SpokenText.Length,
                        wantsPhoneAudio,
                        response.VoiceSocketConnected,
                        response.VoiceChunksSent,
                        response.VoiceBytesSent,
                        response.VoiceError,
                        state = new
                        {
                            response.HeartDelta,
                            response.TrustDelta,
                            response.RomanceDelta,
                            response.BenaveilDelta,
                            response.EmotionalConnectionDelta,
                            response.EmotionalConnectionAfter,
                            response.IsGirlfriendReadyAfter
                        }
                    }, _jsonOptions);
            });

            if (!string.IsNullOrWhiteSpace(starResponse.BackgroundAnalysisId))
            {
                _pendingBackgroundAnalysisId = starResponse.BackgroundAnalysisId;
            }

            if (wantsPhoneAudio)
            {
                response.VoiceSocketConnected = (_mobileBridgeAudioHost?.ClientCount ?? 0) > 0;
                response.VoiceMessage = response.VoiceSocketConnected
                    ? "Text returned immediately. Voice is generating in the desktop app and will stream to the Android phone socket."
                    : "Text returned immediately, but no Android audio socket is connected yet.";

                OnUiThread(() => AppendMobileBridgeVoiceDebug("HTTP response returned to phone", response.VoiceSocketConnected ? "Voice generation is now running in the background." : "No phone audio socket was connected."));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendMobileVoiceAsync(starResponse, response, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnUiThread(() => MobileBridgeStatus = "Android background voice error: " + ex.Message);
                        try { ReleasePendingBackgroundAnalysis("Android background voice failed: " + ex.Message); } catch { }
                    }
                });
            }
            else if (mobileRequest.SpeakOnDesktop || _appConfig.MobileBridge.SpeakResponsesOnDesktop)
            {
                await SpeakStarResponseAsync(starResponse, linkedCts.Token).ConfigureAwait(false);
            }
            else
            {
                ReleasePendingBackgroundAnalysis("Android bridge response returned without voice playback.");
            }

            response.VoiceSocketConnected = (_mobileBridgeAudioHost?.ClientCount ?? 0) > 0;
            OnUiThread(() =>
            {
                MobileBridgeStatus = wantsPhoneAudio
                    ? "Android text response returned. Phone voice generation continues in the background."
                    : "Android response complete. Voice chunks sent: " + response.VoiceChunksSent + ", bytes: " + response.VoiceBytesSent + ".";
            });
            return response;
        }
        catch (OperationCanceledException)
        {
            return new MobileBridgeResponse { Ok = false, VoiceError = "Android bridge request was cancelled." };
        }
        catch (Exception ex)
        {
            OnUiThread(() => MobileBridgeStatus = "Android bridge error: " + ex.Message);
            return new MobileBridgeResponse { Ok = false, VoiceError = ex.Message, Notes = { ex.GetType().Name } };
        }
        finally
        {
            IsProcessingRuntime = false;
            _mobileBridgeRuntimeGate.Release();
        }
    }

    private GameContext CreateMobileGameContext(MobileBridgeRequest request, string starId)
    {
        GameContext context = new GameContext
        {
            Area = string.IsNullOrWhiteSpace(request.Area) ? Area : request.Area,
            Quest = string.IsNullOrWhiteSpace(request.Quest) ? Quest : request.Quest,
            BattleState = "Safe",
            RomanceMode = request.RomanceMode,
            BenaveilPressureLevel = 0
        };
        context.EquippedStars.Add(starId);
        context.RecentEvents.Add("Android phone bridge conversation request");
        if (!string.IsNullOrWhiteSpace(request.ClientName)) context.RecentEvents.Add("Client: " + request.ClientName);
        context.Flags["mobileBridge"] = "true";
        context.Flags["phoneAudioSocketConnected"] = ((_mobileBridgeAudioHost?.ClientCount ?? 0) > 0).ToString().ToLowerInvariant();
        context.Flags["speakOnDesktop"] = request.SpeakOnDesktop.ToString().ToLowerInvariant();
        context.Flags["returnAudioToPhone"] = request.ReturnAudio.ToString().ToLowerInvariant();
        return context;
    }

    private MobileBridgeResponse CreateMobileResponse(MobileBridgeRequest request, RequestType requestType, string transcript, StarInteractionResponse response)
    {
        return new MobileBridgeResponse
        {
            Ok = true,
            Source = "android-mobile-bridge",
            StarId = response.StarId ?? request.StarId,
            RequestType = requestType.ToString(),
            Transcript = transcript ?? string.Empty,
            SpokenText = response.SpokenText ?? string.Empty,
            StarOutput = FormatStarOutput(response),
            ConversationSummary = response.ConversationSummary ?? string.Empty,
            ChangesSummary = response.ChangesSummary ?? string.Empty,
            EmotionalPurpose = response.EmotionalPurpose ?? string.Empty,
            HeartDelta = response.StateDeltas.Heart,
            TrustDelta = response.StateDeltas.Trust,
            RomanceDelta = response.StateDeltas.Romance,
            BenaveilDelta = response.StateDeltas.BenaveilInfluence,
            EmotionalConnectionDelta = response.StateDeltas.EmotionalConnectionPercent,
            EmotionalConnectionAfter = response.EmotionalConnectionPercentAfter,
            IsGirlfriendReadyAfter = response.IsGirlfriendReadyAfter,
            VoiceRequested = request.ReturnAudio,
            VoiceSocketConnected = (_mobileBridgeAudioHost?.ClientCount ?? 0) > 0,
            VoiceMessage = request.ReturnAudio
                ? "Voice is streamed over the phone audio socket on port " + ResolveMobileBridgeAudioPort() + "."
                : "Phone voice was not requested."
        };
    }

    private async Task SendMobileVoiceAsync(StarInteractionResponse starResponse, MobileBridgeResponse mobileResponse, CancellationToken cancellationToken)
    {
        if (_mobileBridgeAudioHost == null || _mobileBridgeAudioHost.ClientCount == 0)
        {
            mobileResponse.VoiceSocketConnected = false;
            mobileResponse.VoiceError = "No Android audio socket is connected. The phone should connect to TCP " + ResolveMobileBridgeAudioPort() + " after /health.";
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation skipped", "No Android audio socket is connected."));
            ReleasePendingBackgroundAnalysis("Android bridge had no phone audio socket; released background analysis after text response.");
            return;
        }

        PrepareVoiceCloneReferenceForTts(starResponse);
        mobileResponse.VoiceSocketConnected = true;
        await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa opened the voice stream. Generating " + (starResponse.StarId ?? "Star") + " voice...", cancellationToken).ConfigureAwait(false);
        OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation started", (starResponse.StarId ?? "Star") + ", streaming=" + _useStreamingTts));

        if (_useStreamingTts)
        {
            using IStreamingVoiceGenerator generator = CreateStreamingVoiceGenerator();
            using StreamingStarVoiceResponsePipeline mobilePipeline = new StreamingStarVoiceResponsePipeline(generator, new NullWavePlaybackEndpoint(), _speechPipeline, _interruptionManager)
            {
                AutoPlayChunks = false
            };

            int chunksSent = 0;
            int bytesSent = 0;
            mobilePipeline.ChunkReadyForConsumer += (sender, args) =>
            {
                try
                {
                    int chunkIndex = chunksSent;
                    int totalChunks = Math.Max(1, starResponse.SpokenParts.Count == 0 ? 1 : starResponse.SpokenParts.Count);
                    string checksum = Sha256Hex(args.Result.WavAudio);
                    MobileAudioSendResult sent = _mobileBridgeAudioHost.SendVoiceChunkAsync(args.Result, chunkIndex, totalChunks, CancellationToken.None).GetAwaiter().GetResult();
                    chunksSent++;
                    bytesSent += args.Result.WavAudio.Length;
                    _mobileBridgeAudioHost.SendStatusAsync("Elusiaa sent phone voice chunk " + chunksSent + "/" + totalChunks + " sha256=" + ShortHash(checksum) + ", bytes=" + args.Result.WavAudio.Length + ".", CancellationToken.None).GetAwaiter().GetResult();
                    OnUiThread(() =>
                    {
                        MobileBridgeStatus = "Sent phone voice chunk " + chunksSent + " (" + args.Result.WavAudio.Length + " bytes, sha256=" + ShortHash(checksum) + ") to " + sent.SentClients + " phone client(s).";
                        AppendMobileBridgeVoiceDebug("Voice chunk sent", "chunk=" + chunksSent + "/" + totalChunks + ", clients=" + sent.SentClients + ", bytes=" + args.Result.WavAudio.Length + ", sha256=" + ShortHash(checksum));
                    });
                }
                catch (Exception ex)
                {
                    mobileResponse.VoiceError = "Phone audio send failed: " + ex.Message;
                }
            };
            mobilePipeline.GenerationError += (_, args) =>
            {
                mobileResponse.VoiceError = args.ErrorMessage ?? "TTS generation error.";
                _ = _mobileBridgeAudioHost.SendErrorAsync(mobileResponse.VoiceError, CancellationToken.None);
            };

            await mobilePipeline.SpeakStreamingAsync(DefaultSessionId, starResponse, cancellationToken).ConfigureAwait(false);
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa closed the voice stream. Finished generating and sending " + chunksSent + " chunk(s), bytes=" + bytesSent + ".", cancellationToken).ConfigureAwait(false);
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Voice generation finished", "chunks=" + chunksSent + ", bytes=" + bytesSent));
            mobileResponse.VoiceChunksSent = chunksSent;
            mobileResponse.VoiceBytesSent = bytesSent;
            if (string.IsNullOrWhiteSpace(mobileResponse.VoiceError))
            {
                mobileResponse.VoiceMessage = "Queued " + chunksSent + " ordered voice chunk(s) on the Android phone audio socket.";
            }
        }
        else
        {
            IStarVoiceGenerator generator = CreateVoiceGenerator();
            VoiceSynthesisResult result = await generator.GenerateAsync(CreateVoiceSynthesisRequest(starResponse), cancellationToken).ConfigureAwait(false);
            string checksum = Sha256Hex(result.WavAudio);
            MobileAudioSendResult sent = await _mobileBridgeAudioHost.SendVoiceChunkAsync(result, 0, 1, cancellationToken).ConfigureAwait(false);
            await _mobileBridgeAudioHost.SendStatusAsync("Elusiaa sent one complete voice WAV. sha256=" + ShortHash(checksum) + ", bytes=" + result.WavAudio.Length + ".", cancellationToken).ConfigureAwait(false);
            mobileResponse.VoiceChunksSent = sent.SentClients > 0 ? 1 : 0;
            mobileResponse.VoiceBytesSent = result.WavAudio.Length;
            mobileResponse.VoiceMessage = "Queued one non-streaming voice WAV on the Android phone audio socket.";
            OnUiThread(() => AppendMobileBridgeVoiceDebug("Non-streaming voice sent", "clients=" + sent.SentClients + ", bytes=" + result.WavAudio.Length + ", sha256=" + ShortHash(checksum)));
        }

        ReleasePendingBackgroundAnalysis("Android phone voice chunks were generated/sent.");
    }

    private static RequestType ParseMobileRequestType(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out RequestType parsed))
        {
            return parsed;
        }
        return RequestType.Chat;
    }

    private static string TruncateForUi(string? text, int maxChars)
    {
        string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (value.Length <= maxChars) return value;
        return value.Substring(0, Math.Max(0, maxChars - 3)) + "...";
    }

    private static PcmWavData DecodePcm16WavOrRaw(byte[] bytes, int fallbackSampleRate, int fallbackChannels, int fallbackBits)
    {
        if (bytes.Length >= 44 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
        {
            int offset = 12;
            int channels = fallbackChannels <= 0 ? 1 : fallbackChannels;
            int sampleRate = fallbackSampleRate <= 0 ? 16000 : fallbackSampleRate;
            int bits = fallbackBits <= 0 ? 16 : fallbackBits;
            byte[]? data = null;
            while (offset + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                int size = BitConverter.ToInt32(bytes, offset + 4);
                offset += 8;
                if (size < 0 || offset + size > bytes.Length) break;
                if (id == "fmt " && size >= 16)
                {
                    short audioFormat = BitConverter.ToInt16(bytes, offset);
                    channels = BitConverter.ToInt16(bytes, offset + 2);
                    sampleRate = BitConverter.ToInt32(bytes, offset + 4);
                    bits = BitConverter.ToInt16(bytes, offset + 14);
                    if (audioFormat != 1 || bits != 16)
                    {
                        throw new NotSupportedException("Only PCM16 WAV input is supported by the Android bridge. audioFormat=" + audioFormat + ", bits=" + bits);
                    }
                }
                else if (id == "data")
                {
                    data = new byte[size];
                    Buffer.BlockCopy(bytes, offset, data, 0, size);
                }
                offset += size + (size % 2);
            }
            if (data == null) throw new InvalidDataException("WAV did not contain a data chunk.");
            return new PcmWavData(data, new PcmAudioFormat { SampleRateHz = sampleRate, Channels = channels, BitsPerSample = bits, Encoding = "pcm_s16le" });
        }

        return new PcmWavData(bytes, new PcmAudioFormat
        {
            SampleRateHz = fallbackSampleRate <= 0 ? 16000 : fallbackSampleRate,
            Channels = fallbackChannels <= 0 ? 1 : fallbackChannels,
            BitsPerSample = fallbackBits <= 0 ? 16 : fallbackBits,
            Encoding = "pcm_s16le"
        });
    }

    private sealed class PcmWavData
    {
        public PcmWavData(byte[] pcm16, PcmAudioFormat format)
        {
            Pcm16 = pcm16;
            Format = format;
        }
        public byte[] Pcm16 { get; }
        public PcmAudioFormat Format { get; }
    }

    private sealed class DisposableWhisperWrapper : IWhisperSpeechStreamer, IDisposable
    {
        private readonly IWhisperSpeechStreamer _inner;
        public DisposableWhisperWrapper(IWhisperSpeechStreamer inner) { _inner = inner; }
        public Task BeginSegmentAsync(string segmentId, PcmAudioFormat format, WhisperTranscriptionOptions options, CancellationToken cancellationToken) => _inner.BeginSegmentAsync(segmentId, format, options, cancellationToken);
        public Task PushAudioAsync(string segmentId, byte[] pcm16Buffer, int offset, int count, CancellationToken cancellationToken) => _inner.PushAudioAsync(segmentId, pcm16Buffer, offset, count, cancellationToken);
        public Task<SpeechTranscript> EndSegmentAsync(string segmentId, CancellationToken cancellationToken) => _inner.EndSegmentAsync(segmentId, cancellationToken);
        public Task CancelSegmentAsync(string segmentId, CancellationToken cancellationToken) => _inner.CancelSegmentAsync(segmentId, cancellationToken);
        public void Dispose()
        {
            if (_inner is IDisposable disposable) disposable.Dispose();
        }
    }
}
