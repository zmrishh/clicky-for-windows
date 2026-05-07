using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClickyWindows.Core;
using ClickyWindows.ScreenCapture;
using Point = System.Windows.Point;

namespace ClickyWindows.Overlay;

/// <summary>
/// Full-screen transparent WPF overlay for one monitor.
/// Renders the blue triangle cursor, waveform, spinner, and speech bubbles.
/// Hosts the bezier arc flight animation and element pointing behaviour.
///
/// All state is driven by <see cref="CompanionManager"/> property changes, which
/// this window subscribes to on construction. The window is purely visual —
/// it never receives or blocks input (IsHitTestVisible=False, WS_EX_TRANSPARENT).
/// </summary>
public sealed partial class OverlayWindow : Window
{
    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly CompanionManager _manager;

    // The physical bounds of the monitor this overlay covers (physical pixels).
    public System.Drawing.Rectangle PhysicalBounds { get; }

    // Per-monitor DPI scale (e.g. 1.5 for 144dpi). Used to convert physical pixel
    // coords from CompanionManager into WPF canvas DIPs.
    private readonly double _dpiScale;

    // Current cursor position in canvas DIPs (top-left origin of this canvas).
    private Point _cursorPos;

    // Navigation state — mirrors BuddyNavigationMode
    private BuddyNavigationMode _navMode = BuddyNavigationMode.FollowingCursor;
    private bool _isReturningToCursor;
    private Point _cursorPosAtNavStart;

    // Bezier animation state
    private DispatcherTimer? _flightTimer;
    private Point _flightStart;
    private Point _flightEnd;
    private Point _flightControl;
    private int _flightFrame;
    private int _flightTotalFrames;
    private Action? _flightOnComplete;

    // Cursor tracking timer (16ms ≈ 60fps)
    private DispatcherTimer? _cursorTimer;

    // Waveform animation timer (28ms ≈ 36fps)
    private DispatcherTimer? _waveTimer;

    // Spinner rotation animation (started once in OnLoaded via Storyboard)
    // kept as a reference so it can be stopped on Close
    private System.Windows.Media.Animation.Storyboard? _spinnerStoryboard;

    // Onboarding
    private bool _isFirstAppearance;
    private bool _showWelcome = true;
    private string _welcomeBuilt = "";
    private DispatcherTimer? _charStreamTimer;

    // Bubble dimensions for positioning
    private double _welcomeBubbleWidth;
    private double _navBubbleWidth;

    // Waveform profile
    private static readonly double[] WaveBarProfile = [0.4, 0.7, 1.0, 0.7, 0.4];
    private readonly System.Windows.Shapes.Rectangle[] _waveBars;

    // ── Constructor ───────────────────────────────────────────────────────────

    public OverlayWindow(
        CompanionManager manager,
        System.Drawing.Rectangle physicalBounds,
        double dpiScale,
        bool isFirstAppearance)
    {
        InitializeComponent();

        _manager       = manager;
        PhysicalBounds = physicalBounds;
        _dpiScale      = dpiScale;
        _isFirstAppearance = isFirstAppearance;

        // Size the canvas to cover the monitor in DIPs
        double dipW = physicalBounds.Width  / dpiScale;
        double dipH = physicalBounds.Height / dpiScale;
        Width  = dipW;
        Height = dipH;
        RootCanvas.Width  = dipW;
        RootCanvas.Height = dipH;

        _waveBars = [(System.Windows.Shapes.Rectangle)WaveBar0, (System.Windows.Shapes.Rectangle)WaveBar1, (System.Windows.Shapes.Rectangle)WaveBar2, (System.Windows.Shapes.Rectangle)WaveBar3, (System.Windows.Shapes.Rectangle)WaveBar4];

        // Seed cursor position
        var cp = System.Windows.Forms.Cursor.Position;
        _cursorPos = new Point(
            (cp.X - physicalBounds.Left) / dpiScale + AppConstants.CursorOffsetX,
            (cp.Y - physicalBounds.Top)  / dpiScale + AppConstants.CursorOffsetY);

        Loaded += OnLoaded;
        Closed += (s, e) => OnClosed(s!, e);

        // Subscribe to CompanionManager state changes
        _manager.VoiceStateChanged       += OnVoiceStateChanged;
        _manager.AudioPowerLevelChanged  += OnAudioPowerLevelChanged;
        _manager.DetectedElementChanged  += OnDetectedElementChanged;
        _manager.OnboardingVideoChanged  += OnOnboardingVideoChanged;
        _manager.OnboardingPromptChanged += OnOnboardingPromptChanged;
    }

    // ── Loaded / Closed ───────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MakeClickThrough();

        // Exclude from screen capture so GDI+ doesn't include the overlay
        ScreenCaptureService.ExcludeWindowFromCapture(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);

        StartCursorTracking();
        StartWaveformAnimation();
        StartSpinnerAnimation();

        if (_isFirstAppearance && IsCursorOnThisScreen())
        {
            // Fade in cursor, then run welcome animation
            FadeTo(CursorTriangle, 1.0, TimeSpan.FromSeconds(2.0));
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000);
                StartWelcomeAnimation();
            });
        }
        else
        {
            CursorTriangle.Opacity = 1.0;
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _manager.VoiceStateChanged       -= OnVoiceStateChanged;
        _manager.AudioPowerLevelChanged  -= OnAudioPowerLevelChanged;
        _manager.DetectedElementChanged  -= OnDetectedElementChanged;
        _manager.OnboardingVideoChanged  -= OnOnboardingVideoChanged;
        _manager.OnboardingPromptChanged -= OnOnboardingPromptChanged;

        _cursorTimer?.Stop();
        _flightTimer?.Stop();
        _waveTimer?.Stop();
        _charStreamTimer?.Stop();
        _spinnerStoryboard?.Stop();
        StopOnboardingVideo();
    }

    // ── Win32: make click-through ─────────────────────────────────────────────

    private void MakeClickThrough()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int style = GetWindowLong(hwnd, NativeConstants.GWL_EXSTYLE);
        style |= NativeConstants.WS_EX_TRANSPARENT | NativeConstants.WS_EX_LAYERED
                 | NativeConstants.WS_EX_NOACTIVATE | NativeConstants.WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, NativeConstants.GWL_EXSTYLE, style);
    }

    // ── Cursor tracking ───────────────────────────────────────────────────────

    private void StartCursorTracking()
    {
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _cursorTimer.Tick += OnCursorTick;
        _cursorTimer.Start();
    }

    private void OnCursorTick(object? sender, EventArgs e)
    {
        var cp   = System.Windows.Forms.Cursor.Position;
        bool onThis = IsCursorOnThisScreen();

        if (_navMode == BuddyNavigationMode.NavigatingToTarget && _isReturningToCursor)
        {
            var current = PhysicalToCanvas(cp.X, cp.Y);
            double dist = Distance(current, _cursorPosAtNavStart);
            if (dist > AppConstants.NavigationCancelDistanceDip)
                CancelNavigationAndResumeFollowing();
            return;
        }

        if (_navMode != BuddyNavigationMode.FollowingCursor) return;

        if (!onThis) return;

        double targetX = (cp.X - PhysicalBounds.Left) / _dpiScale + AppConstants.CursorOffsetX;
        double targetY = (cp.Y - PhysicalBounds.Top)  / _dpiScale + AppConstants.CursorOffsetY;

        _cursorPos = new Point(targetX, targetY);
        SetElementPosition(CursorTriangle,   TriangleTranslate,  targetX, targetY);
        SetElementPosition(WaveformCanvas,   WaveformTranslate,  targetX, targetY);
        SetElementPosition(SpinnerCanvas,    SpinnerTranslate,   targetX, targetY);
        UpdateBubblePosition(targetX, targetY);
    }

    private bool IsCursorOnThisScreen()
    {
        var cp = System.Windows.Forms.Cursor.Position;
        return PhysicalBounds.Contains(cp.X, cp.Y);
    }

    // ── Voice state observer ──────────────────────────────────────────────────

    private void OnVoiceStateChanged(VoiceState state)
    {
        bool showTriangle  = state is VoiceState.Idle or VoiceState.Responding;
        bool showWaveform  = state == VoiceState.Listening;
        bool showSpinner   = state == VoiceState.Processing;

        bool isOnThis = IsCursorOnThisScreen();

        // Only show visual on this screen when cursor is here (or navigating here)
        bool visible = _navMode != BuddyNavigationMode.FollowingCursor || isOnThis;
        // If another screen is running a navigation, hide here to avoid duplicate
        if (_navMode == BuddyNavigationMode.FollowingCursor && _manager.HasDetectedElement)
            visible = false;

        var dur = TimeSpan.FromSeconds(0.2);

        FadeTo(CursorTriangle,  showTriangle && visible  ? 1.0 : 0.0, dur);
        FadeTo(WaveformCanvas,  showWaveform && visible  ? 1.0 : 0.0, dur);
        FadeTo(SpinnerCanvas,   showSpinner  && visible  ? 1.0 : 0.0, dur);
    }

    private void OnAudioPowerLevelChanged(float level)
    {
        // Waveform bars are driven by the waveform timer using this level
    }

    // ── Detected element observer ─────────────────────────────────────────────

    private void OnDetectedElementChanged()
    {
        if (_manager.DetectedElementDisplayFrame == null ||
            _manager.DetectedElementCanvasPoint == null)
            return;

        // Only the screen whose bounds contain the target's display frame handles it
        var frame = _manager.DetectedElementDisplayFrame.Value;
        bool isOurScreen = PhysicalBounds.IntersectsWith(new System.Drawing.Rectangle(
            frame.X, frame.Y, frame.Width, frame.Height));

        if (!isOurScreen) return;

        var target = _manager.DetectedElementCanvasPoint.Value;
        StartNavigatingToElement(target);
    }

    // ── Waveform animation ────────────────────────────────────────────────────

    private void StartWaveformAnimation()
    {
        _waveTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(28) // ~36fps
        };
        _waveTimer.Tick += OnWaveTick;
        _waveTimer.Start();
    }

    private double _wavePhase;

    private void OnWaveTick(object? sender, EventArgs e)
    {
        _wavePhase += 0.1;
        float power = _manager.AudioPowerLevel;
        float normalised = Math.Max(power - 0.008f, 0f);
        double eased = Math.Pow(Math.Min(normalised * 2.85, 1.0), 0.76);

        for (int i = 0; i < 5; i++)
        {
            double animPhase = _wavePhase + i * 0.35;
            double reactive = eased * 10.0 * WaveBarProfile[i];
            double idle = (Math.Sin(animPhase) + 1.0) / 2.0 * 1.5;
            double h = Math.Max(3.0 + reactive + idle, 3.0);

            _waveBars[i].Height = h;
            // Align bar bottom to canvas bottom (14px container)
            System.Windows.Controls.Canvas.SetTop(_waveBars[i], WaveformCanvas.Height - h);
        }
    }

    // ── Spinner animation ─────────────────────────────────────────────────────

    private void StartSpinnerAnimation()
    {
        var anim = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.8)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, SpinnerRotate);
        Storyboard.SetTargetProperty(anim, new PropertyPath(RotateTransform.AngleProperty));

        _spinnerStoryboard = new Storyboard();
        _spinnerStoryboard.Children.Add(anim);
        _spinnerStoryboard.Begin();
    }

    // ── Element navigation (bezier arc flight) ────────────────────────────────

    private void StartNavigatingToElement(Point targetCanvas)
    {
        if (_showWelcome && !string.IsNullOrEmpty(_welcomeBuilt)) return;

        var cp = System.Windows.Forms.Cursor.Position;
        _cursorPosAtNavStart = PhysicalToCanvas(cp.X, cp.Y);

        var offsetTarget = new Point(
            targetCanvas.X + AppConstants.TargetOffsetX,
            targetCanvas.Y + AppConstants.TargetOffsetY);

        double clampedX = Math.Max(20, Math.Min(offsetTarget.X, Width  - 20));
        double clampedY = Math.Max(20, Math.Min(offsetTarget.Y, Height - 20));
        var clamped = new Point(clampedX, clampedY);

        _navMode = BuddyNavigationMode.NavigatingToTarget;
        _isReturningToCursor = false;

        AnimateBezierFlight(clamped, () =>
        {
            if (_navMode == BuddyNavigationMode.NavigatingToTarget)
                StartPointingAtElement();
        });
    }

    /// <summary>
    /// Quadratic bezier arc flight at 60fps. Mirrors the Swift animateBezierFlightArc
    /// with the same smoothstep easing, tangent rotation, and scale pulse.
    /// </summary>
    private void AnimateBezierFlight(Point destination, Action onComplete)
    {
        _flightTimer?.Stop();

        _flightStart = _cursorPos;
        _flightEnd   = destination;

        double deltaX = destination.X - _flightStart.X;
        double deltaY = destination.Y - _flightStart.Y;
        double dist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

        double durationSec = Math.Clamp(dist / 800.0,
            AppConstants.FlightMinSeconds, AppConstants.FlightMaxSeconds);

        _flightTotalFrames = (int)(durationSec / (AppConstants.FlightFrameIntervalMs / 1000.0));
        _flightFrame = 0;
        _flightOnComplete = onComplete;

        // Arc control point — offset midpoint upward
        var mid = new Point(
            (_flightStart.X + destination.X) / 2.0,
            (_flightStart.Y + destination.Y) / 2.0);
        double arcHeight = Math.Min(dist * 0.2, 80.0);
        _flightControl = new Point(mid.X, mid.Y - arcHeight);

        _flightTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(AppConstants.FlightFrameIntervalMs)
        };
        _flightTimer.Tick += OnFlightTick;
        _flightTimer.Start();
    }

    private void OnFlightTick(object? sender, EventArgs e)
    {
        _flightFrame++;

        if (_flightFrame >= _flightTotalFrames)
        {
            _flightTimer!.Stop();
            _flightTimer = null;
            _cursorPos = _flightEnd;
            TriangleScale.ScaleX = 1.0;
            TriangleScale.ScaleY = 1.0;
            TriangleGlow.BlurRadius = 10;
            SetAllPositions(_flightEnd);
            _flightOnComplete?.Invoke();
            return;
        }

        double linear = (double)_flightFrame / _flightTotalFrames;
        // Smoothstep: 3t² - 2t³ (Hermite interpolation)
        double t = linear * linear * (3.0 - 2.0 * linear);
        double u = 1.0 - t;

        // Quadratic bezier position
        double bx = u * u * _flightStart.X + 2 * u * t * _flightControl.X + t * t * _flightEnd.X;
        double by = u * u * _flightStart.Y + 2 * u * t * _flightControl.Y + t * t * _flightEnd.Y;
        var pos = new Point(bx, by);

        _cursorPos = pos;
        SetAllPositions(pos);

        // Tangent rotation: face direction of travel
        double tx = 2 * u * (_flightControl.X - _flightStart.X) + 2 * t * (_flightEnd.X - _flightControl.X);
        double ty = 2 * u * (_flightControl.Y - _flightStart.Y) + 2 * t * (_flightEnd.Y - _flightControl.Y);
        double angle = Math.Atan2(ty, tx) * (180.0 / Math.PI) + 90.0;
        TriangleRotate.Angle = angle;

        // Scale pulse: grows to ~1.3x at arc midpoint
        double scalePulse = Math.Sin(linear * Math.PI);
        double scale = 1.0 + scalePulse * 0.3;
        TriangleScale.ScaleX = scale;
        TriangleScale.ScaleY = scale;
        TriangleGlow.BlurRadius = 10 + scalePulse * 20;
    }

    private void StartPointingAtElement()
    {
        _navMode = BuddyNavigationMode.PointingAtTarget;
        TriangleRotate.Angle = -35.0;

        string phrase = _manager.DetectedElementBubbleText
                        ?? PickRandomPointerPhrase();

        NavBubble.Visibility = Visibility.Visible;
        NavBubble.Opacity = 1.0;
        NavBubbleText.Text = "";
        NavBubbleScale.ScaleX = 0.5;
        NavBubbleScale.ScaleY = 0.5;

        // Reset bubble scale center to top-left of bubble
        NavBubbleScale.CenterX = 0;
        NavBubbleScale.CenterY = 0;

        StreamTextIntoLabel(NavBubbleText, phrase, () =>
        {
            // Measure bubble width now that text is set
            NavBubble.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
            _navBubbleWidth = NavBubble.DesiredSize.Width;

            // Scale-bounce entrance on first character
            var scaleAnim = new DoubleAnimation(0.5, 1.0, new Duration(TimeSpan.FromSeconds(0.4)))
            {
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 3 }
            };
            NavBubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            NavBubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

            // After 3 seconds, fade out and fly back
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(AppConstants.PointingHoldSeconds));
                if (_navMode != BuddyNavigationMode.PointingAtTarget) return;

                FadeTo(NavBubble, 0.0, TimeSpan.FromSeconds(0.5));
                await Task.Delay(500);
                if (_navMode != BuddyNavigationMode.PointingAtTarget) return;

                StartFlyingBackToCursor();
            });
        });
    }

    private void StartFlyingBackToCursor()
    {
        var cp = System.Windows.Forms.Cursor.Position;
        var cursorInCanvas = PhysicalToCanvas(cp.X, cp.Y);
        _cursorPosAtNavStart = cursorInCanvas;

        var target = new Point(
            cursorInCanvas.X + AppConstants.CursorOffsetX,
            cursorInCanvas.Y + AppConstants.CursorOffsetY);

        _navMode = BuddyNavigationMode.NavigatingToTarget;
        _isReturningToCursor = true;

        AnimateBezierFlight(target, FinishNavigation);
    }

    private void CancelNavigationAndResumeFollowing()
    {
        _flightTimer?.Stop();
        _flightTimer = null;
        NavBubble.Visibility = Visibility.Collapsed;
        TriangleScale.ScaleX = 1.0;
        TriangleScale.ScaleY = 1.0;
        TriangleGlow.BlurRadius = 10;
        FinishNavigation();
    }

    private void FinishNavigation()
    {
        _flightTimer?.Stop();
        _flightTimer = null;
        _navMode = BuddyNavigationMode.FollowingCursor;
        _isReturningToCursor = false;
        TriangleRotate.Angle = -35.0;
        TriangleScale.ScaleX = 1.0;
        TriangleScale.ScaleY = 1.0;
        TriangleGlow.BlurRadius = 10;
        NavBubble.Visibility = Visibility.Collapsed;
        NavBubbleText.Text = "";
        _manager.ClearDetectedElement();
    }

    // ── Text streaming ────────────────────────────────────────────────────────

    private void StreamTextIntoLabel(
        System.Windows.Controls.TextBlock label,
        string text,
        Action? onComplete = null)
    {
        _charStreamTimer?.Stop();
        int index = 0;

        _charStreamTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(AppConstants.CharacterStreamIntervalSeconds)
        };
        _charStreamTimer.Tick += (_, _) =>
        {
            if (index >= text.Length)
            {
                _charStreamTimer.Stop();
                onComplete?.Invoke();
                return;
            }
            label.Text += text[index++];
        };
        _charStreamTimer.Start();
    }

    // ── Welcome animation ─────────────────────────────────────────────────────

    private void StartWelcomeAnimation()
    {
        WelcomeBubble.Visibility = Visibility.Visible;
        FadeTo(WelcomeBubble, 1.0, TimeSpan.FromSeconds(0.4));

        StreamTextIntoLabel(WelcomeText, AppConstants.WelcomeMessage, () =>
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000);
                FadeTo(WelcomeBubble, 0.0, TimeSpan.FromSeconds(0.4));
                await Task.Delay(500);
                _showWelcome = false;
                WelcomeBubble.Visibility = Visibility.Collapsed;
                // Trigger onboarding video
                _manager.SetupOnboardingVideo();
            });
        });
    }

    // ── Onboarding video ──────────────────────────────────────────────────────

    private void OnOnboardingVideoChanged()
    {
        if (!IsCursorOnThisScreen()) return;

        if (_manager.ShowOnboardingVideo && _manager.OnboardingVideoUri != null)
        {
            VideoContainer.Visibility = Visibility.Visible;
            OnboardingVideo.MediaEnded -= OnOnboardingVideoEnded;
            OnboardingVideo.MediaEnded += OnOnboardingVideoEnded;
            OnboardingVideo.Source = _manager.OnboardingVideoUri;
            OnboardingVideo.Play();
            FadeTo(VideoContainer, 1.0, TimeSpan.FromSeconds(2.0));
        }
        else
        {
            FadeTo(VideoContainer, 0.0, TimeSpan.FromSeconds(2.0));
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2100);
                VideoContainer.Visibility = Visibility.Collapsed;
                OnboardingVideo.Stop();
                OnboardingVideo.Source = null;
            });
        }

        UpdateVideoPosition();
    }

    private void OnOnboardingVideoEnded(object sender, RoutedEventArgs e)
    {
        OnboardingVideo.MediaEnded -= OnOnboardingVideoEnded;
        _manager.TearDownOnboardingVideo();
    }

    private void StopOnboardingVideo()
    {
        OnboardingVideo.MediaEnded -= OnOnboardingVideoEnded;
        OnboardingVideo.Stop();
        OnboardingVideo.Source = null;
    }

    // ── Onboarding prompt ─────────────────────────────────────────────────────

    private void OnOnboardingPromptChanged()
    {
        if (!IsCursorOnThisScreen()) return;

        if (_manager.ShowOnboardingPrompt)
        {
            WelcomeText.Text = "";
            WelcomeBubble.Visibility = Visibility.Visible;
            FadeTo(WelcomeBubble, 1.0, TimeSpan.FromSeconds(0.4));

            StreamTextIntoLabel(WelcomeText, AppConstants.OnboardingPromptMessage, () =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(AppConstants.OnboardingPromptAutoDismissSeconds));
                    if (!_manager.ShowOnboardingPrompt) return;
                    FadeTo(WelcomeBubble, 0.0, TimeSpan.FromSeconds(0.3));
                    await Task.Delay(350);
                    WelcomeBubble.Visibility = Visibility.Collapsed;
                    _manager.ClearOnboardingPrompt();
                });
            });
        }
        else
        {
            FadeTo(WelcomeBubble, 0.0, TimeSpan.FromSeconds(0.3));
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(350);
                WelcomeBubble.Visibility = Visibility.Collapsed;
            });
        }
    }

    // ── Position helpers ──────────────────────────────────────────────────────

    private void SetAllPositions(Point p)
    {
        SetElementPosition(CursorTriangle,  TriangleTranslate,  p.X, p.Y);
        SetElementPosition(WaveformCanvas,  WaveformTranslate,  p.X, p.Y);
        SetElementPosition(SpinnerCanvas,   SpinnerTranslate,   p.X, p.Y);
        UpdateBubblePosition(p.X, p.Y);
    }

    private static void SetElementPosition(
        UIElement element,
        TranslateTransform translate,
        double x, double y)
    {
        translate.X = x;
        translate.Y = y;
    }

    private void UpdateBubblePosition(double x, double y)
    {
        // Welcome bubble: cursor + 10px right, same vertical
        WelcomeBubble.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        _welcomeBubbleWidth = WelcomeBubble.DesiredSize.Width;
        BubbleTranslate.X = x + 10;
        BubbleTranslate.Y = y + 18;

        // Nav bubble follows cursor too
        System.Windows.Controls.Canvas.SetLeft(NavBubble, x + 10);
        System.Windows.Controls.Canvas.SetTop(NavBubble, y + 18);

        // Video: cursor + 10px right, 18px below
        VideoTranslate.X = x + 10;
        VideoTranslate.Y = y + 18;
    }

    private void UpdateVideoPosition()
    {
        VideoTranslate.X = _cursorPos.X + 10;
        VideoTranslate.Y = _cursorPos.Y + 18;
    }

    private Point PhysicalToCanvas(int physX, int physY) => new(
        (physX - PhysicalBounds.Left) / _dpiScale,
        (physY - PhysicalBounds.Top)  / _dpiScale);

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ── Fade helper ───────────────────────────────────────────────────────────

    private static void FadeTo(UIElement element, double target, TimeSpan duration)
    {
        var anim = new DoubleAnimation(target, new Duration(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(OpacityProperty, anim);
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    private static readonly string[] PointerPhrases =
        ["right here!", "this one!", "over here!", "click this!", "here it is!", "found it!"];

    private static readonly Random _rng = new();
    private static string PickRandomPointerPhrase() =>
        PointerPhrases[_rng.Next(PointerPhrases.Length)];
}

