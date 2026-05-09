using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ClickyWindows.Core;

namespace ClickyWindows.Audio;

/// <summary>
/// Captures microphone audio and converts it to 16 kHz / mono / PCM-16LE for AssemblyAI.
///
/// MediaFoundationResampler is a COM object and cannot be used from a thread other than
/// the one that created it (apartment boundary violation). Instead we do a pure-managed
/// decimation: average stereo channels to mono, then downsample 44100→16000 via integer
/// linear interpolation — no COM, no cross-thread issues.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public event Action<byte[]>? AudioDataAvailable;
    public float AudioPowerLevel { get; private set; }

    private readonly object _lock = new();
    private volatile bool   _isCapturing;

    private System.Threading.ManualResetEventSlim? _stopSignal;

    // Source format delivered by WaveInEvent (primary path)
    private const int SrcRate     = 44100;
    private const int SrcChannels = 2;
    private const int SrcBits     = 16;

    // Target format expected by AssemblyAI
    private const int DstRate     = AppConstants.AudioSampleRate;    // 16000
    private const int DstChannels = AppConstants.AudioChannels;      // 1
    private const int DstBits     = AppConstants.AudioBitsPerSample; // 16

    private const float DecayFactor = 0.72f;
    private const float BoostFactor = 10.2f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_lock)
        {
            if (_isCapturing) return;

            var stopSignal    = new System.Threading.ManualResetEventSlim(false);
            var startedSignal = new System.Threading.ManualResetEventSlim(false);
            Exception? startError = null;
            bool       startedOk  = false;

            _stopSignal = stopSignal;

            var staThread = new System.Threading.Thread(() =>
            {
                IWaveIn? capture = null;

                try
                {
                    capture = CreateCapture();
                    if (capture == null)
                    {
                        startedSignal.Set();
                        return;
                    }

                    // Determine conversion parameters based on actual device format
                    int srcRate     = capture.WaveFormat.SampleRate;
                    int srcChannels = capture.WaveFormat.Channels;

                    capture.DataAvailable += (_, e) =>
                    {
                        if (e.BytesRecorded == 0) return;
                        try
                        {
                            var pcm16Mono16k = ConvertToMono16k(
                                e.Buffer, e.BytesRecorded, srcRate, srcChannels);
                            if (pcm16Mono16k.Length == 0) return;
                            UpdatePowerLevel(pcm16Mono16k);
                            AudioDataAvailable?.Invoke(pcm16Mono16k);
                        }
                        catch (Exception ex)
                        {
                            AppDebugLog.Write($"AudioCapture: conversion error — {ex.Message}");
                        }
                    };

                    capture.RecordingStopped += (_, e) =>
                    {
                        if (e?.Exception != null)
                            AppDebugLog.Write($"AudioCapture: RecordingStopped error — {e.Exception.Message}");
                    };

                    capture.StartRecording();
                    startedOk = true;
                    AppDebugLog.Write($"AudioCapture: recording started ({srcRate}Hz {srcChannels}ch → {DstRate}Hz mono).");
                }
                catch (Exception ex)
                {
                    startError = ex;
                    try { capture?.Dispose(); } catch { /* ignore */ }
                    startedSignal.Set();
                    return;
                }

                startedSignal.Set();

                // Keep this STA thread alive with a Win32 message pump.
                // waveInProc posts WM_* messages to this thread's queue.
                NativeMsg msg;
                while (!stopSignal.IsSet)
                {
                    while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                    stopSignal.Wait(15);
                }

                try { capture!.StopRecording(); } catch { /* ignore */ }

                // Drain remaining messages
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                capture!.Dispose();
                AppDebugLog.Write("AudioCapture: STA pump thread exiting.");
            });

            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Name = "AudioCapturePump";
            staThread.Start();

            startedSignal.Wait(8000);

            if (startError != null)
                throw startError;

            if (!startedOk)
            {
                AppDebugLog.Write("AudioCapture: failed to open any capture device.");
                stopSignal.Set();
                return;
            }

            _isCapturing = true;
        }
    }

    public void Stop()
    {
        System.Threading.ManualResetEventSlim? stop;
        lock (_lock)
        {
            if (!_isCapturing) return;
            _isCapturing    = false;
            AudioPowerLevel = 0f;
            stop = _stopSignal;
        }
        stop?.Set();
    }

    // ── Device selection ──────────────────────────────────────────────────────

    private static IWaveIn? CreateCapture()
    {
        // Primary: WaveInEvent 44.1 kHz stereo 16-bit (most compatible WinMM format)
        try
        {
            if (WaveIn.DeviceCount > 0)
            {
                var caps = WaveIn.GetCapabilities(0);
                AppDebugLog.Write($"AudioCapture: WaveInEvent \"{caps.ProductName}\" 44100/16/2");
                return new WaveInEvent
                {
                    DeviceNumber       = 0,
                    WaveFormat         = new WaveFormat(44100, 16, 2),
                    BufferMilliseconds = AppConstants.AudioBufferMilliseconds
                };
            }
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"AudioCapture: WaveInEvent 44.1k failed ({ex.Message}), trying 16k");
        }

        // Fallback: WaveInEvent 16 kHz mono — matches target, no conversion needed
        try
        {
            AppDebugLog.Write("AudioCapture: WaveInEvent 16000/16/1 (fallback)");
            return new WaveInEvent
            {
                DeviceNumber       = 0,
                WaveFormat         = new WaveFormat(DstRate, DstBits, DstChannels),
                BufferMilliseconds = AppConstants.AudioBufferMilliseconds
            };
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"AudioCapture: WaveInEvent 16k failed ({ex.Message}), trying WASAPI");
        }

        // Last resort: WASAPI shared mode
        try
        {
            var mic = new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            AppDebugLog.Write($"AudioCapture: WASAPI \"{mic.FriendlyName}\" (last resort)");
            return new WasapiCapture(mic, true, AppConstants.AudioBufferMilliseconds);
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"AudioCapture: all capture methods failed — {ex.Message}");
            return null;
        }
    }

    // ── Pure-managed sample-rate conversion ───────────────────────────────────

    /// <summary>
    /// Converts raw PCM from the capture device to 16 kHz mono PCM-16LE.
    /// Handles stereo→mono mixing and arbitrary sample-rate decimation using
    /// nearest-neighbour selection (sufficient quality for speech recognition).
    /// No COM, no external dependencies — safe to call from any thread.
    /// </summary>
    private static byte[] ConvertToMono16k(
        byte[] src, int srcBytes, int srcRate, int srcChannels)
    {
        int bytesPerSample = 2; // always 16-bit from WaveInEvent
        int srcFrames      = srcBytes / (bytesPerSample * srcChannels);
        int dstFrames      = (int)Math.Round((double)srcFrames * DstRate / srcRate);

        if (dstFrames == 0) return [];

        var dst = new byte[dstFrames * 2];

        for (int dstIdx = 0; dstIdx < dstFrames; dstIdx++)
        {
            // Map destination frame index back to nearest source frame
            int srcIdx = (int)((double)dstIdx * srcRate / DstRate);
            if (srcIdx >= srcFrames) srcIdx = srcFrames - 1;

            // Mix all channels to mono
            long sum = 0;
            for (int ch = 0; ch < srcChannels; ch++)
            {
                int offset = (srcIdx * srcChannels + ch) * bytesPerSample;
                short sample = (short)(src[offset] | (src[offset + 1] << 8));
                sum += sample;
            }
            short mono = (short)Math.Clamp(sum / srcChannels, short.MinValue, short.MaxValue);

            dst[dstIdx * 2]     = (byte)(mono & 0xFF);
            dst[dstIdx * 2 + 1] = (byte)((mono >> 8) & 0xFF);
        }

        return dst;
    }

    // ── Power level ───────────────────────────────────────────────────────────

    private void UpdatePowerLevel(byte[] pcm16)
    {
        if (pcm16.Length < 2) return;
        int n = pcm16.Length / 2;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            sum += (s / 32768.0) * (s / 32768.0);
        }
        float rms     = (float)Math.Sqrt(sum / n);
        float boosted = Math.Min(rms * BoostFactor, 1.0f);
        AudioPowerLevel = Math.Max(boosted, AudioPowerLevel * DecayFactor);
    }

    public void Dispose() => Stop();

    // ── Win32 message pump ────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public IntPtr hwnd;
        public uint   message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint   time;
        public int    ptX;
        public int    ptY;
    }

    private const uint PM_REMOVE = 0x0001;

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd,
        uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMsg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMsg lpMsg);
}
