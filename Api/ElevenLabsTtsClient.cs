using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using ClickyWindows.Core;

namespace ClickyWindows.Api;

/// <summary>
/// TTS client with two-tier strategy:
///   1. ElevenLabs via Worker proxy — high-quality neural voice (requires paid credits).
///   2. Windows.Media.SpeechSynthesis — free, built-in neural voices (Windows 10+),
///      used automatically when ElevenLabs is unavailable or returns an error.
///
/// Playback is non-blocking: SpeakAsync returns as soon as audio starts playing.
/// </summary>
public sealed class ElevenLabsTtsClient : IDisposable
{
    private readonly Uri        _proxyUri;
    private readonly HttpClient _http;

    private WaveOutEvent?       _waveOut;
    private readonly object     _playbackLock = new();

    // Windows WinRT synthesizer — created lazily, reused across calls
    private SpeechSynthesizer?  _winSynth;
    private readonly object     _synthLock = new();

    public ElevenLabsTtsClient(string workerBaseUrl)
    {
        _proxyUri = new Uri(workerBaseUrl.TrimEnd('/') + "/tts");
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        // Try ElevenLabs first; fall through to Windows TTS on any failure
        try
        {
            await SpeakElevenLabsAsync(text, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw; // don't swallow cancellation
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"TTS: ElevenLabs failed ({ex.Message}) — using Windows TTS.");
        }

        await SpeakWindowsAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public bool IsPlaying
    {
        get { lock (_playbackLock) return _waveOut?.PlaybackState == PlaybackState.Playing; }
    }

    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
        }
    }

    // ── ElevenLabs path ───────────────────────────────────────────────────────

    private async Task SpeakElevenLabsAsync(string text, CancellationToken ct)
    {
        var json    = JsonSerializer.Serialize(BuildElevenLabsBody(text));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _proxyUri) { Content = content };
        request.Headers.Accept.ParseAdd("audio/mpeg");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"ElevenLabs {(int)response.StatusCode}: {err}", null, response.StatusCode);
        }

        var audioBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        AppDebugLog.Write($"TTS: ElevenLabs playing {audioBytes.Length / 1024}KB");
        PlayMp3(audioBytes);
    }

    // ── Windows TTS path ──────────────────────────────────────────────────────

    /// <summary>
    /// Synthesises text using the best available Windows neural voice and plays
    /// it through NAudio so volume/device is consistent with the rest of the app.
    /// </summary>
    private async Task SpeakWindowsAsync(string text, CancellationToken ct)
    {
        SpeechSynthesizer synth;
        lock (_synthLock)
        {
            _winSynth ??= new SpeechSynthesizer();

            // Prefer the highest-quality installed voice
            var voices = SpeechSynthesizer.AllVoices;
            var preferred = voices
                .OrderByDescending(v => v.DisplayName.Contains("Neural",  StringComparison.OrdinalIgnoreCase) ? 2
                                      : v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) ? 1
                                      : 0)
                .ThenByDescending(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (preferred != null && _winSynth.Voice?.Id != preferred.Id)
            {
                _winSynth.Voice = preferred;
                AppDebugLog.Write($"TTS: Windows voice = \"{preferred.DisplayName}\"");
            }

            synth = _winSynth;
        }

        ct.ThrowIfCancellationRequested();

        // SynthesizeTextToStreamAsync returns a WAV-format IRandomAccessStream
        var winStream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // Read WinRT stream via DataReader — no extension method dependencies
        var ms         = new System.IO.MemoryStream();
        var dataReader = new DataReader(winStream.GetInputStreamAt(0));
        uint remaining = (uint)winStream.Size;
        while (remaining > 0)
        {
            uint chunk = Math.Min(remaining, 65536u);
            await dataReader.LoadAsync(chunk).AsTask(ct).ConfigureAwait(false);
            var buf = new byte[chunk];
            dataReader.ReadBytes(buf);
            ms.Write(buf, 0, (int)chunk);
            remaining -= chunk;
        }
        dataReader.Dispose();

        ms.Position = 0;
        AppDebugLog.Write($"TTS: Windows TTS {ms.Length / 1024}KB");

        var reader  = new WaveFileReader(ms);
        StartWaveOut(reader, () => { reader.Dispose(); ms.Dispose(); });
    }

    // ── Playback helpers ──────────────────────────────────────────────────────

    private void PlayMp3(byte[] audioBytes)
    {
        StopPlayback();
        var ms     = new IoMemoryStream(audioBytes);
        var reader = new Mp3FileReader(ms);
        StartWaveOut(reader, () => { reader.Dispose(); ms.Dispose(); });
    }

    private void StartWaveOut(IWaveProvider provider, Action onStopped)
    {
        var waveOut = new WaveOutEvent { Volume = 1.0f };

        waveOut.PlaybackStopped += (_, _) =>
        {
            onStopped();
            waveOut.Dispose();
            lock (_playbackLock)
            {
                if (ReferenceEquals(_waveOut, waveOut))
                    _waveOut = null;
            }
        };

        waveOut.Init(provider);

        lock (_playbackLock)
            _waveOut = waveOut;

        waveOut.Play();
    }

    // ── Legacy static fallback (kept for error-message paths) ─────────────────

    public static void SpeakFallback(string message)
    {
        Task.Run(async () =>
        {
            try
            {
                using var synth  = new SpeechSynthesizer();
                var winStream    = await synth.SynthesizeTextToStreamAsync(message);
                var ms           = new System.IO.MemoryStream();
                var dr           = new DataReader(winStream.GetInputStreamAt(0));
                uint rem         = (uint)winStream.Size;
                while (rem > 0)
                {
                    uint chunk = Math.Min(rem, 65536u);
                    await dr.LoadAsync(chunk);
                    var buf = new byte[chunk];
                    dr.ReadBytes(buf);
                    ms.Write(buf, 0, (int)chunk);
                    rem -= chunk;
                }
                dr.Dispose();
                ms.Position = 0;

                using var reader = new WaveFileReader(ms);
                using var wo     = new WaveOutEvent { Volume = 1.0f };
                wo.Init(reader);
                wo.Play();
                while (wo.PlaybackState == PlaybackState.Playing)
                    await Task.Delay(100);
            }
            catch (Exception ex)
            {
                AppDebugLog.Write($"TTS: SpeakFallback failed — {ex.Message}");
            }
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Dictionary<string, object> BuildElevenLabsBody(string text) => new()
    {
        ["text"]          = text,
        ["model_id"]      = AppConstants.TtsModelId,
        ["voice_settings"] = new Dictionary<string, object>
        {
            ["stability"]        = AppConstants.TtsStability,
            ["similarity_boost"] = AppConstants.TtsSimilarityBoost
        }
    };

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        StopPlayback();
        _http.Dispose();
        lock (_synthLock) { _winSynth?.Dispose(); _winSynth = null; }
    }
}
