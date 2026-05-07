namespace ClickyWindows.Core;

/// <summary>
/// Mirrors macOS CompanionVoiceState. Drives which visual is shown on the overlay.
/// </summary>
public enum VoiceState
{
    /// <summary>No activity. Blue triangle follows cursor.</summary>
    Idle,

    /// <summary>Push-to-talk held; mic is recording. Waveform shown.</summary>
    Listening,

    /// <summary>Transcription finalizing or Claude/TTS in-flight. Spinner shown.</summary>
    Processing,

    /// <summary>TTS audio is playing back. Triangle shown (same as Idle visually).</summary>
    Responding
}

/// <summary>
/// Push-to-talk hotkey transition events. Mirrors BuddyPushToTalkShortcut.ShortcutTransition.
/// </summary>
public enum HotkeyTransition
{
    None,
    Pressed,
    Released
}

/// <summary>Buddy navigation mode — mirrors macOS BuddyNavigationMode.</summary>
public enum BuddyNavigationMode
{
    FollowingCursor,
    NavigatingToTarget,
    PointingAtTarget
}
