using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClickyWindows.Core;

/// <summary>
/// Executes system-level actions that Claude can request: mouse clicks, keyboard
/// typing, key combos, and application launching.
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
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT  mi;
        [FieldOffset(0)] public KEYBDINPUT  ki;
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

    private const uint KEYEVENTF_UNICODE  = 0x0004;
    private const uint KEYEVENTF_KEYUP    = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    // Virtual key codes
    private const ushort VK_LWIN   = 0x5B;
    private const ushort VK_RWIN   = 0x5C;
    private const ushort VK_SHIFT  = 0x10;
    private const ushort VK_CTRL   = 0x11;
    private const ushort VK_ALT    = 0x12;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB    = 0x09;
    private const ushort VK_ESC    = 0x1B;
    private const ushort VK_F4     = 0x73;
    private const ushort VK_UP     = 0x26;
    private const ushort VK_DOWN   = 0x28;
    private const ushort VK_LEFT   = 0x25;
    private const ushort VK_RIGHT  = 0x27;
    private const ushort VK_HOME   = 0x24;
    private const ushort VK_END    = 0x23;
    private const ushort VK_DELETE = 0x2E;
    private const ushort VK_BACK   = 0x08;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Left-clicks at the given physical screen pixel coordinates.
    /// Normalises to the full virtual desktop space so multi-monitor offsets work correctly.
    /// </summary>
    public static void Click(int physX, int physY, bool rightClick = false)
    {
        var (nx, ny) = Normalise(physX, physY);

        uint downFlag = rightClick ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint upFlag   = rightClick ? MOUSEEVENTF_RIGHTUP   : MOUSEEVENTF_LEFTUP;

        SendInput(1, [MoveInput(nx, ny)], Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);
        SendInput(2, [MouseButton(nx, ny, downFlag), MouseButton(nx, ny, upFlag)], Marshal.SizeOf<INPUT>());

        AppDebugLog.Write($"ActionExecutor: click physXY=({physX},{physY}) norm=({nx},{ny}) right={rightClick}");
    }

    /// <summary>
    /// Double-clicks at the given physical screen pixel coordinates.
    /// Needed for opening files and folders in File Explorer.
    /// </summary>
    public static void DoubleClick(int physX, int physY)
    {
        var (nx, ny) = Normalise(physX, physY);

        SendInput(1, [MoveInput(nx, ny)], Marshal.SizeOf<INPUT>());
        Thread.Sleep(30);

        // Two click pairs in quick succession — OS interprets as double-click
        INPUT[] inputs =
        [
            MouseButton(nx, ny, MOUSEEVENTF_LEFTDOWN),
            MouseButton(nx, ny, MOUSEEVENTF_LEFTUP),
            MouseButton(nx, ny, MOUSEEVENTF_LEFTDOWN),
            MouseButton(nx, ny, MOUSEEVENTF_LEFTUP),
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        AppDebugLog.Write($"ActionExecutor: double-click physXY=({physX},{physY})");
    }

    /// <summary>
    /// Sends a keyboard shortcut described as a "+"-separated key combo string.
    /// Examples: "Win+D", "Win+Down", "Alt+F4", "Ctrl+W", "Ctrl+Shift+T"
    /// </summary>
    public static void PressKey(string combo)
    {
        var parts  = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var vkeys  = new List<ushort>();

        foreach (string p in parts)
        {
            ushort vk = p.ToLowerInvariant() switch
            {
                "win"   or "windows"         => VK_LWIN,
                "shift"                      => VK_SHIFT,
                "ctrl"  or "control"         => VK_CTRL,
                "alt"                        => VK_ALT,
                "enter" or "return"          => VK_RETURN,
                "tab"                        => VK_TAB,
                "esc"   or "escape"          => VK_ESC,
                "f4"                         => VK_F4,
                "up"                         => VK_UP,
                "down"                       => VK_DOWN,
                "left"                       => VK_LEFT,
                "right"                      => VK_RIGHT,
                "home"                       => VK_HOME,
                "end"                        => VK_END,
                "delete" or "del"            => VK_DELETE,
                "backspace" or "back"        => VK_BACK,
                _ when p.Length == 1         => (ushort)(VkKeyScan(p[0]) & 0xFF),
                _                            => 0
            };

            if (vk != 0) vkeys.Add(vk);
        }

        if (vkeys.Count == 0) return;

        var inputs = new List<INPUT>(vkeys.Count * 2);
        foreach (var vk in vkeys)
            inputs.Add(KeyDown(vk));
        foreach (var vk in Enumerable.Reverse(vkeys))
            inputs.Add(KeyUp(vk));

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        AppDebugLog.Write($"ActionExecutor: keypress \"{combo}\"");
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
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT
                { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE } } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT
                { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        AppDebugLog.Write($"ActionExecutor: typed {text.Length} chars");
    }

    /// <summary>
    /// Launches an application by name or path using the shell.
    /// Spoken names (e.g. "apple music", "file explorer") are resolved to
    /// their real executable or URI-scheme before launching.
    /// </summary>
    public static void OpenApp(string appName)
    {
        var target = ResolveAppName(appName.Trim());
        try
        {
            // URI-scheme targets (ms-music:, spotify:, etc.) must not receive
            // a WorkingDirectory — set it to the user profile to be safe for
            // both URI and executable targets.
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute  = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
            AppDebugLog.Write($"ActionExecutor: opened \"{appName}\" -> \"{target}\"");
        }
        catch (Exception ex)
        {
            AppDebugLog.Write($"ActionExecutor: open \"{appName}\" (\"{target}\") failed — {ex.Message}");
            throw;
        }
    }

    // ── App name resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Maps spoken / informal app names to the real executable name or URI
    /// scheme that Windows can launch via UseShellExecute.
    /// </summary>
    private static readonly Dictionary<string, string> AppAliases =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Music / media ────────────────────────────────────────────────────
        ["apple music"]         = "ms-music:",
        ["music"]               = "ms-music:",
        ["spotify"]             = "spotify:",
        ["vlc"]                 = "vlc",
        ["media player"]        = "wmplayer",
        ["windows media player"]= "wmplayer",
        // ── Messaging / social ───────────────────────────────────────────────
        ["whatsapp"]            = "whatsapp:",
        ["telegram"]            = "telegram:",
        ["discord"]             = "discord",
        ["slack"]               = "slack",
        ["teams"]               = "msteams:",
        ["microsoft teams"]     = "msteams:",
        ["zoom"]                = "zoom",
        ["skype"]               = "skype:",
        // ── Browsers ─────────────────────────────────────────────────────────
        ["chrome"]              = "chrome",
        ["google chrome"]       = "chrome",
        ["brave"]               = "brave",
        ["brave browser"]       = "brave",
        ["firefox"]             = "firefox",
        ["edge"]                = "msedge",
        ["microsoft edge"]      = "msedge",
        // ── System utilities ─────────────────────────────────────────────────
        ["file explorer"]       = "explorer",
        ["explorer"]            = "explorer",
        ["notepad"]             = "notepad",
        ["calculator"]          = "calc",
        ["calc"]                = "calc",
        ["paint"]               = "mspaint",
        ["task manager"]        = "taskmgr",
        ["settings"]            = "ms-settings:",
        ["windows settings"]    = "ms-settings:",
        ["cmd"]                 = "cmd",
        ["command prompt"]      = "cmd",
        ["powershell"]          = "powershell",
        ["terminal"]            = "wt",
        ["windows terminal"]    = "wt",
        // ── Productivity / Office ─────────────────────────────────────────────
        ["word"]                = "winword",
        ["microsoft word"]      = "winword",
        ["excel"]               = "excel",
        ["microsoft excel"]     = "excel",
        ["powerpoint"]          = "powerpnt",
        ["microsoft powerpoint"]= "powerpnt",
        ["outlook"]             = "outlook",
        ["microsoft outlook"]   = "outlook",
        ["onenote"]             = "onenote",
        // ── Dev tools ────────────────────────────────────────────────────────
        ["vs code"]             = "code",
        ["vscode"]              = "code",
        ["visual studio code"]  = "code",
        ["visual studio"]       = "devenv",
        // ── Store / photos / mail ─────────────────────────────────────────────
        ["store"]               = "ms-windows-store:",
        ["microsoft store"]     = "ms-windows-store:",
        ["photos"]              = "ms-photos:",
        ["mail"]                = "ms-outlook:",
    };

    /// <summary>
    /// Strips filler words ("open X", "launch X") then looks up the alias table.
    /// Falls back to the raw name so shell-execute can still try its luck.
    /// </summary>
    private static string ResolveAppName(string name)
    {
        if (AppAliases.TryGetValue(name, out var hit)) return hit;

        // Strip leading verb: "open brave" → "brave"
        var stripped = Regex.Replace(
            name, @"^(open|launch|start|run)\s+", string.Empty,
            RegexOptions.IgnoreCase).Trim();

        if (AppAliases.TryGetValue(stripped, out var hit2)) return hit2;

        // Return the stripped name so the shell can try (handles "notepad++", etc.)
        return stripped.Length > 0 ? stripped : name;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (int nx, int ny) Normalise(int physX, int physY)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return ((int)(((physX - vx) * 65535.0) / vw),
                (int)(((physY - vy) * 65535.0) / vh));
    }

    private static INPUT MoveInput(int nx, int ny) => new()
    {
        type = INPUT_MOUSE,
        u    = new INPUTUNION { mi = new MOUSEINPUT
            { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } }
    };

    private static INPUT MouseButton(int nx, int ny, uint flag) => new()
    {
        type = INPUT_MOUSE,
        u    = new INPUTUNION { mi = new MOUSEINPUT
            { dx = nx, dy = ny, dwFlags = flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK } }
    };

    private static INPUT KeyDown(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        u    = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } }
    };

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        u    = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
    };
}
