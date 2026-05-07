using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using ClickyWindows.Core;
using Application = System.Windows.Application;

namespace ClickyWindows.SystemTray;

/// <summary>
/// Manages the system-tray <see cref="NotifyIcon"/> and the floating popup panel.
/// Mirrors the Mac MenuBarPanelManager:
///   - Draws the Clicky triangle as the tray icon (same shape and rotation as Mac)
///   - Clicking the icon toggles the popup
///   - Clicking outside the popup dismisses it
///   - Auto-opens the popup on first launch when setup is needed
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly CompanionManager _manager;
    private readonly NotifyIcon _notifyIcon;
    private TrayPopupWindow? _popup;

    private const int PopupWidth = 320;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TrayIconManager(CompanionManager manager)
    {
        _manager = manager;

        _notifyIcon = new NotifyIcon
        {
            Icon    = BuildTriangleIcon(),
            Text    = "Clicky",
            Visible = true
        };

        _notifyIcon.MouseUp += OnTrayIconClicked;
    }

    // ── Panel show/hide ───────────────────────────────────────────────────────

    /// <summary>Auto-opens the popup on launch when the user still needs to configure.</summary>
    public void ShowPanelOnLaunch()
    {
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            timer?.Dispose();
            Application.Current.Dispatcher.Invoke(ShowPanel);
        }, null, 300, Timeout.Infinite);
    }

    private void OnTrayIconClicked(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_popup?.IsVisible == true)
            HidePanel();
        else
            ShowPanel();
    }

    private void ShowPanel()
    {
        if (_popup == null || !_popup.IsLoaded)
        {
            _popup = new TrayPopupWindow(_manager);
            _popup.Deactivated += (_, _) => HidePanel();
        }

        PositionPanelAtTrayIcon();
        _popup.Show();
        _popup.Activate();
    }

    public void HidePanel()
    {
        _popup?.Hide();
    }

    // ── Position popup below tray icon ────────────────────────────────────────

    private void PositionPanelAtTrayIcon()
    {
        if (_popup == null) return;

        // Measure the popup height
        _popup.Measure(new System.Windows.Size(PopupWidth, double.PositiveInfinity));
        double popupH = _popup.DesiredSize.Height > 0 ? _popup.DesiredSize.Height : 380;

        // Get cursor position as fallback for tray icon location
        GetCursorPos(out var cursor);

        var workArea = Screen.GetWorkingArea(new System.Drawing.Point(cursor.X, cursor.Y));

        // Place popup above taskbar bottom edge, centered on cursor X
        double left = cursor.X - PopupWidth / 2.0;
        double top;

        if (cursor.Y > workArea.Bottom - 60)
        {
            // Taskbar is at bottom — popup goes upward from taskbar
            top = workArea.Bottom - popupH - 4;
        }
        else if (cursor.Y < workArea.Top + 60)
        {
            // Taskbar is at top
            top = workArea.Top + 4;
        }
        else
        {
            top = cursor.Y - popupH - 4;
        }

        // Clamp to work area
        left = Math.Clamp(left, workArea.Left, workArea.Right - PopupWidth);
        top  = Math.Clamp(top,  workArea.Top,  workArea.Bottom - popupH);

        _popup.Left = left;
        _popup.Top  = top;
    }

    // ── Triangle icon ─────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the same equilateral triangle used as the Mac menu bar icon.
    /// Rotated 35° clockwise to match the cursor orientation.
    /// </summary>
    private static Icon BuildTriangleIcon()
    {
        const int Size = 16;
        using var bmp = new Bitmap(Size, Size);
        using var g   = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float cx = Size / 2f;
        float cy = Size / 2f;
        float triangleSize = Size * 0.70f;
        float height = triangleSize * (float)Math.Sqrt(3.0) / 2f;

        var top         = new PointF(cx,                    cy - height / 1.5f);
        var bottomLeft  = new PointF(cx - triangleSize / 2, cy + height / 3f);
        var bottomRight = new PointF(cx + triangleSize / 2, cy + height / 3f);

        double angle = 35.0 * Math.PI / 180.0;
        PointF Rotate(PointF p)
        {
            float dx = p.X - cx, dy = p.Y - cy;
            float cosA = (float)Math.Cos(angle), sinA = (float)Math.Sin(angle);
            return new PointF(cx + cosA * dx - sinA * dy, cy + sinA * dx + cosA * dy);
        }

        var pts = new[] { Rotate(top), Rotate(bottomLeft), Rotate(bottomRight) };

        using var brush = new SolidBrush(Color.White);
        g.FillPolygon(brush, pts);

        return Icon.FromHandle(bmp.GetHicon());
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _popup?.Close();
    }
}
