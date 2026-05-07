using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using ClickyWindows.Core;

namespace ClickyWindows.Api;

/// <summary>
/// Sends text to ElevenLabs via the Worker proxy and plays back the returned MP3
/// through the default audio output. Mirrors the Swift ElevenLabsTTSClient.
///
/// Playback is non-blocking: <see cref="SpeakAsync"/> returns once the audio starts
/// playing (same behaviour as the Swift implementation where AVAudioPlayer.play()
/// returns immediately).
/// </summary>
public sealed class ElevenLabsTtsClient : IDisposable
{
    private readonly Uri _proxyUri;
    private readonly HttpClient _http;
    private WaveOutEvent? _waveOut;
    private readonly object _playbackLock = new();

    public ElevenLabsTtsClient(string workerBaseUrl)
    {
        _proxyUri = new Uri(workerBaseUrl.TrimEnd('/') + "/tts");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches TTS audio and begins playback. Returns as soon as playback starts.
    /// Throws on network/decode errors. Respects <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(text);
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, _proxyUri) { Content = content };
        request.Headers.Accept.ParseAdd("audio/mpeg");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"ElevenLabs TTS error ({(int)response.StatusCode}): {err}",
                null,
                response.StatusCode);
        }

        var audioBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine($"[TTS] Playing {audioBytes.Length / 1024}KB audio");
        PlayMp3(audioBytes);
    }

    /// <summary>Whether TTS audio is currently playing.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_playbackLock)
                return _waveOut?.PlaybackState == PlaybackState.Playing;
        }
    }

    /// <summary>Stops any in-progress playback immediately.</summary>
    public void StopPlayback()
    {
        lock (_playbackLock)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
        }
    }

    // ── Fallback ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Speaks a message using Windows built-in TTS via the Windows.Speech COM API.
    /// Used as a fallback when ElevenLabs credits are exhausted.
    /// </summary>
    public static void SpeakFallback(string message)
    {
        Task.Run(() =>
        {
            try
            {
                // Use Windows SAPI via PowerShell as a lightweight fallback;
                // avoids a hard dependency on System.Speech in .NET 10.
                using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"Add-Type -AssemblyName System.Speech; " +
                                $"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                                $"$s.Speak([System.Text.RegularExpressions.Regex]::Replace('{message.Replace("'", " ")}', '[^a-zA-Z0-9 .,!?]', ''))\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                ps?.WaitForExit(8000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TTS] Fallback speech failed: {ex.Message}");
            }
        });
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void PlayMp3(byte[] audioBytes)
    {
        StopPlayback();

        // NAudio reads MP3 from a IoMemoryStream; keep the stream alive for the
        // duration of playback by storing it in a local that the PlaybackStopped
        // callback will dispose.
        var ms = new IoMemoryStream(audioBytes);
        var reader = new Mp3FileReader(ms);
        var waveOut = new WaveOutEvent();

        waveOut.PlaybackStopped += (_, _) =>
        {
            waveOut.Dispose();
            reader.Dispose();
            ms.Dispose();
            lock (_playbackLock)
            {
                if (ReferenceEquals(_waveOut, waveOut))
                    _waveOut = null;
            }
        };

        waveOut.Init(reader);

        lock (_playbackLock)
        {
            _waveOut = waveOut;
        }

        waveOut.Play();
    }

    private static Dictionary<string, object> BuildRequestBody(string text) => new()
    {
        ["text"] = text,
        ["model_id"] = AppConstants.TtsModelId,
        ["voice_settings"] = new Dictionary<string, object>
        {
            ["stability"] = AppConstants.TtsStability,
            ["similarity_boost"] = AppConstants.TtsSimilarityBoost
        }
    };

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        StopPlayback();
        _http.Dispose();
    }
}

