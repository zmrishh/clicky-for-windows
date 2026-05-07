using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClickyWindows.Core;

namespace ClickyWindows.ScreenCapture;

/// <summary>
/// Captures all connected displays as JPEG data using GDI+ CopyFromScreen.
/// Returns one <see cref="ScreenCaptureData"/> per monitor, sorted cursor-screen-first
/// — the same ordering as the macOS CompanionScreenCaptureUtility.
///
/// Overlay windows are excluded from captures via WDA_EXCLUDEFROMCAPTURE
/// (Windows 10 2004+). The method is thread-safe and allocation-efficient.
/// </summary>
public static class ScreenCaptureService
{
    // ── Win32 interop ─────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── JPEG encoder ─────────────────────────────────────────────────────────

    private static readonly ImageCodecInfo JpegEncoder = GetJpegEncoder();
    private static readonly EncoderParameters JpegParams = BuildJpegParams(AppConstants.JpegQuality);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures all screens and returns them sorted so the cursor screen is first.
    /// Must be called from a thread that can access GDI+ (any STA or MTA thread is fine).
    /// </summary>
    public static List<ScreenCaptureData> CaptureAllScreens()
    {
        var cursorPos = Cursor.Position;
        var results   = new List<ScreenCaptureData>(Screen.AllScreens.Length);

        foreach (var screen in Screen.AllScreens)
        {
            var capture = CaptureScreen(screen, cursorPos);
            if (capture != null)
                results.Add(capture);
        }

        // Sort cursor screen first — mirrors macOS sortedDisplays logic
        results.Sort((a, b) =>
        {
            if (a.IsCursorScreen == b.IsCursorScreen) return 0;
            return a.IsCursorScreen ? -1 : 1;
        });

        // Apply labels with count and role now that ordering is known
        for (int i = 0; i < results.Count; i++)
        {
            var label = BuildLabel(i, results.Count, results[i].IsCursorScreen);
            results[i] = results[i] with { Label = label };
        }

        return results;
    }

    /// <summary>
    /// Calls <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> on an overlay HWND
    /// so it is invisible to GDI+ CopyFromScreen. Safe to call on Win10 2004+;
    /// silently no-ops on older builds.
    /// </summary>
    public static void ExcludeWindowFromCapture(IntPtr hwnd)
    {
        SetWindowDisplayAffinity(hwnd, NativeConstants.WDA_EXCLUDEFROMCAPTURE);
    }

    // ── Per-screen capture ────────────────────────────────────────────────────

    private static ScreenCaptureData? CaptureScreen(Screen screen, Point cursorPos)
    {
        var bounds  = screen.Bounds;
        var isCursorScreen = bounds.Contains(cursorPos);

        // Compute scaled dimensions preserving aspect ratio at ≤ MaxScreenshotDimension
        int srcW = bounds.Width;
        int srcH = bounds.Height;
        ComputeScaledDimensions(srcW, srcH, out int dstW, out int dstH);

        using var bitmap = new Bitmap(dstW, dstH, PixelFormat.Format32bppRgb);
        using var g      = Graphics.FromImage(bitmap);

        g.InterpolationMode  = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode      = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

        try
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, new Size(srcW, srcH),
                             CopyPixelOperation.SourceCopy);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreenCapture] CopyFromScreen failed for {screen.DeviceName}: {ex.Message}");
            return null;
        }

        // Resize to target dimensions if necessary
        byte[] jpeg;
        if (dstW == srcW && dstH == srcH)
        {
            jpeg = BitmapToJpeg(bitmap);
        }
        else
        {
            using var scaled = new Bitmap(bitmap, dstW, dstH);
            jpeg = BitmapToJpeg(scaled);
        }

        var dpiScale = GetDpiScaleForScreen(bounds);

        return new ScreenCaptureData
        {
            ImageBytes        = jpeg,
            Label             = string.Empty,   // Filled in after sort
            IsCursorScreen    = isCursorScreen,
            PhysicalBounds    = bounds,
            DpiScale          = dpiScale,
            ScreenshotWidthPx  = dstW,
            ScreenshotHeightPx = dstH
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ComputeScaledDimensions(int srcW, int srcH, out int dstW, out int dstH)
    {
        int max = AppConstants.MaxScreenshotDimension;

        if (srcW <= max && srcH <= max)
        {
            dstW = srcW;
            dstH = srcH;
            return;
        }

        if (srcW >= srcH)
        {
            dstW = max;
            dstH = (int)Math.Round(srcH * ((double)max / srcW));
        }
        else
        {
            dstH = max;
            dstW = (int)Math.Round(srcW * ((double)max / srcH));
        }
    }

    private static string BuildLabel(int index, int total, bool isCursorScreen)
    {
        if (total == 1)
            return "user's screen (cursor is here)";

        if (isCursorScreen)
            return $"screen {index + 1} of {total} — cursor is on this screen (primary focus)";

        return $"screen {index + 1} of {total} — secondary screen";
    }

    private static byte[] BitmapToJpeg(Bitmap bmp)
    {
        using var ms = new IoMemoryStream();
        bmp.Save(ms, JpegEncoder, JpegParams);
        return ms.ToArray();
    }

    private static double GetDpiScaleForScreen(Rectangle bounds)
    {
        var center = new POINT
        {
            X = bounds.Left + bounds.Width / 2,
            Y = bounds.Top  + bounds.Height / 2
        };

        var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 1.0;

        int hr = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        if (hr != 0) return 1.0;

        return dpiX / 96.0;
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    private static EncoderParameters BuildJpegParams(long quality)
    {
        var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        return ep;
    }
}

