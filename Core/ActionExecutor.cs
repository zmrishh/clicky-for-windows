using System.Diagnostics;
using System.IO;
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
    private const ushort VK_SPACE  = 0x20;
    // Function keys
    private const ushort VK_F1     = 0x70;
    private const ushort VK_F2     = 0x71;
    private const ushort VK_F3     = 0x72;
    private const ushort VK_F4     = 0x73;
    private const ushort VK_F5     = 0x74;
    private const ushort VK_F6     = 0x75;
    private const ushort VK_F7     = 0x76;
    private const ushort VK_F8     = 0x77;
    private const ushort VK_F9     = 0x78;
    private const ushort VK_F10    = 0x79;
    private const ushort VK_F11    = 0x7A;
    private const ushort VK_F12    = 0x7B;
    // Navigation
    private const ushort VK_UP     = 0x26;
    private const ushort VK_DOWN   = 0x28;
    private const ushort VK_LEFT   = 0x25;
    private const ushort VK_RIGHT  = 0x27;
    private const ushort VK_HOME   = 0x24;
    private const ushort VK_END    = 0x23;
    private const ushort VK_PGUP   = 0x21;
    private const ushort VK_PGDN   = 0x22;
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
                "space" or "spacebar"        => VK_SPACE,
                "f1"                         => VK_F1,
                "f2"                         => VK_F2,
                "f3"                         => VK_F3,
                "f4"                         => VK_F4,
                "f5"                         => VK_F5,
                "f6"                         => VK_F6,
                "f7"                         => VK_F7,
                "f8"                         => VK_F8,
                "f9"                         => VK_F9,
                "f10"                        => VK_F10,
                "f11"                        => VK_F11,
                "f12"                        => VK_F12,
                "up"                         => VK_UP,
                "down"                       => VK_DOWN,
                "left"                       => VK_LEFT,
                "right"                      => VK_RIGHT,
                "home"                       => VK_HOME,
                "end"                        => VK_END,
                "pgup"  or "pageup"          => VK_PGUP,
                "pgdn"  or "pagedown"        => VK_PGDN,
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
    /// Moves the cursor to the given position without clicking.
    /// Use before clicking elements whose controls only appear on hover
    /// (e.g. Netflix / YouTube video controls, dropdown menus, tooltips).
    /// </summary>
    public static void Hover(int physX, int physY)
    {
        var (nx, ny) = Normalise(physX, physY);
        SendInput(1, [MoveInput(nx, ny)], Marshal.SizeOf<INPUT>());
        AppDebugLog.Write($"ActionExecutor: hover physXY=({physX},{physY})");
    }

    /// <summary>
    /// Navigates the currently active browser tab to the given URL.
    /// Uses Ctrl+L to focus the address bar (works in Chrome, Edge, Brave, Firefox),
    /// then types the URL and presses Enter.
    /// </summary>
    public static void Navigate(string url)
    {
        PressKey("Ctrl+L");
        Thread.Sleep(180); // let the address bar gain focus and select existing text
        Type(url);
        Thread.Sleep(60);
        PressKey("Enter");
        AppDebugLog.Write($"ActionExecutor: navigate to \"{url}\"");
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
    /// Maps spoken / informal app names to the real executable name or URI scheme.
    /// For apps that have both a desktop version and a web fallback, the entry
    /// here is the web URL — <see cref="ResolveAppName"/> checks for the installed
    /// desktop exe first and overrides this if found.
    /// </summary>
    private static readonly Dictionary<string, string> AppAliases =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Music / media ────────────────────────────────────────────────────
        ["apple music"]         = "ms-music:",
        ["music"]               = "ms-music:",
        ["spotify"]             = "https://open.spotify.com",   // web fallback
        ["vlc"]                 = "vlc",
        ["media player"]        = "wmplayer",
        ["windows media player"]= "wmplayer",
        // ── Messaging / social (web fallbacks — desktop exes checked at runtime) ─
        ["whatsapp"]            = "https://web.whatsapp.com",
        ["whatsapp web"]        = "https://web.whatsapp.com",
        ["telegram"]            = "https://web.telegram.org",
        ["discord"]             = "https://discord.com/app",
        ["slack"]               = "https://app.slack.com",
        ["teams"]               = "msteams:",
        ["microsoft teams"]     = "msteams:",
        ["zoom"]                = "https://app.zoom.us/wc",
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
    /// For apps that ship a desktop client but also have a web fallback, these
    /// candidate exe paths are checked (in order) at runtime. The first existing
    /// path wins; otherwise the <see cref="AppAliases"/> web-fallback is used.
    ///
    /// Paths may contain environment-variable tokens (%LOCALAPPDATA%, etc.).
    /// </summary>
    private static readonly Dictionary<string, string[]> DesktopExeCandidates =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["whatsapp"] =
        [
            @"%LOCALAPPDATA%\WhatsApp\WhatsApp.exe",
            @"%APPDATA%\WhatsApp\WhatsApp.exe",
        ],
        ["whatsapp web"] =
        [
            @"%LOCALAPPDATA%\WhatsApp\WhatsApp.exe",
        ],
        ["spotify"] =
        [
            @"%APPDATA%\Spotify\Spotify.exe",
            @"%LOCALAPPDATA%\Microsoft\WindowsApps\Spotify.exe",
        ],
        ["telegram"] =
        [
            @"%APPDATA%\Telegram Desktop\Telegram.exe",
            @"%LOCALAPPDATA%\Telegram Desktop\Telegram.exe",
        ],
        ["discord"] =
        [
            @"%LOCALAPPDATA%\Discord\Update.exe",          // Discord updater doubles as launcher
            @"%LOCALAPPDATA%\Discord\app-*\Discord.exe",   // glob, handled below
        ],
        ["slack"] =
        [
            @"%LOCALAPPDATA%\slack\slack.exe",
        ],
        ["zoom"] =
        [
            @"%APPDATA%\Zoom\bin\Zoom.exe",
            @"%LOCALAPPDATA%\Zoom\bin\Zoom.exe",
        ],
    };

    /// <summary>
    /// Strips filler words ("open X", "launch X") then looks up the alias table.
    /// For apps in <see cref="DesktopExeCandidates"/>, checks whether the desktop
    /// client is installed and prefers it over the web fallback.
    /// </summary>
    private static string ResolveAppName(string name)
    {
        // Try exact alias first (may be overridden below for installed-app check)
        AppAliases.TryGetValue(name, out var aliasTarget);

        // Strip leading verb: "open brave" → "brave"
        var stripped = Regex.Replace(
            name, @"^(open|launch|start|run)\s+", string.Empty,
            RegexOptions.IgnoreCase).Trim();

        if (aliasTarget == null)
            AppAliases.TryGetValue(stripped, out aliasTarget);

        var lookupKey = aliasTarget != null ? name : stripped;

        // For apps with desktop candidates, prefer the installed exe
        if (DesktopExeCandidates.TryGetValue(lookupKey,  out var candidates) ||
            DesktopExeCandidates.TryGetValue(stripped,   out candidates))
        {
            var exePath = FindFirstExisting(candidates);
            if (exePath != null)
            {
                AppDebugLog.Write($"ActionExecutor: resolved \"{name}\" to installed exe \"{exePath}\"");
                return exePath;
            }
            // Desktop app not found — fall through to web fallback
            AppDebugLog.Write($"ActionExecutor: \"{name}\" desktop not found, using web fallback");
        }

        if (aliasTarget != null) return aliasTarget;

        // Last resort: return the stripped name and let the shell try
        return stripped.Length > 0 ? stripped : name;
    }

    /// <summary>
    /// <summary>
    /// Expands environment variables and returns the first path that exists on disk.
    /// Supports a single wildcard segment anywhere in the path (e.g. app-*\Discord.exe):
    /// the non-wildcard prefix is treated as the search root, subdirectories matching the
    /// glob are scanned, and the latest match by last-write time is returned.
    /// </summary>
    private static string? FindFirstExisting(string[] candidates)
    {
        foreach (var raw in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(raw);

            if (!expanded.Contains('*'))
            {
                if (File.Exists(expanded)) return expanded;
                continue;
            }

            // Split at the first wildcard-containing segment.
            // e.g. "C:\Users\x\AppData\Local\Discord\app-*\Discord.exe"
            //   → searchRoot = "C:\Users\x\AppData\Local\Discord"
            //   → subdirGlob = "app-*"
            //   → fileName   = "Discord.exe"
            char sep = Path.DirectorySeparatorChar;
            var parts       = expanded.Split(sep, Path.AltDirectorySeparatorChar);
            int wildcardIdx = Array.FindIndex(parts, p => p.Contains('*'));
            if (wildcardIdx < 0) continue;

            string searchRoot = string.Join(sep.ToString(), parts[..wildcardIdx]);
            string subdirGlob = parts[wildcardIdx];
            string fileName   = string.Join(sep.ToString(), parts[(wildcardIdx + 1)..]);

            if (!Directory.Exists(searchRoot)) continue;

            var match = Directory
                .EnumerateDirectories(searchRoot, subdirGlob)
                .Select(d => Path.Combine(d, fileName))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (match != null) return match;
        }
        return null;
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
