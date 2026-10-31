using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.LocalModels;
using Wds.SixStars.Api.Music;
using Wds.SixStars.Ui.Services;

namespace Wds.SixStars.Ui;

public partial class MusicWindow : Window
{
    private readonly LocalAiClient _model;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly MusicCompositionService _composer;
    private readonly ResonatorMidiClient _midi;
    private readonly DesktopMusicPlaybackService _player = new();
    private readonly SongWorkStore _store;
    private readonly string _midiPath;
    private long _requestStartedAtMs;
    private SongWork? _song;
    private CancellationTokenSource? _request;
    private int _generation;
    private bool _busy;
    private bool _hasMidi;

    public MusicWindow(LocalModelServiceOptions options, ResonatorMidiOptions midiOptions)
    {
        InitializeComponent();
        _model = new LocalAiClient(options);
        _composer = new MusicCompositionService(_model, new InstructionLibrary(InstructionLibrary.FindDefaultContentRoot()));
        _midi = new ResonatorMidiClient(_http, midiOptions);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SixStars", "Music");
        _store = new SongWorkStore(folder);
        _midiPath = Path.Combine(folder, "preview.mid");
        try {
            _song = _store.Load();
            if (_song != null) {
                Score.Text = _song.Notation; Bars.Text = _song.Bars.ToString(); Tempo.Text = _song.Tempo.ToString();
                Meter.Text = _song.Meter; UseAhd.IsChecked = _song.UseAhd;
                Status.Text = "Restored your song. Describe what you want to change.";
            }
        } catch (Exception error) { Status.Text = "Could not restore song work: " + error.Message + ". Clear Song Work to start fresh."; }
        Refresh();
    }

    private void Refresh()
    {
        Heading.Text = _song == null ? "Start a new song" : "Working on your song";
        Ask.Content = _song == null ? "Ask for Music" : "Revise Song";
        Ask.IsEnabled = !_busy;
        Render.IsEnabled = !_busy && _song != null && _midi.IsConfigured;
        Play.IsEnabled = !_busy && _hasMidi;
        Bars.IsEnabled = Tempo.IsEnabled = Meter.IsEnabled = UseAhd.IsEnabled = !_busy;
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!int.TryParse(Bars.Text, out int bars) || !int.TryParse(Tempo.Text, out int tempo)) { Status.Text = "Enter numeric bars and tempo."; return; }
        var request = new MusicCompositionRequest {
            Description = Brief.Text.Trim(), Bars = bars, Tempo = tempo, Meter = Meter.Text.Trim(), UseAhd = UseAhd.IsChecked == true,
            ExistingNotation = _song?.Notation ?? "", OriginalBrief = _song?.OriginalBrief ?? ""
        };
        try { MusicCompositionService.ValidateRequest(request); } catch (Exception error) { Status.Text = error.Message; return; }
        int generation = BeginRequest();
        var token = _request!.Token;
        try {
            Status.Text = _song == null ? "Composing your song…" : "Revising your song…";
            var result = await _composer.ComposeAsync(request, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            var next = new SongWork {
                Notation = result.Notation, OriginalBrief = _song?.OriginalBrief ?? request.Description,
                LastRequest = request.Description, Bars = bars, Tempo = tempo, Meter = request.Meter, UseAhd = request.UseAhd
            };
            _store.Save(next); // Commit only after valid generation; failures preserve previous work.
            _song = next; _hasMidi = false;
            Score.Text = next.Notation; Brief.Clear();
            if (_midi.IsConfigured) await RenderSongAsync(generation, token);
            else Status.Text = "Song saved. Your next request will revise it. MIDI rendering is disabled in appsettings.json.";
        } catch (OperationCanceledException) { if (generation == _generation) Status.Text = "Music request cancelled."; }
        catch (Exception error) { if (generation == _generation) Status.Text = error.Message; }
        finally { EndRequest(generation); }
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _song == null) return;
        int generation = BeginRequest();
        try { await RenderSongAsync(generation, _request!.Token); }
        catch (OperationCanceledException) { if (generation == _generation) Status.Text = "Render cancelled."; }
        catch (Exception error) { if (generation == _generation) Status.Text = "Song retained. " + error.Message; }
        finally { EndRequest(generation); }
    }

    private async Task RenderSongAsync(int generation, CancellationToken token)
    {
        var song = _song!;
        Status.Text = "Rendering MIDI…";
        byte[] data = await _midi.RenderAsync(new MusicRenderRequest {
            Notation = song.Notation, Bars = song.Bars, Tempo = song.Tempo, Meter = song.Meter
        }, token);
        if (generation != _generation || token.IsCancellationRequested) return;
        File.WriteAllBytes(_midiPath, data);
        _hasMidi = true;
        await StartPlaybackAsync(generation, token);
    }

    private int BeginRequest()
    {
        CancelRequest();
        _busy = true; _requestStartedAtMs = Environment.TickCount64;
        _request = new CancellationTokenSource(); Refresh();
        return _generation;
    }
    private void EndRequest(int generation)
    {
        if (generation != _generation) return;
        _request?.Dispose(); _request = null; _busy = false; Refresh();
    }
    private void CancelRequest()
    {
        _generation++; _request?.Cancel(); _request?.Dispose(); _request = null;
        _busy = false; _player.Stop();
    }
    private async Task StartPlaybackAsync(int generation, CancellationToken token)
    {
        Status.Text = "Buffering / playing music…";
        var midi = await File.ReadAllBytesAsync(_midiPath, token);
        await _player.PlayAsync(midi, _requestStartedAtMs, token);
        if (generation == _generation && !token.IsCancellationRequested)
            Status.Text = "Music preview finished.";
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_hasMidi) return;
        int generation = BeginRequest();
        try { await StartPlaybackAsync(generation, _request!.Token); }
        catch (OperationCanceledException) { if (generation == _generation) Status.Text = "Playback cancelled."; }
        catch (Exception error) { if (generation == _generation) Status.Text = error.Message; }
        finally { EndRequest(generation); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { CancelRequest(); Status.Text = "Stopped. Song work is retained."; Refresh(); }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CancelRequest();
        try {
            _store.Clear(); _song = null; _hasMidi = false; Score.Clear(); Brief.Clear();
            if (File.Exists(_midiPath)) File.Delete(_midiPath);
            Status.Text = "Song work cleared. Your next request will start a new song.";
        } catch (Exception error) { Status.Text = "Could not fully clear song work: " + error.Message; }
        Refresh();
    }
    protected override void OnClosed(EventArgs e)
    {
        CancelRequest(); _player.Dispose(); _http.Dispose(); _model.Dispose();
        base.OnClosed(e);
    }
}
