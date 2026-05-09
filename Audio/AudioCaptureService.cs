using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ClickyWindows.Core;

namespace ClickyWindows.Audio;

/// <summary>
/// Captures microphone audio and resamples to 16 kHz / mono / PCM-16LE for AssemblyAI.
///
/// Architecture:
/// - WaveInEvent (WinMM) is the primary capture path. WinMM delivers DataAvailable
///   callbacks via the Windows message queue of the thread that called waveInOpen.
///   If that thread exits, all callbacks are silently dropped.
/// - A dedicated STA thread ("AudioCapturePump") owns the WaveInEvent for its
///   entire lifetime: it calls StartRecording(), pumps Application.DoEvents() in a
///   15 ms loop so waveInProc messages are dispatched, then calls StopRecording()
///   and disposes when signalled by Stop().
/// - The resampler and BufferedWaveProvider live on the STA thread's locals; only
///   the AudioDataAvailable event fires callbacks into the rest of the application.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public event Action<byte[]>? AudioDataAvailable;
    public float AudioPowerLevel { get; private set; }

    private readonly object _lock = new();
    private volatile bool   _isCapturing;

    // Signals the STA pump thread to stop
    private System.Threading.ManualResetEventSlim? _stopSignal;

    private static readonly WaveFormat TargetFormat =
        new(AppConstants.AudioSampleRate, AppConstants.AudioBitsPerSample, AppConstants.AudioChannels);

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
                IWaveIn?                  capture        = null;
                BufferedWaveProvider?     resamplerInput = null;
                MediaFoundationResampler? resampler      = null;

                try
                {
                    capture = CreateCapture();
                    if (capture == null)
                    {
                        startedSignal.Set();
                        return;
                    }

                    resamplerInput = new BufferedWaveProvider(capture.WaveFormat)
                    {
                        BufferDuration          = TimeSpan.FromSeconds(5),
                        DiscardOnBufferOverflow = true
                    };
                    resampler = new MediaFoundationResampler(resamplerInput, TargetFormat)
                    {
                        ResamplerQuality = 60
                    };

                    capture.DataAvailable += (_, e) =>
                    {
                        AppDebugLog.Write($"AudioCapture: DataAvailable bytes={e.BytesRecorded} capturing={_isCapturing}");
                        if (e.BytesRecorded == 0) return;
                        resamplerInput.AddSamples(e.Buffer, 0, e.BytesRecorded);
                        var resampled = DrainResampler(resampler);
                        if (resampled.Length == 0) return;
                        UpdatePowerLevel(resampled);
                        AudioDataAvailable?.Invoke(resampled);
                    };

                    capture.RecordingStopped += (_, e) =>
                        AppDebugLog.Write($"AudioCapture: RecordingStopped exception={e.Exception?.Message ?? "none"}");

                    capture.StartRecording();
                    startedOk = true;
                    AppDebugLog.Write("AudioCapture: StartRecording() — STA pump thread live.");
                }
                catch (Exception ex)
                {
                    startError = ex;
                    try { capture?.Dispose(); } catch { /* ignore */ }
                    startedSignal.Set();
                    return;
                }

                startedSignal.Set();

                // Pump the Win32 message loop on this thread so waveInProc callbacks
                // (posted as WM_* messages to this thread's queue) are dispatched.
                // We use a hidden message-only window via PeekMessage to drain the
                // queue, and wake ourselves every 15 ms to check the stop signal.
                // Application.DoEvents() does NOT work inside a WPF process because
                // it pumps the WPF dispatcher queue, not the raw Win32 queue.
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

                // Drain any remaining messages after StopRecording
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                capture!.Dispose();
                resampler?.Dispose();
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

    /// <summary>
    /// Creates the best available capture source.
    ///
    /// WaveInEvent (WinMM) is tried first: Realtek WASAPI in shared mode opens
    /// successfully but never fires DataAvailable on many consumer drivers.
    ///
    /// 1. WaveInEvent 44.1 kHz stereo 16-bit — most compatible WinMM format.
    /// 2. WaveInEvent 16 kHz mono 16-bit     — direct target, no resample needed.
    /// 3. WASAPI Shared Communications mic   — last resort for non-Realtek hardware.
    /// </summary>
    private static IWaveIn? CreateCapture()
    {
        // Attempt 1: WaveInEvent 44.1 kHz stereo 16-bit
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
            AppDebugLog.Write("AudioCapture: WaveIn.DeviceCount=0");
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"AudioCapture: WaveInEvent 44.1k failed ({ex.Message}), trying 16k mono");
        }

        // Attempt 2: WaveInEvent 16 kHz mono 16-bit
        try
        {
            AppDebugLog.Write("AudioCapture: WaveInEvent 16000/16/1 (fallback)");
            return new WaveInEvent
            {
                DeviceNumber       = 0,
                WaveFormat         = TargetFormat,
                BufferMilliseconds = AppConstants.AudioBufferMilliseconds
            };
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"AudioCapture: WaveInEvent 16k failed ({ex.Message}), trying WASAPI");
        }

        // Attempt 3: WASAPI on default Communications mic
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            var wasapi = new WasapiCapture(mic, true, AppConstants.AudioBufferMilliseconds);
            AppDebugLog.Write($"AudioCapture: WASAPI \"{mic.FriendlyName}\" (last resort)");
            return wasapi;
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"AudioCapture: WASAPI failed ({ex.Message}) — no mic available");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] DrainResampler(MediaFoundationResampler resampler)
    {
        var buf = new byte[8192];
        using var ms = new System.IO.MemoryStream();
        int read;
        while ((read = resampler.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, read);
        return ms.ToArray();
    }

    private void UpdatePowerLevel(byte[] pcm16)
    {
        if (pcm16.Length < 2) return;
        int n = pcm16.Length / 2;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[i * 2] | (pcm16[i * 2 + 1] << 8));
            double v = s / 32768.0;
            sum += v * v;
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
