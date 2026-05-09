using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClickyWindows.Core;

/// <summary>
/// Executes system-level actions that Claude can request: mouse clicks, keyboard
/// typing, and application launching.
///
/// All methods are static and stateless. Mouse coordinates are in physical screen
/// pixels (same space Claude's screenshot coordinates refer to after scaling).
/// </summary>
public static class ActionExecutor
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXVIRTUALSCREEN  = 78;
    private const int SM_CYVIRTUALSCREEN  = 79;
    private const int SM_XVIRTUALSCREEN   = 76;
    private const int SM_YVIRTUALSCREEN   = 77;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx;
        public int    dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE    = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP   = 0x0002;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Left-clicks at the given physical screen pixel coordinates.
    /// Normalises to the full virtual desktop space so multi-monitor offsets work correctly.
    /// </summary>
    public static void Click(int physX, int physY, bool rightClick = false)
    {
        // Virtual desktop origin can be negative on multi-monitor setups
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        // Normalise the physical coordinate (which is already in absolute screen space)
        // relative to the virtual desktop origin, then scale to 0–65535
        int nx = (int)(((physX - vx) * 65535.0) / vw);
        int ny = (int)(((physY - vy) * 65535.0) / vh);

        uint downFlag = rightClick ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint upFlag   = rightClick ? MOUSEEVENTF_RIGHTUP   : MOUSEEVENTF_LEFTUP;

        // Move first, then separate down/up so the OS sees a well-formed click sequence
        var moveInput = new INPUT[]
        {
            new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT
            {
                dx = nx, dy = ny,
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
            }}}
        };
        SendInput(1, moveInput, Marshal.SizeOf<INPUT>());

        // Brief pause so the target window can receive focus after cursor moves
        Thread.Sleep(30);

        var clickInputs = new INPUT[]
        {
            new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT
            {
                dx = nx, dy = ny,
                dwFlags = downFlag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
            }}},
            new() { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT
            {
                dx = nx, dy = ny,
                dwFlags = upFlag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
            }}}
        };
        SendInput(2, clickInputs, Marshal.SizeOf<INPUT>());

        AppDebugLog.Write($"ActionExecutor: click physXY=({physX},{physY}) norm=({nx},{ny}) right={rightClick}");
    }

    /// <summary>
    /// Types the given text into whatever window currently has focus.
    /// Uses Unicode key events so any character (including non-ASCII) works.
    /// </summary>
    public static void Type(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            // Key down
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT
                {
                    wVk    = 0,
                    wScan  = c,
                    dwFlags = KEYEVENTF_UNICODE
                }}
            });
            // Key up
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT
                {
                    wVk    = 0,
                    wScan  = c,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                }}
            });
        }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        AppDebugLog.Write($"ActionExecutor: typed {text.Length} chars");
    }

    /// <summary>
    /// Launches an application by name or path using the shell.
    /// Falls back to searching PATH if the name alone fails.
    /// </summary>
    public static void OpenApp(string appName)
    {
        try
        {
            var psi = new ProcessStartInfo(appName)
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            AppDebugLog.Write($"ActionExecutor: opened \"{appName}\"");
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"ActionExecutor: open \"{appName}\" failed — {ex.Message}");
            throw;
        }
    }
}
