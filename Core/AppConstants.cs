namespace ClickyWindows.Core;

internal static class AppConstants
{
    // Worker proxy — replace with your deployed Cloudflare Worker URL.
    public const string WorkerBaseUrl = "https://your-worker-name.your-subdomain.workers.dev";

    // Global push-to-talk hotkey: Ctrl + Alt (mirrors Mac Ctrl + Option)
    public const uint HotkeyModifiers = NativeConstants.MOD_CONTROL | NativeConstants.MOD_ALT | NativeConstants.MOD_NOREPEAT;
    // We use a keyboard hook approach rather than RegisterHotKey for modifier-only,
    // but keep VK_MENU as the trigger key when using the low-level hook fallback.
    public const int HotkeyId = 9001;

    // Audio
    public const int AudioSampleRate = 16_000;
    public const int AudioChannels = 1;
    public const int AudioBitsPerSample = 16;
    public const int AudioBufferMilliseconds = 100;

    // Screen capture
    public const int MaxScreenshotDimension = 1280;
    public const long JpegQuality = 80L;

    // AI APIs
    public const string DefaultClaudeModel = "claude-sonnet-4-6";
    public const string TtsModelId = "eleven_flash_v2_5";
    public const double TtsStability = 0.5;
    public const double TtsSimilarityBoost = 0.75;
    public const int MaxConversationHistory = 10;

    // AssemblyAI
    public const string AssemblyAiWebSocketBase = "wss://streaming.assemblyai.com/v3/ws";
    /// <summary>After ForceEndpoint, wait this long before delivering best-effort transcript if no end_turn.</summary>
    public const double AssemblyAiExplicitFinalGracePeriodSeconds = 2.2;
    /// <summary>If the streaming session never calls OnFinalTranscriptReady this long after key-up, abandon it.</summary>
    public const double AssemblyAiFallbackDelaySeconds = 4.0;

    // Overlay
    public const double CursorOffsetX = 35.0;
    public const double CursorOffsetY = 25.0;
    public const double TargetOffsetX = 8.0;
    public const double TargetOffsetY = 12.0;
    public const double FlightMinSeconds = 0.6;
    public const double FlightMaxSeconds = 1.4;
    public const double FlightFrameIntervalMs = 16.67; // ~60fps
    public const double PointingHoldSeconds = 3.0;
    public const double NavigationCancelDistanceDip = 100.0;

    // Action execution
    /// <summary>Default pause between sequential action steps (page-load buffer).</summary>
    public const int ActionStepPauseMs = 1200;
    /// <summary>Toast display duration before each action fires.</summary>
    public const int ActionToastHoldMs = 1200;
    /// <summary>Maximum agentic loop iterations to prevent runaway loops.</summary>
    public const int AgentMaxIterations = 8;

    // Persistence
    public const string SettingsFileName = "settings.json";
    public static string SettingsDirectory =>
        IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clicky");
    public static string SettingsFilePath =>
        IoPath.Combine(SettingsDirectory, SettingsFileName);

    // Registry
    public const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupRegistryValueName = "Clicky";

    // Onboarding
    public const string OnboardingVideoUrl = "https://stream.mux.com/e5jB8UuSrtFABVnTHCR7k3sIsmcUHCyhtLu1tzqLlfs.m3u8";
    public const string WelcomeMessage = "hey! i'm clicky";
    public const string OnboardingPromptMessage = "press ctrl + alt and introduce yourself";
    public const double CharacterStreamIntervalSeconds = 0.03;

    // Music
    public const string OnboardingMusicFileName = "ff.mp3";
    public const float OnboardingMusicVolume = 0.3f;
    public const double OnboardingMusicFadeStartSeconds = 90.0;
    public const double OnboardingMusicFadeDurationSeconds = 3.0;
    public const double OnboardingDemoTriggerSeconds = 40.0;
    public const double OnboardingPromptAutoDismissSeconds = 10.0;
}

/// <summary>Win32 constant values used across the application.</summary>
internal static class NativeConstants
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int WM_HOTKEY = 0x0312;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;   // Alt key
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    public const int WH_KEYBOARD_LL = 13;

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int MONITOR_DEFAULTTONEAREST = 0x00000002;
}

