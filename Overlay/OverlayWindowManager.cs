using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using ClickyWindows.Core;

namespace ClickyWindows.Overlay;

/// <summary>
/// Creates and manages one <see cref="OverlayWindow"/> per connected monitor.
/// Mirrors the macOS OverlayWindowManager — ShowOverlay, HideOverlay, and
/// FadeOutAndHideOverlay all behave identically to their Swift counterparts.
///
/// Window positioning uses Win32 SetWindowPos with physical pixel coordinates
/// to sidestep WPF's logical-pixel DPI translation, which drifts on non-100%
/// DPI monitors.
/// </summary>
public sealed class OverlayWindowManager
{
    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER   = 0x0004;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly List<OverlayWindow> _windows = new();
    public bool HasShownOverlayBefore { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Closes any existing overlays and creates one new overlay per monitor.
    /// Must be called on the UI thread.
    /// </summary>
    public void ShowOverlay(CompanionManager manager)
    {
        HideOverlay();

        bool isFirst = !HasShownOverlayBefore;
        HasShownOverlayBefore = true;

        var screens = System.Windows.Forms.Screen.AllScreens;

        foreach (var screen in screens)
        {
            var bounds   = screen.Bounds;
            double dpi   = GetDpiScaleForScreen(bounds);
            double dipW  = bounds.Width  / dpi;
            double dipH  = bounds.Height / dpi;

            var overlay = new OverlayWindow(manager, bounds, dpi, isFirst)
            {
                Left   = 0,
                Top    = 0,
                Width  = dipW,
                Height = dipH,
                ShowActivated = false
            };

            overlay.Show();

            // Position using SetWindowPos with physical pixel coords so that
            // PerMonitorV2 DPI differences between monitors don't cause drift.
            var hwnd = new WindowInteropHelper(overlay).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                SWP_SHOWWINDOW | SWP_NOACTIVATE);

            _windows.Add(overlay);
        }
    }

    /// <summary>Immediately hides and destroys all overlay windows.</summary>
    public void HideOverlay()
    {
        foreach (var w in _windows)
        {
            w.Dispatcher.Invoke(w.Close);
        }
        _windows.Clear();
    }

    /// <summary>
    /// Fades all overlays to transparent over <paramref name="durationSeconds"/> then
    /// destroys them. Mirrors macOS NSAnimationContext fadeOutAndHideOverlay.
    /// </summary>
    public void FadeOutAndHideOverlay(double durationSeconds = 0.4)
    {
        var toFade = new List<OverlayWindow>(_windows);
        _windows.Clear();

        foreach (var w in toFade)
        {
            var anim = new DoubleAnimation(0.0, new Duration(TimeSpan.FromSeconds(durationSeconds)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) => w.Dispatcher.Invoke(w.Close);
            w.BeginAnimation(UIElement.OpacityProperty, anim);
        }
    }

    public bool IsShowingOverlay => _windows.Count > 0;

    /// <summary>
    /// Shows the response panel on the overlay covering the cursor's current screen.
    /// Must be called from the UI thread.
    /// </summary>
    public void ShowResponse(string text)
    {
        var cp = System.Windows.Forms.Cursor.Position;
        var target = _windows.FirstOrDefault(w =>
            w.PhysicalBounds.Contains(cp.X, cp.Y)) ?? _windows.FirstOrDefault();
        target?.ShowResponse(text);
    }

    /// <summary>Hides the response panel on all overlays.</summary>
    public void HideResponse()
    {
        foreach (var w in _windows)
            w.HideResponse();
    }

    /// <summary>
    /// Shows the action countdown toast on the cursor's screen overlay and fires
    /// <paramref name="onComplete"/> after <paramref name="holdMs"/> milliseconds.
    /// </summary>
    public void ShowActionToast(string message, int holdMs, Action onComplete)
    {
        var cp = System.Windows.Forms.Cursor.Position;
        var target = _windows.FirstOrDefault(w =>
            w.PhysicalBounds.Contains(cp.X, cp.Y)) ?? _windows.FirstOrDefault();

        if (target != null)
            target.ShowActionToast(message, holdMs, onComplete);
        else
            onComplete(); // no overlay visible — execute immediately
    }

    // ── DPI helper ────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static double GetDpiScaleForScreen(System.Drawing.Rectangle bounds)
    {
        var center = new POINT
        {
            X = bounds.Left + bounds.Width  / 2,
            Y = bounds.Top  + bounds.Height / 2
        };

        var monitor = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor == IntPtr.Zero) return 1.0;

        int hr = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _);
        return hr == 0 ? dpiX / 96.0 : 1.0;
    }
}
