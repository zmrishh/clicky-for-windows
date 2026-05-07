using Microsoft.Win32;
using System.Windows;
using ClickyWindows.Core;
using ClickyWindows.SystemTray;
using WinApplication = System.Windows.Application;

namespace ClickyWindows;

/// <summary>
/// Application entry point. No main window — the app lives entirely in the system tray.
/// Mirrors the Mac app's CompanionAppDelegate:
///   - Initialises CompanionManager and TrayIconManager
///   - Registers as a login item so the app auto-starts with Windows
///   - Auto-opens the tray popup when setup is needed
///   - Shuts down cleanly on Exit
/// </summary>
public sealed partial class App : System.Windows.Application
{
    private CompanionManager? _manager;
    private TrayIconManager?  _trayIcon;

    // ── Startup ───────────────────────────────────────────────────────────────

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Console.WriteLine("🎯 Clicky Windows: Starting...");
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Console.WriteLine($"🎯 Version {version}");

        // Prevent multiple instances
        if (IsAlreadyRunning())
        {
            Console.WriteLine("⚠️ Clicky is already running. Exiting.");
            Shutdown();
            return;
        }

        _manager  = new CompanionManager();
        _trayIcon = new TrayIconManager(_manager);

        _manager.Start();

        RegisterStartupItem();

        // Auto-open the popup if:
        //   - User has not completed onboarding yet, OR
        //   - Worker URL is still the placeholder
        bool needsSetup = !_manager.Settings.HasCompletedOnboarding
                          || _manager.Settings.WorkerBaseUrl == AppConstants.WorkerBaseUrl;

        if (needsSetup)
            _trayIcon.ShowPanelOnLaunch();
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.Stop();
        _manager?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    // ── Single instance guard ─────────────────────────────────────────────────

    private static System.Threading.Mutex? _singleInstanceMutex;

    private static bool IsAlreadyRunning()
    {
        _singleInstanceMutex = new System.Threading.Mutex(
            initiallyOwned: true,
            name: "Global\\ClickyWindowsSingleInstance",
            out bool createdNew);

        return !createdNew;
    }

    // ── Login item (startup with Windows) ─────────────────────────────────────

    /// <summary>
    /// Registers the app in HKCU Run so it launches at Windows login.
    /// Visible to the user in Task Manager > Startup Apps and can be toggled off there.
    /// Mirrors SMAppService.mainApp.register() on macOS.
    /// </summary>
    private static void RegisterStartupItem()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(
                AppConstants.StartupRegistryKey, writable: true);

            if (key == null) return;

            var current = key.GetValue(AppConstants.StartupRegistryValueName) as string;
            if (current != exePath)
            {
                key.SetValue(AppConstants.StartupRegistryValueName, exePath);
                Console.WriteLine("🎯 Clicky: registered as login item");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to register startup item: {ex.Message}");
        }
    }
}
