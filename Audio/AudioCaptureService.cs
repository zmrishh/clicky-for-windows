using NAudio.CoreAudioApi;
using NAudio.Wave;
using ClickyWindows.Core;

namespace ClickyWindows.Audio;

/// <summary>
/// Captures microphone audio via WASAPI, resamples to 16 kHz / mono / PCM-16LE,
/// and surfaces both raw audio frames (for AssemblyAI) and a smoothed RMS power
/// level (for the waveform animation).
///
/// Mirrors the AVAudioEngine tap + BuddyPCM16AudioConverter pipeline from the Mac app.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires on a background thread with resampled PCM-16LE mono frames at 16 kHz.
    /// Subscribers must be thread-safe (CompanionManager posts to UI thread itself).
    /// </summary>
    public event Action<byte[]>? AudioDataAvailable;

    /// <summary>
    /// Smoothed RMS power level in [0, 1]. Updated on the calling (capture) thread;
    /// CompanionManager marshals to the UI thread before propagating.
    /// </summary>
    public float AudioPowerLevel { get; private set; }

    // ── NAudio objects ───────────────────────────────────────────────────────

    private WasapiCapture? _capture;
    private MediaFoundationResampler? _resampler;
    private BufferedWaveProvider? _resamplerInput;

    private readonly object _captureLock = new();
    private volatile bool _isCapturing;

    // Target format for AssemblyAI: 16kHz, mono, PCM-16LE
    private static readonly WaveFormat TargetFormat = new(
        AppConstants.AudioSampleRate,
        AppConstants.AudioBitsPerSample,
        AppConstants.AudioChannels
    );

    // ── Power level smoothing ────────────────────────────────────────────────

    // Mirrors BuddyDictationManager: exponential decay with factor 0.72
    private const float DecayFactor = 0.72f;
    private const float BoostFactor = 10.2f;

    // ── Capture lifecycle ────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the WASAPI capture device and resampling pipeline.
    /// Throws if no microphone is available or initialisation fails.
    /// </summary>
    public void Start()
    {
        lock (_captureLock)
        {
            if (_isCapturing) return;

            _capture = new WasapiCapture();
            _capture.DataAvailable += OnDataAvailable;

            // Buffer for the resampler: feed raw WASAPI PCM into it, read 16 kHz out.
            _resamplerInput = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true
            };

            _resampler = new MediaFoundationResampler(_resamplerInput, TargetFormat)
            {
                ResamplerQuality = 60
            };

            _capture.StartRecording();
            _isCapturing = true;
        }
    }

    /// <summary>Stops the capture pipeline cleanly.</summary>
    public void Stop()
    {
        lock (_captureLock)
        {
            if (!_isCapturing) return;
            _isCapturing = false;

            _capture?.StopRecording();
            _capture?.Dispose();
            _capture = null;

            _resampler?.Dispose();
            _resampler = null;

            _resamplerInput = null;
            AudioPowerLevel = 0f;
        }
    }

    // ── Raw data handler ─────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // Feed raw bytes into the resampler's input buffer.
        _resamplerInput?.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Drain the resampled output as PCM-16LE frames.
        var resampled = DrainResampler();
        if (resampled.Length > 0)
        {
            UpdatePowerLevel(resampled);
            AudioDataAvailable?.Invoke(resampled);
        }
    }

    private byte[] DrainResampler()
    {
        if (_resampler == null) return [];

        // Allocate a generously sized drain buffer.
        // 16 kHz × 16-bit × 1ch = 32 000 bytes/s; 200ms → 6 400 bytes.
        const int DrainBufferSize = 8192;
        var buffer = new byte[DrainBufferSize];
        using var ms = new IoMemoryStream();

        int read;
        while ((read = _resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    // ── RMS power level ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes RMS from raw PCM-16LE bytes, boosts and clamps to [0,1], then
    /// applies the same exponential smoothing decay as the Swift implementation.
    /// </summary>
    private void UpdatePowerLevel(byte[] pcm16Bytes)
    {
        if (pcm16Bytes.Length < 2) return;

        int sampleCount = pcm16Bytes.Length / 2;
        double sumSquares = 0.0;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(pcm16Bytes[i * 2] | (pcm16Bytes[i * 2 + 1] << 8));
            double normalised = sample / 32768.0;
            sumSquares += normalised * normalised;
        }

        float rms = (float)Math.Sqrt(sumSquares / sampleCount);
        float boosted = Math.Min(rms * BoostFactor, 1.0f);

        // Exponential decay: level falls gradually so waveform looks natural.
        AudioPowerLevel = Math.Max(boosted, AudioPowerLevel * DecayFactor);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
    }
}

