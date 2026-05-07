using System.Drawing;

namespace ClickyWindows.Core;

/// <summary>
/// Mirrors macOS CompanionScreenCapture. Carries one captured display's JPEG data,
/// labeling, physical bounds, DPI scale, and whether the cursor is on this screen.
/// </summary>
public sealed record ScreenCaptureData
{
    /// <summary>JPEG-encoded screenshot bytes at ≤ MaxScreenshotDimension on longest side.</summary>
    public required byte[] ImageBytes { get; init; }

    /// <summary>
    /// Human-readable label sent to Claude alongside the image, e.g.
    /// "screen 1 of 2 — cursor is on this screen (primary focus)".
    /// </summary>
    public required string Label { get; init; }

    /// <summary>True when the system cursor was inside this screen at capture time.</summary>
    public required bool IsCursorScreen { get; init; }

    /// <summary>
    /// Screen bounds in physical pixels (top-left origin, Windows convention).
    /// Used for coordinate translation from screenshot space to screen space.
    /// </summary>
    public required Rectangle PhysicalBounds { get; init; }

    /// <summary>
    /// Per-monitor DPI scale factor (e.g., 1.0 for 96dpi, 1.5 for 144dpi, 2.0 for 192dpi).
    /// Used to convert physical pixels to WPF device-independent units (DIPs).
    /// </summary>
    public required double DpiScale { get; init; }

    /// <summary>Width of the screenshot in pixels (after scaling to max dimension).</summary>
    public required int ScreenshotWidthPx { get; init; }

    /// <summary>Height of the screenshot in pixels (after scaling to max dimension).</summary>
    public required int ScreenshotHeightPx { get; init; }
}
