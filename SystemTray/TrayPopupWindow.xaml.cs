using System.Windows;
using System.Windows.Media;
using ClickyWindows.Core;

namespace ClickyWindows.SystemTray;

/// <summary>
/// 320-pixel-wide dark popup panel shown below the system-tray icon.
/// Mirrors the Mac app's CompanionPanelView — same sections, same dark theme.
/// Non-activating: clicking a button does not steal focus from the user's app.
/// </summary>
public sealed partial class TrayPopupWindow : Window
{
    private readonly CompanionManager _manager;

    // ── Colors ────────────────────────────────────────────────────────────────

    private static readonly SolidColorBrush GreenBrush  = new(WpfColor.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush OrangeBrush = new(WpfColor.FromRgb(0xFF, 0xB2, 0x24));
    private static readonly SolidColorBrush RedBrush    = new(WpfColor.FromRgb(0xE5, 0x48, 0x4D));

    public TrayPopupWindow(CompanionManager manager)
    {
        InitializeComponent();

        _manager = manager;

        // Version
        var version = System.Reflection.Assembly.GetExecutingAssembly()
                            .GetName().Version;
        VersionLabel.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";

        // Populate from current state
        WorkerUrlBox.Text = _manager.Settings.WorkerBaseUrl;
        CursorToggle.IsChecked = _manager.Settings.IsClickyCursorEnabled;
        SelectModelComboItem(_manager.Settings.SelectedClaudeModel);

        RefreshPermissionUi();
        RefreshStatusUi();

        // Subscribe to changes
        _manager.PermissionsChanged += OnPermissionsChanged;
        _manager.VoiceStateChanged  += OnVoiceStateChanged;
        _manager.OverlayVisibilityChanged += OnOverlayVisibilityChanged;

        Closed += (_, _) =>
        {
            _manager.PermissionsChanged -= OnPermissionsChanged;
            _manager.VoiceStateChanged  -= OnVoiceStateChanged;
            _manager.OverlayVisibilityChanged -= OnOverlayVisibilityChanged;
        };
    }

    // ── Permission UI ─────────────────────────────────────────────────────────

    private void RefreshPermissionUi()
    {
        bool hasMic = _manager.HasMicrophonePermission;

        MicDot.Fill  = hasMic ? GreenBrush : RedBrush;
        MicLabel.Text = hasMic ? "Microphone" : "Microphone — click to grant";
        MicGrantBtn.Visibility = hasMic ? Visibility.Collapsed : Visibility.Visible;

        AllGrantedPanel.Visibility = hasMic ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPermissionsChanged() =>
        Dispatcher.Invoke(RefreshPermissionUi);

    // ── Status UI ─────────────────────────────────────────────────────────────

    private void RefreshStatusUi()
    {
        bool active = _manager.IsOverlayVisible;
        StatusLabel.Text = active ? "Active — Ctrl+Alt to talk" : "Ready — show cursor to begin";
    }

    private void OnVoiceStateChanged(VoiceState _) =>
        Dispatcher.Invoke(RefreshStatusUi);

    private void OnOverlayVisibilityChanged() =>
        Dispatcher.Invoke(RefreshStatusUi);

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnGrantMicClick(object sender, RoutedEventArgs e)
    {
        _manager.RequestMicrophonePermission();
    }

    private void OnCursorToggleChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = CursorToggle.IsChecked == true;
        _manager.SetClickyCursorEnabled(enabled);
    }

    private void OnModelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string model)
        {
            _manager.SetSelectedModel(model);
        }
    }

    private void OnWorkerUrlLostFocus(object sender, RoutedEventArgs e) => SaveWorkerUrl();
    private void OnWorkerUrlKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SaveWorkerUrl();
    }
    private void OnSaveWorkerUrl(object sender, RoutedEventArgs e) => SaveWorkerUrl();

    private void SaveWorkerUrl()
    {
        var url = WorkerUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
            _manager.SetWorkerBaseUrl(url);
    }

    private void OnReplayOnboardingClick(object sender, RoutedEventArgs e)
    {
        _manager.ReplayOnboarding();
        Hide();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        WpfApp.Current.Shutdown();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SelectModelComboItem(string model)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in ModelCombo.Items)
        {
            if (item.Tag as string == model)
            {
                ModelCombo.SelectedItem = item;
                return;
            }
        }
        // Fall back to first item
        if (ModelCombo.Items.Count > 0)
            ModelCombo.SelectedIndex = 0;
    }
}

