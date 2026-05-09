using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using ClickyWindows.Api;
using ClickyWindows.Audio;
using ClickyWindows.Input;
using ClickyWindows.Overlay;
using ClickyWindows.ScreenCapture;
using NAudio.CoreAudioApi;

namespace ClickyWindows.Core;

/// <summary>
/// Central state machine. Owns the push-to-talk pipeline, AI APIs, overlay manager,
/// and all application state. Mirrors the Swift CompanionManager 1:1 in terms of
/// state transitions, coordinate translation logic, and conversation history.
///
/// All public members must be accessed from the UI thread (Dispatcher.Invoke if needed).
/// Events are raised on the UI thread.
/// </summary>
public sealed class CompanionManager : IDisposable
{
    // ── Public events (fired on UI thread) ────────────────────────────────────

    public event Action? PermissionsChanged;
    public event Action<VoiceState>? VoiceStateChanged;
    public event Action<float>? AudioPowerLevelChanged;
    public event Action? DetectedElementChanged;
    public event Action? OnboardingVideoChanged;
    public event Action? OnboardingPromptChanged;
    public event Action? OverlayVisibilityChanged;

    // ── Permissions ───────────────────────────────────────────────────────────

    public bool HasMicrophonePermission { get; private set; }

    // ── Voice state ───────────────────────────────────────────────────────────

    private VoiceState _voiceState = VoiceState.Idle;
    public VoiceState VoiceState
    {
        get => _voiceState;
        private set
        {
            if (_voiceState == value) return;
            _voiceState = value;
            VoiceStateChanged?.Invoke(value);
        }
    }

    public string? LastTranscript { get; private set; }

    // ── Audio power level ─────────────────────────────────────────────────────

    public float AudioPowerLevel { get; private set; }

    // ── Overlay / detected element ────────────────────────────────────────────

    public bool IsOverlayVisible { get; private set; }
    public bool HasDetectedElement => _detectedElementCanvasPoint != null;

    private System.Windows.Point? _detectedElementCanvasPoint;
    public System.Windows.Point? DetectedElementCanvasPoint => _detectedElementCanvasPoint;

    private Rectangle? _detectedElementDisplayFrame;
    public Rectangle? DetectedElementDisplayFrame => _detectedElementDisplayFrame;

    public string? DetectedElementBubbleText { get; private set; }

    // ── Settings ──────────────────────────────────────────────────────────────

    public AppSettings Settings { get; }

    // ── Onboarding ────────────────────────────────────────────────────────────

    public Uri? OnboardingVideoUri { get; private set; }
    public bool ShowOnboardingVideo { get; private set; }
    public bool ShowOnboardingPrompt { get; private set; }

    // ── Internal components ───────────────────────────────────────────────────

    private ClaudeApi _claudeApi;
    private AssemblyAIStreamingProvider _assemblyAiProvider;
    private ElevenLabsTtsClient _ttsClient;

    /// <summary>Invalidates <see cref="StartPushToTalkSession"/> when the user starts a new hold while one is still preparing.</summary>
    private Guid _pttPrepareId = Guid.Empty;

    /// <summary>Cancels in-flight Worker + AssemblyAI handshake when the user releases Ctrl+Alt before listening starts.</summary>
    private CancellationTokenSource? _pttPrepareAbortSource;

    /// <summary>Avoids spamming the user if they mash Ctrl+Alt without mic permission.</summary>
    private DateTime _lastMicrophoneNeededHintUtc = DateTime.MinValue;

    /// <summary>Throttles the "speech service stalled" toast when fallback closes a stuck session.</summary>
    private DateTime _lastAssemblyAiStallHintUtc = DateTime.MinValue;

    private readonly AudioCaptureService _audioCapture;
    private readonly GlobalHotkeyMonitor _hotkeyMonitor;
    private readonly OverlayWindowManager _overlayManager;

    // ── Push-to-talk state ────────────────────────────────────────────────────

    private bool _isRecording;
    private bool _isFinalizingTranscript;
    private bool _isPreparing;
    private IStreamingTranscriptionSession? _activeSession;
    private CancellationTokenSource? _responseTaskCts;
    private CancellationTokenSource? _transientHideCts;

    // ── Conversation history ──────────────────────────────────────────────────

    private readonly List<(string UserTranscript, string AssistantResponse)> _history = new();

    // ── Fallback delay timer ──────────────────────────────────────────────────

    private DispatcherTimer? _finalTranscriptFallbackTimer;

    // ── Permission poll timer ─────────────────────────────────────────────────

    private DispatcherTimer? _permissionTimer;

    // ── Music ─────────────────────────────────────────────────────────────────

    private NAudio.Wave.WaveOutEvent? _musicPlayer;
    private NAudio.Wave.Mp3FileReader? _musicReader;
    private DispatcherTimer? _musicFadeTimer;
    // Music volume tracks the current playback level (used in fade-out)
#pragma warning disable CS0414
    private float _musicVolume = AppConstants.OnboardingMusicVolume;
#pragma warning restore CS0414

    // ── Constructor ───────────────────────────────────────────────────────────

    public CompanionManager()
    {
        Settings = AppSettings.Load();

        _claudeApi = new ClaudeApi(Settings.WorkerBaseUrl, Settings.SelectedClaudeModel);
        _assemblyAiProvider = new AssemblyAIStreamingProvider(Settings.WorkerBaseUrl);
        _ttsClient = new ElevenLabsTtsClient(Settings.WorkerBaseUrl);
        _audioCapture = new AudioCaptureService();
        _hotkeyMonitor = new GlobalHotkeyMonitor();
        _overlayManager = new OverlayWindowManager();

        _audioCapture.AudioDataAvailable += OnAudioDataAvailable;

        _hotkeyMonitor.ShortcutTransition += OnShortcutTransition;
    }

    /// <summary>
    /// Re-points HTTP gateways at <see cref="AppSettings.WorkerBaseUrl"/>.
    /// Previously settings were saved without recreating clients, so stale URLs pointed at the placeholder.
    /// </summary>
    private void RebindApiClients(string reason)
    {
        var baseUrl = Settings.WorkerBaseUrl.Trim().TrimEnd('/');
        Console.WriteLine($"[Clicky] Rebinding API clients ({reason}): {baseUrl}");
        AppDebugLog.Write($"RebindApiClients ({reason}): {baseUrl}");

        _claudeApi.Dispose();
        _ttsClient.Dispose();

        _claudeApi = new ClaudeApi(baseUrl, Settings.SelectedClaudeModel);
        _assemblyAiProvider = new AssemblyAIStreamingProvider(baseUrl);
        _ttsClient = new ElevenLabsTtsClient(baseUrl);

        _ = _claudeApi;
    }

    private static bool IsWorkerUrlProbablyConfigured(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var t = url.Trim();
        if (t.Contains("your-worker-name", StringComparison.OrdinalIgnoreCase)
            || t.Contains("your-subdomain", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u))
            return false;
        return u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start()
    {
        AppDebugLog.Write("=== CompanionManager.Start()");
        RefreshPermissions();
        StartPermissionPolling();

        if (!IsWorkerUrlProbablyConfigured(Settings.WorkerBaseUrl))
        {
            WpfApp.Current.Dispatcher.InvokeAsync(static () =>
                System.Windows.MessageBox.Show(
                    "Clicky Worker URL is not configured (still the template URL, or not a valid https URL).\n\n" +
                    "Click the tray icon → set Worker URL to your deployed worker (e.g. https://clicky-proxy.yourname.workers.dev) → Save.\n" +
                    "Then try Ctrl+Alt again.",
                    "Clicky",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning));
        }

        RebindApiClients(nameof(Start));
        AppDebugLog.Write($"Loaded worker URL: {Settings.WorkerBaseUrl}");

        if (Settings.HasCompletedOnboarding && Settings.IsClickyCursorEnabled)
        {
            _overlayManager.HasShownOverlayBefore = true;
            _overlayManager.ShowOverlay(this);
            IsOverlayVisible = true;
            OverlayVisibilityChanged?.Invoke();
        }

        // Hotkey must not depend on mic permission: permission can flip after startup; previously the hook
        // only started on a mic transition and could never install, so Ctrl+Alt appeared "dead".
        _hotkeyMonitor.Start();
        AppDebugLog.Write("Global hotkey hook started (Ctrl+Alt).");
    }

    public void Stop()
    {
        _hotkeyMonitor.Stop();
        AbortActiveResponseWorkflow();
        _pttPrepareAbortSource?.Cancel();
        _pttPrepareAbortSource?.Dispose();
        _pttPrepareAbortSource = null;
        _permissionTimer?.Stop();
        StopRecordingSession();
        _overlayManager.HideOverlay();
        StopOnboardingMusic();
    }

    public void SetSelectedModel(string model)
    {
        Settings.SelectedClaudeModel = model;
        Settings.Save();
        _claudeApi.Model = model;
    }

    public void SetWorkerBaseUrl(string url)
    {
        Settings.WorkerBaseUrl = url.Trim().TrimEnd('/');
        Settings.Save();
        RebindApiClients(nameof(SetWorkerBaseUrl));
    }

    /// <summary>Cancels and disposes the Claude/TTS pipeline token source so timeouts and interruptions don't leak.</summary>
    private void AbortActiveResponseWorkflow()
    {
        if (_responseTaskCts == null) return;
        try { _responseTaskCts.Cancel(); }
        catch (ObjectDisposedException) { /* no-op */ }

        _responseTaskCts = null;
        // CTS is owned/disposed by the Task.Run lambda's finally once the pipeline exits.
    }

    public void SetClickyCursorEnabled(bool enabled)
    {
        Settings.IsClickyCursorEnabled = enabled;
        Settings.Save();

        _transientHideCts?.Cancel();
        _transientHideCts = null;

        if (enabled)
        {
            _overlayManager.HasShownOverlayBefore = true;
            _overlayManager.ShowOverlay(this);
            IsOverlayVisible = true;
        }
        else
        {
            _overlayManager.HideOverlay();
            IsOverlayVisible = false;
        }

        OverlayVisibilityChanged?.Invoke();
    }

    public void ClearDetectedElement()
    {
        _detectedElementCanvasPoint = null;
        _detectedElementDisplayFrame = null;
        DetectedElementBubbleText = null;
    }

    public void ClearOnboardingPrompt()
    {
        ShowOnboardingPrompt = false;
        OnboardingPromptChanged?.Invoke();
    }

    // ── Permissions ───────────────────────────────────────────────────────────

    public void RefreshPermissions()
    {
        bool prev = HasMicrophonePermission;
        HasMicrophonePermission = CheckMicrophonePermission();

        if (prev != HasMicrophonePermission)
            PermissionsChanged?.Invoke();
    }

    public void RequestMicrophonePermission()
    {
        Task.Run(() =>
        {
            try { _ = NAudio.Wave.WaveIn.DeviceCount; }
            catch { }
            WpfApp.Current.Dispatcher.Invoke(RefreshPermissions);
        });
    }

    private bool CheckMicrophonePermission()
    {
        try { return NAudio.Wave.WaveIn.DeviceCount > 0; }
        catch { return false; }
    }

    private void StartPermissionPolling()
    {
        _permissionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _permissionTimer.Tick += (_, _) => RefreshPermissions();
        _permissionTimer.Start();
    }

    // ── Hotkey transitions ────────────────────────────────────────────────────

    private void OnShortcutTransition(HotkeyTransition transition)
    {
        switch (transition)
        {
            case HotkeyTransition.Pressed:
                HandleHotkeyPressed();
                break;

            case HotkeyTransition.Released:
                HandleHotkeyReleased();
                break;
        }
    }

    private void HandleHotkeyPressed()
    {
        if (_isRecording) return;
        if (ShowOnboardingVideo) return;

        if (!HasMicrophonePermission)
        {
            WarnMicrophoneNeededThrottled();
            return;
        }

        // Cancel transient hide — user is interacting
        _transientHideCts?.Cancel();
        _transientHideCts = null;

        // Bring overlay back if cursor mode is off but user spoke
        if (!Settings.IsClickyCursorEnabled && !IsOverlayVisible)
        {
            _overlayManager.HasShownOverlayBefore = true;
            _overlayManager.ShowOverlay(this);
            IsOverlayVisible = true;
            OverlayVisibilityChanged?.Invoke();
        }

        // Cancel any in-flight response
        AbortActiveResponseWorkflow();
        _ttsClient.StopPlayback();
        ClearDetectedElement();
        DetectedElementChanged?.Invoke();
        _overlayManager.HideResponse();

        // Dismiss onboarding prompt if visible
        if (ShowOnboardingPrompt)
        {
            ShowOnboardingPrompt = false;
            OnboardingPromptChanged?.Invoke();
        }

        StartPushToTalkSession();
    }

    private void HandleHotkeyReleased()
    {
        StopPushToTalkSession();
    }

    // ── Push-to-talk audio session ────────────────────────────────────────────

    private void StartPushToTalkSession()
    {
        if (!HasMicrophonePermission) return;

        _pttPrepareAbortSource?.Cancel();
        _pttPrepareAbortSource?.Dispose();
        _pttPrepareAbortSource = new CancellationTokenSource();
        var handshakeAbortToken = _pttPrepareAbortSource.Token;

        var prepareId = Guid.NewGuid();
        _pttPrepareId = prepareId;

        _isPreparing = true;
        VoiceState = VoiceState.Processing;

        Task.Run(async () =>
        {
            using var prepareTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            using var linkedLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                handshakeAbortToken,
                prepareTimeout.Token);
            var ct = linkedLifetime.Token;

            try
            {
                AppDebugLog.Write("PTT: fetching streaming token + opening AssemblyAI websocket…");

                var session = await _assemblyAiProvider.StartSessionAsync(
                    keyterms: BuildKeyterms(),
                    onTranscriptUpdate: _ => { },
                    onFinalTranscriptReady: OnFinalTranscriptReady,
                    onError: OnTranscriptionError,
                    ct).ConfigureAwait(false);

                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_pttPrepareId != prepareId)
                    {
                        session.Cancel();
                        return;
                    }

                    if (handshakeAbortToken.IsCancellationRequested)
                    {
                        session.Cancel();
                        AppDebugLog.Write("PTT: handshake finished but keys were released — discarding stray session attach.");
                        _isPreparing = false;
                        VoiceState = VoiceState.Idle;
                        ScheduleTransientHideIfNeeded();
                        return;
                    }

                    _activeSession = session;
                    _isRecording = true;
                    _isPreparing = false;
                    _audioChunksSent = 0;

                    ReleasePrepareAbortHandlesCommit();

                    // Switch to waveform ASAP; microphone errors are handled separately below.
                    VoiceState = VoiceState.Listening;

                    try
                    {
                        _audioCapture.Start();
                        AppDebugLog.Write("AssemblyAI session ready → recording (listening).");

                        // Watchdog: if no audio chunks arrive within 2s the capture device is
                        // silently broken (driver/exclusive-mode issue).
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000).ConfigureAwait(false);
                            if (_isRecording && _audioChunksSent == 0)
                            {
                                AppDebugLog.Write("AudioCapture WATCHDOG: 2s elapsed, 0 chunks — mic not delivering audio.");
                                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (!_isRecording) return;
                                    ShowDiagnostic(
                                        "Microphone is not delivering audio.\r\n\r\n" +
                                        "• Windows Settings → Privacy → Microphone → allow desktop apps\r\n" +
                                        "• Close Teams, Discord, or any app with exclusive mic access\r\n" +
                                        "• Check recording device volume in Windows Sound settings",
                                        isError: true);
                                });
                            }
                        });
                    }
                    catch (Exception micEx)
                    {
                        AppDebugLog.Write($"PTT: microphone pipeline failed after session opened: {micEx}");
                        _activeSession?.Cancel();
                        _activeSession = null;
                        _isRecording = false;
                        VoiceState = VoiceState.Idle;
                        ShowDiagnostic(
                            $"Microphone could not start recording.\r\n\r\n{micEx.Message}\r\n\r\n" +
                            "Pick a working microphone in Windows Settings → System → Sound → Input, then try Ctrl+Alt again.",
                            isError: true);
                    }
                }, DispatcherPriority.Send);
            }
            catch (OperationCanceledException)
            {
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_pttPrepareId != prepareId)
                    {
                        AppDebugLog.Write("PTT: stale prepare task exited (another PTT superseded it).");
                        return;
                    }

                    _isPreparing = false;
                    VoiceState = VoiceState.Idle;

                    var timedOutOverall = prepareTimeout.Token.IsCancellationRequested;
                    var abortedEarly = handshakeAbortToken.IsCancellationRequested;

                    if (timedOutOverall)
                    {
                        AppDebugLog.Write("PTT: prepare timed out (75s) waiting for Worker + AssemblyAI.");
                        ShowDiagnostic(
                            "Connecting to the speech service took too long.\r\n\r\n" +
                            "Check Internet, VPN/firewall (outbound WebSocket TLS to streaming.assemblyai.com), Worker URL,\r\n" +
                            "and `clicky-debug.log` under %AppData%\\Roaming\\Clicky\\.",
                            isError: true);
                        return;
                    }

                    if (abortedEarly)
                    {
                        AppDebugLog.Write(
                            "PTT: released Ctrl+Alt while still connecting. Hold both keys until you see the waveform, then speak.");
                        ScheduleTransientHideIfNeeded();
                        return;
                    }

                    AppDebugLog.Write("PTT: prepare canceled.");
                    ScheduleTransientHideIfNeeded();
                }, DispatcherPriority.Send);
            }
            catch (TimeoutException tex)
            {
                Console.WriteLine($"[PTT] Timeout: {tex.Message}");
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_pttPrepareId != prepareId)
                        return;

                    _isPreparing = false;
                    VoiceState = VoiceState.Idle;
                    ShowDiagnostic(
                        $"Speech WebSocket timed out:\r\n{tex.Message}\r\n\r\n" +
                        "Verify your Worker exposes POST /transcribe-token with a valid AssemblyAI key. " +
                        "If you are on restrictive Wi‑Fi, try another network or VPN.",
                        isError: true);
                }, DispatcherPriority.Send);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PTT] Failed to start session: {ex.Message}");
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_pttPrepareId != prepareId)
                        return;

                    _isPreparing = false;
                    VoiceState = VoiceState.Idle;
                    ShowDiagnostic(
                        $"Could not start transcription (microphone/network or Worker).\r\n\r\n{ex.Message}\r\n\r\n" +
                        $"Check your Worker URL in the tray menu and verify AssemblyAI/ElevenLabs secrets on the Worker.",
                        isError: true);
                }, DispatcherPriority.Send);
            }
        });
    }

    /// <summary>Handshake finished and session is attaching or attached — disposed so release during record doesn't cancel unrelated work.</summary>
    private void ReleasePrepareAbortHandlesCommit()
    {
        try
        {
            _pttPrepareAbortSource?.Dispose();
        }
        catch (ObjectDisposedException) { /* no-op */ }

        _pttPrepareAbortSource = null;
    }

    private void StopPushToTalkSession()
    {
        if (!_isRecording && !_isPreparing) return;

        if (_isPreparing)
            _pttPrepareAbortSource?.Cancel();

        _isRecording = false;
        _isPreparing = false;
        _isFinalizingTranscript = true;
        VoiceState = VoiceState.Processing;

        _audioCapture.Stop();

        AppDebugLog.Write($"PTT key-up: chunks sent={_audioChunksSent} peakLevel={AudioPowerLevel:F3}");

        // If the user releases before the WebSocket session is assigned, RequestFinalTranscript is a no-op and
        // OnFinalTranscriptReady will never run — do NOT start the stall timer (it would false-positive every time).
        var session = _activeSession;
        session?.RequestFinalTranscript();

        if (session == null)
        {
            AppDebugLog.Write(
                "PTT: stopped before session attached (released early or spurious Ctrl+Alt up while connecting). " +
                "Hold until the waveform; no transcript to finalize.");
            _isFinalizingTranscript = false;
            VoiceState = VoiceState.Idle;
            ScheduleTransientHideIfNeeded();
            return;
        }

        // Fallback: if AssemblyAI doesn't deliver a final transcript in time, give up
        _finalTranscriptFallbackTimer?.Stop();
        _finalTranscriptFallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(AppConstants.AssemblyAiFallbackDelaySeconds)
        };
        _finalTranscriptFallbackTimer.Tick += (_, _) =>
        {
            _finalTranscriptFallbackTimer.Stop();
            if (!_isFinalizingTranscript) return;

            AppDebugLog.Write(
                $"AssemblyAI: fallback timer expired ({AppConstants.AssemblyAiFallbackDelaySeconds}s) before final transcript.");

            try { _activeSession?.Cancel(); }
            catch (Exception ex) { AppDebugLog.Write($"AssemblyAI Cancel after stall: {ex.Message}"); }
            finally { _activeSession = null; }

            _isFinalizingTranscript = false;
            VoiceState = VoiceState.Idle;
            ScheduleTransientHideIfNeeded();
            WarnAssemblyAiStallThrottled();
        };
        _finalTranscriptFallbackTimer.Start();
    }

    private void StopRecordingSession()
    {
        _isRecording = false;
        _isPreparing = false;
        _isFinalizingTranscript = false;
        _audioCapture.Stop();
        _activeSession?.Cancel();
        _activeSession = null;
        _finalTranscriptFallbackTimer?.Stop();
    }

    // ── Audio data pipeline ───────────────────────────────────────────────────

    private int _audioChunksSent;

    private void OnAudioDataAvailable(byte[] pcm16Data)
    {
        var session = _activeSession;
        if (session == null) return;

        session.AppendAudioData(pcm16Data);

        var count = System.Threading.Interlocked.Increment(ref _audioChunksSent);
        if (count == 1 || count % 50 == 0)
            AppDebugLog.Write($"Audio→AssemblyAI chunk #{count} ({pcm16Data.Length} bytes)");

        var level = _audioCapture.AudioPowerLevel;
        WpfApp.Current.Dispatcher.InvokeAsync(() =>
        {
            AudioPowerLevel = level;
            AudioPowerLevelChanged?.Invoke(level);
        });
    }

    private void OnFinalTranscriptReady(string transcript)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            _finalTranscriptFallbackTimer?.Stop();
            _isFinalizingTranscript = false;
            _activeSession = null;

            var trimmed = transcript.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                AppDebugLog.Write("Transcript empty from AssemblyAI.");
                VoiceState = VoiceState.Idle;
                ScheduleTransientHideIfNeeded();
                return;
            }

            LastTranscript = trimmed;
            Console.WriteLine($"🗣️ Transcript: {trimmed}");
            AppDebugLog.Write($"Final transcript ({trimmed.Length} chars): {trimmed}");
            SendTranscriptToClaudeWithScreenshot(trimmed);
        });
    }

    private void WarnAssemblyAiStallThrottled()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastAssemblyAiStallHintUtc).TotalSeconds < 25)
            return;
        _lastAssemblyAiStallHintUtc = now;

        var logPath = IoPath.Combine(AppConstants.SettingsDirectory, "clicky-debug.log");
        var msg =
            "Clicky timed out waiting for a finished transcript from the speech service.\r\n\r\n" +
            "Keep Ctrl+Alt held until you see the waveform (listening), speak, then release.\r\n" +
            "If it still fails: check mic input, Worker URL, and the log file:\r\n" +
            logPath;

        ShowDiagnostic(msg, isError: false);
    }

    private void WarnMicrophoneNeededThrottled()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMicrophoneNeededHintUtc).TotalSeconds < 15)
            return;
        _lastMicrophoneNeededHintUtc = now;

        const string msg =
            "Microphone access is required before Clicky can listen.\r\n\r\n" +
            "Open the tray menu and use Grant next to Microphone, or enable the app under " +
            "Windows Settings → Privacy → Microphone.";

        AppDebugLog.Write("Ctrl+Alt ignored: microphone permission not granted.");
        ShowDiagnostic(msg, isError: false);
    }

    private static void ShowDiagnostic(string message, bool isError = false)
    {
        var icon = isError ? System.Windows.MessageBoxImage.Error : System.Windows.MessageBoxImage.Warning;
        try
        {
            if (WpfApp.Current.Dispatcher.CheckAccess())
                System.Windows.MessageBox.Show(message, "Clicky", System.Windows.MessageBoxButton.OK, icon);
            else
                WpfApp.Current.Dispatcher.Invoke(() =>
                    System.Windows.MessageBox.Show(message, "Clicky", System.Windows.MessageBoxButton.OK, icon));
        }
        catch
        {
            Console.WriteLine($"[Clicky diagnostic] {message}");
        }
    }

    private void OnTranscriptionError(Exception ex)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            Console.WriteLine($"⚠️ Transcription error: {ex.Message}");
            _isFinalizingTranscript = false;
            _activeSession = null;
            VoiceState = VoiceState.Idle;
            ScheduleTransientHideIfNeeded();
            ShowDiagnostic($"Transcription error:\n{ex.Message}", isError: true);
        });
    }

    // ── AI response pipeline ──────────────────────────────────────────────────

    /// <summary>
    /// Mirrors Swift sendTranscriptToClaudeWithScreenshot:
    /// capture → Claude streaming → point tag parsing → coordinate translation → TTS.
    /// </summary>
    private void SendTranscriptToClaudeWithScreenshot(string transcript)
    {
        AbortActiveResponseWorkflow();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        _responseTaskCts = cts;

        VoiceState = VoiceState.Processing;
        AppDebugLog.Write($"Claude pipeline START — user transcript chars={transcript.Length}.");

        Task.Run(async () =>
        {
            try
            {
                // 1. Capture all screens
                var captures = await Task.Run(ScreenCaptureService.CaptureAllScreens, cts.Token);
                cts.Token.ThrowIfCancellationRequested();

                // 2. Build labeled image list for Claude
                var images = captures.Select(c => (
                    Data: c.ImageBytes,
                    Label: $"{c.Label} (image dimensions: {c.ScreenshotWidthPx}x{c.ScreenshotHeightPx} pixels)"
                )).ToList();

                AppDebugLog.Write($"Claude vision request — captures={captures.Count}.");

                // 3. Build conversation history
                var history = _history
                    .Select(h => (UserPlaceholder: h.UserTranscript, AssistantResponse: h.AssistantResponse))
                    .ToList();

                // 4. Claude (streaming + non-stream fallback)
                string fullText;
                try
                {
                    fullText = await _claudeApi.AnalyzeVisionTextAsync(
                        images: images,
                        systemPrompt: CompanionSystemPrompt,
                        conversationHistory: history,
                        userPrompt: transcript,
                        onTextChunk: _ => { },
                        cancellationToken: cts.Token);
                }
                catch (Exception apiEx)
                {
                    AppDebugLog.Write($"Claude pipeline exception: {apiEx}");
                    throw;
                }

                AppDebugLog.Write($"Claude returned text chars={fullText.Trim().Length}.");

                if (string.IsNullOrWhiteSpace(fullText))
                {
                    AppDebugLog.Write("Claude returned EMPTY text after stream+fallback.");
                    await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                        ShowDiagnostic(
                            "Claude returned an empty reply. Check Worker logs Anthropic/ElevenLabs/Assembly secrets, model name, or open\n" +
                            IoPath.Combine(AppConstants.SettingsDirectory, "clicky-debug.log"),
                            isError: true));
                }

                cts.Token.ThrowIfCancellationRequested();

                // 5. Parse [POINT:...] tag and [ACTION:...] tags
                var parseResult  = PointingParseResult.Parse(fullText);
                var actionResult = ActionTagParser.Parse(parseResult.SpokenText);

                // Guarantee spokenText is never empty — Claude sometimes returns only tags
                var spokenText = BuildSpokenText(actionResult);

                // 6. Show response in the blue NavBubble
                WpfApp.Current.Dispatcher.Invoke(() =>
                    _overlayManager.ShowResponse(spokenText));

                // 7. Coordinate translation — switch to UI thread for state mutation
                await WpfApp.Current.Dispatcher.InvokeAsync(() =>
                {
                    var pointResult = PointingParseResult.Parse(fullText);
                    HandlePointingTag(pointResult, captures);
                });

                // 8. Update conversation history (initial turn only)
                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    _history.Add((transcript, spokenText));
                    if (_history.Count > AppConstants.MaxConversationHistory)
                        _history.RemoveAt(0);
                    Console.WriteLine($"🧠 Conversation history: {_history.Count} exchanges");
                });

                cts.Token.ThrowIfCancellationRequested();

                // 9. TTS for initial response — fire concurrently while actions run
                var ttsTask = RunTtsAsync(spokenText, cts.Token);

                // 10. Execute initial actions then run the agentic continuation loop
                if (actionResult.HasActions && !actionResult.IsDone)
                {
                    try
                    {
                        await RunAgentLoopAsync(transcript, actionResult, captures, cts.Token);
                    }
                    catch (OperationCanceledException) { /* normal cancellation */ }
                    catch (Exception agentEx)
                    {
                        AppDebugLog.Write($"Agent loop CRASHED: {agentEx.GetType().Name}: {agentEx.Message}\n{agentEx.StackTrace}");
                    }
                }

                await ttsTask;

                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        VoiceState = VoiceState.Idle;
                        ScheduleTransientHideIfNeeded();
                    }
                });
                AppDebugLog.Write("Claude pipeline END.");
            }
            catch (OperationCanceledException)
            {
                AppDebugLog.Write("[Response] Cancelled or timed out (Claude/TTS pipeline).");
                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    VoiceState = VoiceState.Idle;
                    ScheduleTransientHideIfNeeded();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Response error: {ex.Message}");
                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    VoiceState = VoiceState.Idle;
                    ScheduleTransientHideIfNeeded();
                    ShowDiagnostic(
                        $"Claude / Worker request failed:\n{ex.Message}\n\n" +
                        "If you just changed the Worker URL, save it again in the tray menu. ",
                        isError: true);
                });
            }
            finally
            {
                if (ReferenceEquals(_responseTaskCts, cts))
                    _responseTaskCts = null;

                try { cts.Dispose(); }
                catch (ObjectDisposedException) { /* no-op */ }
            }
        }, cts.Token);
    }

    // ── Point tag coordinate translation ─────────────────────────────────────

    /// <summary>
    /// Translates Claude's screenshot-pixel coordinates into Windows physical pixel
    /// coordinates, then into per-monitor DIP canvas coordinates for the overlay.
    ///
    /// Windows equivalent of the Swift coordinate math:
    ///   screenshotPx → physicalScreenPx (no Y-flip needed — top-left origin on both)
    ///   physicalScreenPx → canvas DIPs via per-monitor DPI scale
    /// </summary>
    private void HandlePointingTag(PointingParseResult result, List<ScreenCaptureData> captures)
    {
        if (result.Coordinate == null) return;

        // Select target screen: match Claude's :screenN tag or fall back to cursor screen
        ScreenCaptureData? target = null;
        if (result.ScreenNumber != null &&
            result.ScreenNumber >= 1 &&
            result.ScreenNumber <= captures.Count)
        {
            target = captures[result.ScreenNumber.Value - 1];
        }
        else
        {
            target = captures.FirstOrDefault(c => c.IsCursorScreen) ?? captures.FirstOrDefault();
        }

        if (target == null) return;

        var (cx, cy) = result.Coordinate.Value;

        // Clamp to screenshot bounds
        double clampedX = Math.Clamp(cx, 0, target.ScreenshotWidthPx);
        double clampedY = Math.Clamp(cy, 0, target.ScreenshotHeightPx);

        // Scale from screenshot pixel space to physical screen pixel space
        double scaleX = target.PhysicalBounds.Width  / (double)target.ScreenshotWidthPx;
        double scaleY = target.PhysicalBounds.Height / (double)target.ScreenshotHeightPx;

        double physicalLocalX = clampedX * scaleX;
        double physicalLocalY = clampedY * scaleY;

        // Translate to screen-relative DIP coords (for the overlay canvas)
        double canvasX = physicalLocalX / target.DpiScale;
        double canvasY = physicalLocalY / target.DpiScale;

        // Store for overlay to consume (in screen physical pixel space for multi-monitor check)
        _detectedElementCanvasPoint = new System.Windows.Point(canvasX, canvasY);
        _detectedElementDisplayFrame = target.PhysicalBounds;
        DetectedElementBubbleText = null; // use random phrase unless overridden

        Console.WriteLine($"🎯 Pointing at ({(int)cx},{(int)cy}) → canvas ({(int)canvasX},{(int)canvasY}) — \"{result.ElementLabel}\"");

        // Switch to idle so the triangle becomes visible before the flight animation
        VoiceState = VoiceState.Idle;
        DetectedElementChanged?.Invoke();
    }

    // ── Agent loop ────────────────────────────────────────────────────────────

    /// <summary>
    /// Agentic execution loop.
    ///
    /// Executes the actions Claude returned, then re-screenshots and calls Claude again
    /// with a continuation prompt until Claude signals [DONE] or returns no actions.
    /// This means each step sees the actual screen state after the previous step, so
    /// coordinates are always accurate — no pre-planning from a stale screenshot.
    ///
    /// Max <see cref="AppConstants.AgentMaxIterations"/> iterations to prevent runaway loops.
    /// </summary>
    private async Task RunAgentLoopAsync(
        string              originalTranscript,
        ActionParseResult   firstResult,
        List<ScreenCaptureData> initialCaptures,
        CancellationToken   ct)
    {
        var completedSteps = new List<string>();
        var current        = firstResult;
        var captures       = initialCaptures;
        int iteration      = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!current.HasActions || current.IsDone)
            {
                AppDebugLog.Write($"Agent loop: stopping (IsDone={current.IsDone} HasActions={current.HasActions})");
                break;
            }

            if (iteration >= AppConstants.AgentMaxIterations)
            {
                AppDebugLog.Write($"Agent loop: hit max iterations ({AppConstants.AgentMaxIterations}), stopping.");
                break;
            }

            // Execute this batch of actions; captures are refreshed between each step
            try
            {
                captures = await ExecuteActionsAsync(current.Actions, captures, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception stepEx)
            {
                AppDebugLog.Write($"Agent loop: ExecuteActionsAsync threw {stepEx.GetType().Name}: {stepEx.Message}");
                break;
            }
            iteration++;

            // Record what was done for the continuation context
            foreach (var a in current.Actions.Where(a => a is not ActionTag.Wait))
                completedSteps.Add(StepDescription(a));

            if (current.IsDone) break;

            // Pause so any triggered UI animation settles before we screenshot
            await Task.Delay(AppConstants.ActionStepPauseMs, ct);

            // Re-screenshot and ask Claude what to do next
            AppDebugLog.Write($"Agent loop: iteration {iteration} complete, asking Claude for next step.");
            captures = await Task.Run(ScreenCaptureService.CaptureAllScreens, ct);

            var images = captures.Select(c => (
                Data:  c.ImageBytes,
                Label: $"{c.Label} (image dimensions: {c.ScreenshotWidthPx}x{c.ScreenshotHeightPx} pixels)"
            )).ToList();

            string doneList = string.Join(", ", completedSteps);
            string contPrompt = $"original task: \"{originalTranscript}\"\nsteps completed so far: {doneList}\n\n" +
                                "look at the current screenshot. what is the single next step to complete the task? " +
                                "if the full task is now complete, say so and end with [DONE] — no action tags. " +
                                "otherwise give your brief spoken text + the next action tag(s). " +
                                "keep spoken text to one sentence.";

            string nextText;
            try
            {
                nextText = await _claudeApi.AnalyzeVisionTextAsync(
                    images:              images,
                    systemPrompt:        AgentStepSystemPrompt,
                    conversationHistory: null,
                    userPrompt:          contPrompt,
                    onTextChunk:         _ => { },
                    cancellationToken:   ct);
            }
            catch (Exception ex)
            {
                AppDebugLog.Write($"Agent loop: Claude call failed — {ex.Message}");
                break;
            }

            AppDebugLog.Write($"Agent loop iteration {iteration}: Claude returned {nextText.Length} chars");

            current = ActionTagParser.Parse(nextText);

            if (!string.IsNullOrWhiteSpace(current.SpokenText))
            {
                // Stop previous agent-step TTS before speaking the next line
                _ttsClient.StopPlayback();
                WpfApp.Current.Dispatcher.Invoke(() =>
                    _overlayManager.ShowResponse(current.SpokenText));
                _ = RunTtsAsync(current.SpokenText, ct);
            }
        }
    }

    private static string BuildSpokenText(ActionParseResult r)
    {
        if (!string.IsNullOrWhiteSpace(r.SpokenText)) return r.SpokenText;
        return r.Action switch
        {
            ActionTag.Click      c  => c.RightClick ? "right-clicking." : "clicking.",
            ActionTag.DoubleClick _  => "opening that.",
            ActionTag.Type       _  => "typing that for you.",
            ActionTag.Open       o  => $"opening {o.AppName}.",
            ActionTag.Navigate   _  => "navigating there.",
            ActionTag.KeyPress   k  => $"pressing {k.Combo}.",
            null                    => "done.",
            _                       => "on it."
        };
    }

    private async Task RunTtsAsync(string text, CancellationToken ct)
    {
        AppDebugLog.Write($"TTS: speaking chars={text.Trim().Length}.");
        try
        {
            await _ttsClient.SpeakAsync(text, ct);
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                if (!ct.IsCancellationRequested)
                    VoiceState = VoiceState.Responding;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"⚠️ TTS error: {ex.Message}");
            ElevenLabsTtsClient.SpeakFallback(
                "I'm all out of credits. Please tell whoever set this up to top them up.");
            WpfApp.Current.Dispatcher.Invoke(() =>
                VoiceState = VoiceState.Responding);
        }
    }

    // ── Action execution ──────────────────────────────────────────────────────

    /// <summary>
    /// Executes all action steps in the list sequentially.
    /// Pauses and re-screenshots between steps so subsequent CLICK coordinates
    /// reflect the actual screen state.
    /// Returns the most recent captures (after the last re-screenshot).
    /// </summary>
    private async Task<List<ScreenCaptureData>> ExecuteActionsAsync(
        List<ActionTag> actions,
        List<ScreenCaptureData> captures,
        CancellationToken ct)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var action = actions[i];

            if (action is ActionTag.Wait wait)
            {
                AppDebugLog.Write($"Action step {i + 1}/{actions.Count}: waiting {wait.Milliseconds}ms");
                await Task.Delay(wait.Milliseconds, ct);
                captures = await Task.Run(ScreenCaptureService.CaptureAllScreens, ct);
                continue;
            }

            string label = StepLabel(action, i + 1, actions.Count);
            AppDebugLog.Write($"Action step {i + 1}/{actions.Count}: {label}");

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            WpfApp.Current.Dispatcher.Invoke(() =>
                _overlayManager.ShowActionToast(label, AppConstants.ActionToastHoldMs, () => tcs.TrySetResult(true)));
            await tcs.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();

            try
            {
                ExecuteStep(action, captures);
            }
            catch (Exception stepEx)
            {
                AppDebugLog.Write($"ExecuteStep failed ({action.GetType().Name}): {stepEx.GetType().Name}: {stepEx.Message}");
                // Don't abort the whole loop for a single step failure — log and continue
            }

            bool moreActionsAhead = actions.Skip(i + 1).Any(a => a is not ActionTag.Wait);
            if (moreActionsAhead)
            {
                int pauseMs = AppConstants.ActionStepPauseMs;

                if (i + 1 < actions.Count && actions[i + 1] is ActionTag.Wait nextWait)
                {
                    pauseMs = nextWait.Milliseconds;
                    i++;
                }

                AppDebugLog.Write($"Action step {i + 1}/{actions.Count}: pausing {pauseMs}ms then re-screenshotting");
                await Task.Delay(pauseMs, ct);
                captures = await Task.Run(ScreenCaptureService.CaptureAllScreens, ct);
                AppDebugLog.Write("Action step: fresh screenshot captured after pause");
            }
        }

        return captures;
    }

    private static string StepLabel(ActionTag action, int step, int total)
    {
        string suffix = total > 1 ? $" ({step}/{total})" : "";
        return action switch
        {
            ActionTag.Click      c  => c.RightClick ? $"right-clicking...{suffix}" : $"clicking...{suffix}",
            ActionTag.DoubleClick _  => $"opening...{suffix}",
            ActionTag.Type       t  => $"typing: {t.Text.Truncate(28)}{suffix}",
            ActionTag.Open       o  => $"opening {o.AppName}...{suffix}",
            ActionTag.Navigate   n  => $"navigating to {n.Url.Truncate(30)}...{suffix}",
            ActionTag.KeyPress   k  => $"pressing {k.Combo}{suffix}",
            _                       => $"executing...{suffix}"
        };
    }

    private static string StepDescription(ActionTag action) => action switch
    {
        ActionTag.Click      c  => c.RightClick ? $"right-clicked ({c.X},{c.Y})" : $"clicked ({c.X},{c.Y})",
        ActionTag.DoubleClick d => $"double-clicked ({d.X},{d.Y})",
        ActionTag.Type       t  => $"typed \"{t.Text.Truncate(20)}\"",
        ActionTag.Open       o  => $"opened {o.AppName}",
        ActionTag.Navigate   n  => $"navigated to {n.Url.Truncate(40)}",
        ActionTag.KeyPress   k  => $"pressed {k.Combo}",
        _                       => "executed"
    };

    private static void ExecuteStep(ActionTag action, List<ScreenCaptureData> captures)
    {
        switch (action)
        {
            case ActionTag.Click click:
                var ct1 = captures.FirstOrDefault(c => c.IsCursorScreen) ?? captures.FirstOrDefault();
                if (ct1 != null)
                {
                    double sx1 = ct1.PhysicalBounds.Width  / (double)ct1.ScreenshotWidthPx;
                    double sy1 = ct1.PhysicalBounds.Height / (double)ct1.ScreenshotHeightPx;
                    ActionExecutor.Click(
                        ct1.PhysicalBounds.X + (int)(click.X * sx1),
                        ct1.PhysicalBounds.Y + (int)(click.Y * sy1),
                        click.RightClick);
                }
                break;

            case ActionTag.DoubleClick dblClick:
                var ct2 = captures.FirstOrDefault(c => c.IsCursorScreen) ?? captures.FirstOrDefault();
                if (ct2 != null)
                {
                    double sx2 = ct2.PhysicalBounds.Width  / (double)ct2.ScreenshotWidthPx;
                    double sy2 = ct2.PhysicalBounds.Height / (double)ct2.ScreenshotHeightPx;
                    ActionExecutor.DoubleClick(
                        ct2.PhysicalBounds.X + (int)(dblClick.X * sx2),
                        ct2.PhysicalBounds.Y + (int)(dblClick.Y * sy2));
                }
                break;

            case ActionTag.Type type:
                ActionExecutor.Type(type.Text);
                break;

            case ActionTag.Open open:
                ActionExecutor.OpenApp(open.AppName);
                break;

            case ActionTag.Navigate nav:
                ActionExecutor.Navigate(nav.Url);
                break;

            case ActionTag.KeyPress key:
                ActionExecutor.PressKey(key.Combo);
                break;
        }
    }

    // ── Transient hide ────────────────────────────────────────────────────────

    private void ScheduleTransientHideIfNeeded()
    {
        if (Settings.IsClickyCursorEnabled || !IsOverlayVisible) return;

        _transientHideCts?.Cancel();
        _transientHideCts = new CancellationTokenSource();
        var cts = _transientHideCts;

        Task.Run(async () =>
        {
            // Wait for TTS to finish
            while (_ttsClient.IsPlaying && !cts.IsCancellationRequested)
                await Task.Delay(200, cts.Token).ContinueWith(_ => { });

            if (cts.IsCancellationRequested) return;

            // Wait for pointing animation (detected element cleared by overlay)
            while (HasDetectedElement && !cts.IsCancellationRequested)
                await Task.Delay(200, cts.Token).ContinueWith(_ => { });

            if (cts.IsCancellationRequested) return;

            // 1-second pause then fade out
            await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
            if (cts.IsCancellationRequested) return;

            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                _overlayManager.FadeOutAndHideOverlay();
                IsOverlayVisible = false;
                OverlayVisibilityChanged?.Invoke();
            });
        });
    }

    // ── Onboarding ────────────────────────────────────────────────────────────

    public void TriggerOnboarding()
    {
        Settings.HasCompletedOnboarding = true;
        Settings.Save();

        StartOnboardingMusic();

        _overlayManager.ShowOverlay(this);
        IsOverlayVisible = true;
        OverlayVisibilityChanged?.Invoke();
    }

    public void ReplayOnboarding()
    {
        StartOnboardingMusic();
        _overlayManager.HasShownOverlayBefore = false;
        _overlayManager.ShowOverlay(this);
        IsOverlayVisible = true;
        OverlayVisibilityChanged?.Invoke();
    }

    public void SetupOnboardingVideo()
    {
        OnboardingVideoUri = new Uri(AppConstants.OnboardingVideoUrl);
        ShowOnboardingVideo = true;
        OnboardingVideoChanged?.Invoke();

        // At 40s trigger the demo interaction
        var startTime = DateTime.UtcNow;
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(AppConstants.OnboardingDemoTriggerSeconds));
            PerformOnboardingDemoInteraction();
        });

        // After video ends (estimate HLS duration or wait for event), show prompt
        // The overlay calls TearDownOnboardingVideo via MediaElement.MediaEnded
    }

    public void TearDownOnboardingVideo()
    {
        ShowOnboardingVideo = false;
        OnboardingVideoUri = null;
        OnboardingVideoChanged?.Invoke();

        Task.Delay(300).ContinueWith(_ =>
        {
            WpfApp.Current.Dispatcher.Invoke(() =>
            {
                ShowOnboardingPrompt = true;
                OnboardingPromptChanged?.Invoke();

                // Auto-dismiss the prompt after 10s
                Task.Delay(TimeSpan.FromSeconds(AppConstants.OnboardingPromptAutoDismissSeconds))
                    .ContinueWith(_ => WpfApp.Current.Dispatcher.Invoke(() =>
                    {
                        if (ShowOnboardingPrompt)
                        {
                            ShowOnboardingPrompt = false;
                            OnboardingPromptChanged?.Invoke();
                        }
                    }));
            });
        });
    }

    private void PerformOnboardingDemoInteraction()
    {
        Task.Run(async () =>
        {
            try
            {
                var captures = ScreenCaptureService.CaptureAllScreens();
                var cursor = captures.FirstOrDefault(c => c.IsCursorScreen) ?? captures.FirstOrDefault();
                if (cursor == null) return;

                var images = new List<(byte[] Data, string Label)>
                {
                    (cursor.ImageBytes,
                     $"{cursor.Label} (image dimensions: {cursor.ScreenshotWidthPx}x{cursor.ScreenshotHeightPx} pixels)")
                };

                var (fullText, _) = await _claudeApi.AnalyzeImageStreamingAsync(
                    images: images,
                    systemPrompt: OnboardingDemoSystemPrompt,
                    conversationHistory: null,
                    userPrompt: "look around my screen and find something interesting to point at",
                    onTextChunk: _ => { });

                var parseResult = PointingParseResult.Parse(fullText);
                if (parseResult.Coordinate == null) return;

                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    HandlePointingTag(parseResult, captures);
                    DetectedElementBubbleText = parseResult.SpokenText;
                    Console.WriteLine($"🎯 Onboarding demo: \"{parseResult.SpokenText}\"");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Onboarding demo error: {ex.Message}");
            }
        });
    }

    // ── Onboarding music ──────────────────────────────────────────────────────

    private void StartOnboardingMusic()
    {
        StopOnboardingMusic();

        var musicPath = IoPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", AppConstants.OnboardingMusicFileName);
        if (!IoFile.Exists(musicPath)) return;

        try
        {
            _musicReader = new NAudio.Wave.Mp3FileReader(musicPath);
            _musicPlayer = new NAudio.Wave.WaveOutEvent();
            _musicPlayer.Init(_musicReader);
            _musicPlayer.Volume = AppConstants.OnboardingMusicVolume;
            _musicPlayer.Play();

            // Fade out after 90s
            _musicFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AppConstants.OnboardingMusicFadeStartSeconds) };
            _musicFadeTimer.Tick += (_, _) =>
            {
                _musicFadeTimer.Stop();
                FadeOutOnboardingMusic();
            };
            _musicFadeTimer.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Music: {ex.Message}");
        }
    }

    private void FadeOutOnboardingMusic()
    {
        if (_musicPlayer == null) return;

        const int Steps = 30;
        double stepInterval = AppConstants.OnboardingMusicFadeDurationSeconds / Steps;
        float startVolume = _musicPlayer.Volume;
        float decrement = startVolume / Steps;
        int stepsLeft = Steps;

        var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(stepInterval) };
        fadeTimer.Tick += (_, _) =>
        {
            stepsLeft--;
            if (_musicPlayer != null)
                _musicPlayer.Volume = Math.Max(0, _musicPlayer.Volume - decrement);

            if (stepsLeft <= 0)
            {
                fadeTimer.Stop();
                StopOnboardingMusic();
            }
        };
        fadeTimer.Start();
    }

    private void StopOnboardingMusic()
    {
        _musicFadeTimer?.Stop();
        _musicFadeTimer = null;
        _musicPlayer?.Stop();
        _musicPlayer?.Dispose();
        _musicPlayer = null;
        _musicReader?.Dispose();
        _musicReader = null;
    }

    // ── Keyterms ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> BuildKeyterms() =>
    [
        "Clicky", "Claude", "Anthropic", "OpenAI", "SwiftUI",
        "Xcode", "Vercel", "Next.js", "localhost", "Visual Studio"
    ];

    // ── System prompts ────────────────────────────────────────────────────────

    private const string CompanionSystemPrompt = """
        you're clicky, a friendly always-on companion that lives in the user's system tray. the user just spoke to you via push-to-talk and you can see their screen(s). your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation — you remember everything they've said before.

        rules:
        - default to one or two sentences. be direct and dense. BUT if the user asks you to explain more, go deeper, or elaborate, then go all out — give a thorough, detailed explanation with no length limit.
        - all lowercase, casual, warm. no emojis.
        - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, or formatting — just natural speech.
        - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
        - if the user's question relates to what's on their screen, reference specific things you see.
        - if the screenshot doesn't seem relevant to their question, just answer the question directly.
        - you can help with anything — coding, writing, general knowledge, brainstorming.
        - never say "simply" or "just".
        - don't read out code verbatim. describe what the code does or what needs to change conversationally.
        - focus on giving a thorough, useful explanation. don't end with simple yes/no questions like "want me to explain more?" or "should i show you?" — those are dead ends that force the user to just say yes.
        - instead, when it fits naturally, end by planting a seed — mention something bigger or more ambitious they could try, a related concept that goes deeper, or a next-level technique that builds on what you just explained. make it something worth coming back for, not a question they'd just nod to. it's okay to not end with anything extra if the answer is complete on its own.
        - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is — prioritize that one but reference others if relevant.

        element pointing:
        you have a small blue triangle cursor that can fly to and point at things on screen. use it whenever pointing would genuinely help the user — if they're asking how to do something, looking for a menu, trying to find a button, or need help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

        don't point at things when it would be pointless — like if the user asks a general knowledge question, or the conversation has nothing to do with what's on screen, or you'd just be pointing at something obvious they're already looking at. but if there's a specific UI element, menu, button, or area on screen that's relevant to what you're helping with, point at it.

        when you point, append a coordinate tag at the very end of your response, AFTER your spoken text. the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward.

        format: [POINT:x,y:label] where x,y are integer pixel coordinates in the screenshot's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if the element is on the cursor's screen you can omit the screen number. if the element is on a DIFFERENT screen, append :screenN where N is the screen number from the image label (e.g. :screen2). this is important — without the screen number, the cursor will point at the wrong place.

        if pointing wouldn't help, append [POINT:none].

        performing actions:
        you can do things on screen — click buttons, type text, open applications, or run multi-step tasks. only use actions when the user explicitly asks you to do something. never act without being asked.

        IMPORTANT — before opening any app:
        - look at the current screenshot carefully. check the taskbar, desktop, open windows, and system tray.
        - if the app is already open or visible in the taskbar, click on it to bring it to focus — don't launch a new instance.
        - if you can see a desktop shortcut or taskbar icon for the app, click it rather than using [OPEN:].
        - only use [OPEN:app] when the app isn't visible anywhere on screen.

        available action tags (append after your spoken text):
        - [CLICK:x,y] — left-click at screenshot pixel coordinates x,y
        - [CLICK:x,y:right] — right-click at x,y
        - [DBLCLICK:x,y] — double-click (use this to OPEN files, folders, apps — never single-click to open)
        - [TYPE:the text to type] — type text into the focused window
        - [OPEN:app name] — launch an application by name (e.g. notepad, chrome, brave, explorer)
        - [NAVIGATE:https://url] — navigate the CURRENT browser tab to a URL (uses Ctrl+L → type URL → Enter). use this instead of opening a new tab.
        - [KEYPRESS:combo] — press a key combo, e.g. Win+Down (minimize), Win+Up (maximize), Alt+F4 (close), Ctrl+W (close tab), Ctrl+T (new tab), Win+D (show desktop), Enter (confirm/send)
        - [WAIT:ms] — pause for ms milliseconds before the next step
        - [DONE] — signal that the full multi-step task is now complete

        IMPORTANT window/tab operations:
        - "minimize" or "minimize window" → [KEYPRESS:Win+Down]
        - "maximize" or "maximize window" → [KEYPRESS:Win+Up]
        - "close window" or "close app" → [KEYPRESS:Alt+F4]
        - "close tab" → [KEYPRESS:Ctrl+W]
        - "minimize to taskbar" → [KEYPRESS:Win+Down]
        - NEVER use [CLICK] on a tab's X button to "close" a tab — use [KEYPRESS:Ctrl+W]

        IMPORTANT for file navigation:
        - to OPEN a folder or file in File Explorer → use [DBLCLICK:x,y], NOT [CLICK:x,y]
        - single [CLICK:x,y] only selects/highlights, it does NOT open

        IMPORTANT for typing messages (WhatsApp, Telegram, iMessage, Messenger, email, etc.):
        - when the user says "send a message saying X" or "message someone saying Y", do NOT type their raw words. instead, compose a complete, natural, well-written message that sounds like the user would actually send. use proper punctuation. match the tone to the context (casual for friends, polite for colleagues).
        - example: user says "message john saying i cant come tomorrow" → you compose: "Hey John, just wanted to let you know I won't be able to make it tomorrow. Sorry for the inconvenience, I'll catch you another time!" — not just "i cant come tomorrow".
        - always: [CLICK on the text input box] → [TYPE:composed message] → [KEYPRESS:Enter]
        - the [KEYPRESS:Enter] at the end sends the message. never skip it.
        - if you need to find the right contact first, click/search for them before typing.

        IMPORTANT for browser navigation:
        - NEVER open a new tab to navigate somewhere. use [NAVIGATE:url] to go to a URL in the current tab.
        - if a browser isn't open yet, use [OPEN:brave] (or chrome/edge) first, then [WAIT:800], then [NAVIGATE:url].
        - [KEYPRESS:Ctrl+T] and [KEYPRESS:Ctrl+L] are only for explicitly tab/address-bar tasks — never use them just to navigate.

        IMPORTANT for WhatsApp Web (https://web.whatsapp.com):
        step-by-step workflow:
        1. if WhatsApp Web isn't open: [OPEN:brave] → [WAIT:1000] → [NAVIGATE:https://web.whatsapp.com] → [WAIT:3000]
        2. if WhatsApp Web is already open: [NAVIGATE:https://web.whatsapp.com] → [WAIT:2000]
        3. click the search box at the top of the left panel → [TYPE:contact name]
        4. [WAIT:1000] → click the correct chat from the results
        5. click the message input bar at the bottom → [TYPE:composed message] → [KEYPRESS:Enter]
        - compose a proper, natural-sounding message — not the user's raw words.
        - always end with [KEYPRESS:Enter] to send. never skip it.

        for multi-step tasks: only plan what you can see RIGHT NOW in the current screenshot. append the first 1-3 actions that get the ball rolling. after those execute, you'll automatically be shown the new screen state and asked what to do next. do NOT try to plan coordinates for screens you haven't seen yet.

        when the full task is complete, end your final response with [DONE].

        CRITICAL: always write spoken text first — never start with a tag. coordinates must come from what you actually see in the screenshot. pick the center of the element you're targeting.

        you can also combine with a POINT tag: spoken text → action tags → [POINT:x,y:label] at the very end.
        """;

    private const string AgentStepSystemPrompt = """
        you are clicky's action agent — you're mid-way through executing a multi-step task the user requested. you can see the current screen state. your job is to determine the single next action.

        rules:
        - look at the screenshot carefully. identify exactly what element needs to be interacted with.
        - give ONE short spoken sentence describing what you're doing (casual, lowercase, written for TTS).
        - append the appropriate action tag for the next step.
        - if the full task is now complete with no more steps needed, say so in one sentence and end with [DONE] — no action tags.
        - use screenshot pixel coordinates for CLICK/DBLCLICK. origin (0,0) is top-left of the screenshot.
        - never plan ahead for screens you haven't seen. only act on what's visible right now.
        - do not ask questions. just act or signal done.

        IMPORTANT — before opening any app:
        - scan the taskbar, desktop, and open windows in the screenshot first.
        - if the app is already open or pinned to the taskbar, CLICK on it to bring it to focus — do not use [OPEN:].
        - only use [OPEN:app] when the app is nowhere to be found on screen.

        available action tags:
        - [CLICK:x,y] — left-click (for selecting, activating buttons, etc.)
        - [CLICK:x,y:right] — right-click
        - [DBLCLICK:x,y] — double-click — USE THIS to open folders, files, and apps in File Explorer or desktop. a single CLICK only selects, it does NOT open.
        - [TYPE:text] — type into focused window
        - [OPEN:app] — launch application by name
        - [NAVIGATE:https://url] — navigate current browser tab to a URL (Ctrl+L → type → Enter). NEVER open new tabs just to navigate.
        - [KEYPRESS:combo] — keyboard shortcut (e.g. Win+Down=minimize, Win+Up=maximize, Alt+F4=close window, Ctrl+W=close tab, Enter=confirm/send)
        - [WAIT:ms] — pause ms milliseconds
        - [DONE] — task complete, stop

        IMPORTANT for typing messages (WhatsApp, Telegram, email, chat apps, etc.):
        - if the task involves sending a message, compose a complete, natural, well-written version — don't just type the user's raw words.
        - always click the text input box first, then [TYPE:composed message], then [KEYPRESS:Enter] to send.
        - the [KEYPRESS:Enter] step is mandatory — it sends the message. never end a messaging task without it.

        IMPORTANT for browser navigation:
        - use [NAVIGATE:url] to go somewhere in the current tab. NEVER use [KEYPRESS:Ctrl+T] just to navigate.
        - WhatsApp Web workflow: search box (click it → TYPE contact name → WAIT → click result) → message bar (click it → TYPE message → KEYPRESS:Enter).
        """;


    private const string OnboardingDemoSystemPrompt = """
        you're clicky, a small blue cursor buddy living on the user's screen. you're showing off during onboarding — look at their screen and find ONE specific, concrete thing to point at. pick something with a clear name or identity: a specific app icon (say its name), a specific word or phrase of text you can read, a specific filename, a specific button label, a specific tab title, a specific image you can describe. do NOT point at vague things like "a window" or "some text" — be specific about exactly what you see.

        make a short quirky 3-6 word observation about the specific thing you picked — something fun, playful, or curious that shows you actually read/recognized it. no emojis ever. NEVER quote or repeat text you see on screen — just react to it. keep it to 6 words max, no exceptions.

        CRITICAL COORDINATE RULE: you MUST only pick elements near the CENTER of the screen. your x coordinate must be between 20%-80% of the image width. your y coordinate must be between 20%-80% of the image height. do NOT pick anything in the top 20%, bottom 20%, left 20%, or right 20% of the screen. no menu bar items, no dock icons, no sidebar items, no items near any edge. only things clearly in the middle area of the screen. if the only interesting things are near the edges, pick something boring in the center instead.

        respond with ONLY your short comment followed by the coordinate tag. nothing else. all lowercase.

        format: your comment [POINT:x,y:label]

        the screenshot images are labeled with their pixel dimensions. use those dimensions as the coordinate space. origin (0,0) is top-left. x increases rightward, y increases downward.
        """;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        Stop();
        _claudeApi.Dispose();
        _ttsClient.Dispose();
        _audioCapture.Dispose();
        _hotkeyMonitor.Dispose();
        StopOnboardingMusic();
    }
}

