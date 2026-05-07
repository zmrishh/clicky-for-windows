using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClickyWindows.Core;

namespace ClickyWindows.Input;

/// <summary>
/// Monitors a global Ctrl+Alt push-to-talk hotkey using a low-level keyboard hook.
///
/// Why a low-level hook instead of Win32 RegisterHotKey:
///   RegisterHotKey cannot detect modifier-only chords (Ctrl+Alt pressed/released
///   without an additional VK) reliably. The Mac CGEvent tap listens to flagsChanged
///   events on Ctrl+Option; the Windows equivalent is a WH_KEYBOARD_LL hook that
///   watches for both Ctrl and Alt held simultaneously via GetAsyncKeyState.
///
/// The hook callback runs on the thread that installed it (the UI thread), matching
/// the Mac CGEvent tap behaviour where the callback runs on CFRunLoopGetMain().
/// </summary>
public sealed class GlobalHotkeyMonitor : IDisposable
{
    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int HC_ACTION = 0;

    // ── Public events ─────────────────────────────────────────────────────────

    public event Action<HotkeyTransition>? ShortcutTransition;

    // ── State ─────────────────────────────────────────────────────────────────

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;   // Keep delegate alive to prevent GC
    private bool _isShortcutPressed;
    private bool _isStarted;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Installs the low-level keyboard hook. Must be called on the UI thread.
    /// Requires the process to have accessibility/debug privileges on some systems
    /// (standard user is sufficient on Windows 10+).
    /// </summary>
    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;

        _hookProc = HookCallback;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookHandle = SetWindowsHookEx(
            NativeConstants.WH_KEYBOARD_LL,
            _hookProc,
            GetModuleHandle(module.ModuleName),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            Console.WriteLine($"⚠️ [Hotkey] Failed to install keyboard hook (error {Marshal.GetLastWin32Error()})");
        }
        else
        {
            Console.WriteLine("✅ [Hotkey] Low-level keyboard hook installed");
        }
    }

    /// <summary>Removes the keyboard hook. Safe to call multiple times.</summary>
    public void Stop()
    {
        if (!_isStarted) return;
        _isStarted = false;
        _isShortcutPressed = false;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    // ── Hook callback ─────────────────────────────────────────────────────────

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;

            bool isKeyDown = msg is NativeConstants.WM_KEYDOWN or NativeConstants.WM_SYSKEYDOWN;
            bool isKeyUp   = msg is NativeConstants.WM_KEYUP   or NativeConstants.WM_SYSKEYUP;

            // We only care about modifier key transitions
            bool isModifierKey = kbd.vkCode is NativeConstants.VK_CONTROL or NativeConstants.VK_MENU
                                              or NativeConstants.VK_LCONTROL or NativeConstants.VK_RCONTROL
                                              or NativeConstants.VK_LMENU    or NativeConstants.VK_RMENU;

            if (isModifierKey && (isKeyDown || isKeyUp))
                EvaluateShortcutState();
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Checks whether both Ctrl and Alt are currently held using GetAsyncKeyState.
    /// Mirrors the Mac CGEvent flagsChanged handler that checks .control + .option.
    /// </summary>
    private void EvaluateShortcutState()
    {
        bool ctrlDown = (GetAsyncKeyState(NativeConstants.VK_CONTROL) & 0x8000) != 0;
        bool altDown  = (GetAsyncKeyState(NativeConstants.VK_MENU)    & 0x8000) != 0;
        bool isNowPressed = ctrlDown && altDown;

        if (isNowPressed && !_isShortcutPressed)
        {
            _isShortcutPressed = true;
            ShortcutTransition?.Invoke(HotkeyTransition.Pressed);
        }
        else if (!isNowPressed && _isShortcutPressed)
        {
            _isShortcutPressed = false;
            ShortcutTransition?.Invoke(HotkeyTransition.Released);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => Stop();
}
