using System.Runtime.InteropServices;
using System.Windows.Threading;
using ClickyWindows.Core;

namespace ClickyWindows.Input;

/// <summary>
/// Monitors a global Ctrl+Alt push-to-talk hotkey using a low-level keyboard hook.
///
/// Key insight: GetAsyncKeyState and GetKeyboardState are unreliable for low-level hook
/// modifier tracking on the WPF dispatcher thread — they can return "not pressed" even
/// while keys are physically held. We therefore track chord state purely from the hook
/// message stream: explicit WM_KEYDOWN / WM_SYSKEYDOWN increments a per-key held counter;
/// WM_KEYUP / WM_SYSKEYUP decrements it. Released fires only when both modifier groups
/// have reached zero hold count.
/// </summary>
public sealed class GlobalHotkeyMonitor : IDisposable
{
    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

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
    private LowLevelKeyboardProc? _hookProc;
    private bool _isStarted;

    // Hold-count per modifier group. Using counts (not bools) handles any
    // keyboard that sends repeated KEYDOWN without an intervening KEYUP.
    private int _ctrlHeld;   // incremented by L/R Ctrl down; decremented by up
    private int _altHeld;    // incremented by L/R Alt  down; decremented by up

    private bool _chordActive; // true once both > 0; cleared on both-zero

    // ── Lifecycle ─────────────────────────────────────────────────────────────

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

    public void Stop()
    {
        if (!_isStarted) return;
        _isStarted = false;

        _ctrlHeld = 0;
        _altHeld = 0;
        _chordActive = false;

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

            bool isDown = msg is NativeConstants.WM_KEYDOWN or NativeConstants.WM_SYSKEYDOWN;
            bool isUp   = msg is NativeConstants.WM_KEYUP   or NativeConstants.WM_SYSKEYUP;

            if (!isDown && !isUp)
            {
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            bool isCtrl = kbd.vkCode is NativeConstants.VK_LCONTROL or NativeConstants.VK_RCONTROL
                                      or NativeConstants.VK_CONTROL;
            bool isAlt  = kbd.vkCode is NativeConstants.VK_LMENU or NativeConstants.VK_RMENU
                                      or NativeConstants.VK_MENU;

            if (!isCtrl && !isAlt)
            {
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // Update hold counts, clamped to [0, ∞).
            if (isCtrl)
                _ctrlHeld = isDown ? _ctrlHeld + 1 : Math.Max(0, _ctrlHeld - 1);
            if (isAlt)
                _altHeld  = isDown ? _altHeld  + 1 : Math.Max(0, _altHeld  - 1);

            bool chordNow = _ctrlHeld > 0 && _altHeld > 0;

            if (chordNow && !_chordActive)
            {
                _chordActive = true;
                AppDebugLog.Write($"[Hotkey] Chord DOWN  ctrl={_ctrlHeld} alt={_altHeld}");
                ShortcutTransition?.Invoke(HotkeyTransition.Pressed);
            }
            else if (!chordNow && _chordActive)
            {
                _chordActive = false;
                AppDebugLog.Write($"[Hotkey] Chord UP    ctrl={_ctrlHeld} alt={_altHeld}");
                ShortcutTransition?.Invoke(HotkeyTransition.Released);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => Stop();
}
