using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;
using ManagedShell.WindowsTray;
using RetroBar.Utilities;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace RetroBar
{
    /// <summary>
    /// Interaction logic for Taskbar.xaml
    /// </summary>
    public partial class Taskbar : AppBarWindow
    {
        private DispatcherTimer _maximizedWindowTimer;

        public static readonly DependencyProperty HasMaximizedWindowProperty =
            DependencyProperty.Register("HasMaximizedWindow", typeof(bool), typeof(Taskbar),
                new PropertyMetadata(false));

        public bool HasMaximizedWindow
        {
            get => (bool)GetValue(HasMaximizedWindowProperty);
            private set => SetValue(HasMaximizedWindowProperty, value);
        }
        public static readonly DependencyProperty IsDraggingProperty =
            DependencyProperty.Register("IsDragging", typeof(bool), typeof(Taskbar),
                new PropertyMetadata(false));

        // True while the user is dragging a free-floating taskbar; themes use it for feedback.
        public bool IsDragging
        {
            get => (bool)GetValue(IsDraggingProperty);
            private set => SetValue(IsDraggingProperty, value);
        }

        public bool IsLocked => Settings.Instance.LockTaskbar;
        public bool IsScaled => DpiScale > 1 || Settings.Instance.TaskbarScale > 1;

        private double _unlockedMargin;
        public double DesiredRowHeight { get; private set; }

        public int Rows
        {
            get => Settings.Instance.RowCount;
            set => Settings.Instance.RowCount = value;
        }

        private bool _startMenuOpen;
        private LowLevelMouseHook _mouseDragHook;
        private Point? _mouseDragStart = null;
        private bool _mouseDragResize = false;
        private readonly DictionaryManager _dictionaryManager;
        private readonly ShellManager _shellManager;
        private readonly StartMenuMonitor _startMenuMonitor;
        private readonly Updater _updater;
        private bool _fullScreenSuppressed;
        private int _openMenus;
        
        public WindowManager windowManager;
        public HotkeyManager hotkeyManager;

        public Taskbar(WindowManager windowManager, DictionaryManager dictionaryManager, ShellManager shellManager, StartMenuMonitor startMenuMonitor, Updater updater, HotkeyManager hotkeyManager, AppBarScreen screen, AppBarEdge edge, AppBarMode mode)
            : base(shellManager.AppBarManager, shellManager.ExplorerHelper, shellManager.FullScreenHelper, screen, edge, mode, 0)
        {
            _dictionaryManager = dictionaryManager;
            _shellManager = shellManager;
            _startMenuMonitor = startMenuMonitor;
            _updater = updater;
            this.windowManager = windowManager;
            this.hotkeyManager = hotkeyManager;

            InitializeComponent();

            _maximizedWindowTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };

            _maximizedWindowTimer.Tick += MaximizedWindowTimer_Tick;

            // Only themes that opt in via AlternatesAppearanceWhenMaximized need the polling.
            if (AlternatesAppearanceWhenMaximized())
            {
                _maximizedWindowTimer.Start();
            }

            DataContext = _shellManager;
            
            StartButton.StartMenuMonitor = startMenuMonitor;
            
            RecalculateSize(false);

            AllowsTransparency = mode == AppBarMode.AutoHide || (Application.Current.FindResource("AllowsTransparency") as bool? ?? false);

            FlowDirection = Application.Current.FindResource("flow_direction") as FlowDirection? ?? FlowDirection.LeftToRight;

            Settings.Instance.PropertyChanged += Settings_PropertyChanged;

            if (Settings.Instance.ShowQuickLaunch)
            {
                QuickLaunchToolbar.Visibility = Visibility.Visible;
            }

            if (Settings.Instance.ShowDesktopButton)
            {
                ShowDesktopButtonTray.Visibility = Visibility.Visible;
            }

            UpdateStartButton();

            AutoHideElement = TaskbarContentControl;

            PropertyChanged += Taskbar_PropertyChanged;
            _dockMagnifier = new DockMagnifier(this);
            ContentRendered += (_, _) =>
            {
                UpdateAutoHideRevealStrip(true);
                ApplyDockPosition();
                PlayAppearAnimation();

                // Keep the remembered spot as the bar grows or shrinks with open apps.
                if (GetPillElement() is FrameworkElement pill)
                {
                    pill.SizeChanged += (_, args) =>
                    {
                        bool lengthChanged = Orientation == Orientation.Horizontal ? args.WidthChanged : args.HeightChanged;
                        if (lengthChanged && !IsDragging && !Settings.Instance.IsFreePosition && Settings.Instance.DockPosition >= 0)
                        {
                            Dispatcher.BeginInvoke(new Action(ApplyDockPosition), DispatcherPriority.Loaded);
                        }
                    };
                }
            };
            LostMouseCapture += (_, _) =>
            {
                // Only a real loss (e.g. another window took the mouse) ends the drag.
                if (_isManualDragging && !IsMouseCaptured && Mouse.LeftButton != MouseButtonState.Pressed)
                {
                    EndManualDrag();
                }
            };
            MouseLeftButtonUp += (_, _) =>
            {
                if (_isManualDragging)
                {
                    EndManualDrag();
                    return;
                }

                if (_freeDragStart != null)
                {
                    _freeDragStart = null;
                    ReleaseMouseCapture();
                }
            };

            _startMenuMonitor.StartMenuVisibilityChanged += StartMenuMonitor_StartMenuVisibilityChanged;
            _shellManager.TasksService.WindowActivated += TasksService_WindowActivated;
        }

        private void TasksService_WindowActivated(object sender, ManagedShell.WindowsTasks.WindowEventArgs e)
        {
            // If full-screen is suppressed, and a full-screen window is activated, it's time to un-suppress.

            if (!_fullScreenSuppressed)
            {
                return;
            }

            _fullScreenSuppressed = false;

            if (!HasFullScreenApp())
            {
                return;
            }

            for (int i = 0; i < _fullScreenHelper.FullScreenApps.Count; i++)
            {
                if (_fullScreenHelper.FullScreenApps[i].hWnd == e.Window.Handle)
                {
                    OnFullScreenEnter(_fullScreenHelper.FullScreenApps[i]);
                    return;
                }
            }
        }

        private void StartMenuMonitor_StartMenuVisibilityChanged(object sender, StartMenuMonitor.StartMenuMonitorEventArgs e)
        {
            if (!HasFullScreenApp() || !e.Visible)
            {
                return;
            }

            _fullScreenSuppressed = true;
            OnFullScreenLeave();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.Theme))
            {
                bool newTransparency = AppBarMode == AppBarMode.AutoHide || (Application.Current.FindResource("AllowsTransparency") as bool? ?? false);

                if ((AllowsTransparency != newTransparency || AppBarMode != WindowManager.GetAppBarMode()) && Screen.Primary)
                {
                    // Transparency cannot be changed on an open window; reopening also applies the theme's app bar mode.
                    windowManager.ReopenTaskbars();
                    return;
                }

                SetBlur(AllowsBlur());
                PeekDuringAutoHide();
                RecalculateSize();
                UpdateMaximizedWindowTimer();
            }
            else if (e.PropertyName == nameof(Settings.ShowQuickLaunch))
            {
                if (Settings.Instance.ShowQuickLaunch)
                {
                    QuickLaunchToolbar.Visibility = Visibility.Visible;
                }
                else
                {
                    QuickLaunchToolbar.Visibility = Visibility.Collapsed;
                }
            }
            else if (e.PropertyName == nameof(Settings.Edge))
            {
                PeekDuringAutoHide();
                AppBarEdge = Settings.Instance.Edge;
                UpdatePosition();
            }
            else if (e.PropertyName == nameof(Settings.Language))
            {
                FlowDirection newFlowDirection = Application.Current.FindResource("flow_direction") as FlowDirection? ?? FlowDirection.LeftToRight;

                if (FlowDirection != newFlowDirection && Screen.Primary)
                {
                    // It is necessary to reopen the taskbars to refresh menu sizes.
                    windowManager.ReopenTaskbars();
                    return;
                }
            }
            else if (e.PropertyName == nameof(Settings.ShowDesktopButton))
            {
                if (Settings.Instance.ShowDesktopButton)
                {
                    ShowDesktopButtonTray.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowDesktopButtonTray.Visibility = Visibility.Collapsed;
                }
            }
            else if (e.PropertyName == nameof(Settings.TaskbarScale))
            {
                PeekDuringAutoHide();
                RecalculateSize();
                OnPropertyChanged(nameof(IsScaled));
            }
            else if (e.PropertyName == nameof(Settings.AutoHide))
            {
                // Re-evaluated once the mode has changed (below or after reopening).
                Dispatcher.BeginInvoke(new Action(() => UpdateAutoHideRevealStrip(true)), DispatcherPriority.Loaded);

                bool newTransparency = Settings.Instance.AutoHide || (Application.Current.FindResource("AllowsTransparency") as bool? ?? false);

                if (AllowsTransparency == newTransparency)
                {
                    AppBarMode = WindowManager.GetAppBarMode();
                }
                else if (Screen.Primary)
                {
                    // Auto hide requires transparency
                    // Transparency cannot be changed on an open window.
                    windowManager.ReopenTaskbars();
                }
            }
            else if (e.PropertyName == nameof(Settings.LockTaskbar))
            {
                OnPropertyChanged(nameof(IsLocked));
                PeekDuringAutoHide();
                RecalculateSize();
            }
            else if (e.PropertyName == nameof(Settings.RowCount))
            {
                PeekDuringAutoHide();
                RecalculateSize();
                OnPropertyChanged(nameof(Rows));
            }
            else if (e.PropertyName == nameof(Settings.TaskbarWidth))
            {
                PeekDuringAutoHide();
                RecalculateSize();
            }
            else if (e.PropertyName == nameof(Settings.ShowStartButtonMultiMon))
            {
                UpdateStartButton();
            }
            else if (e.PropertyName == nameof(Settings.AutoHideTransparent))
            {
                PeekDuringAutoHide();
            }
            else if (e.PropertyName == nameof(Settings.AllowBlurBehind))
            {
                SetBlur(AllowsBlur());
            }
            else if (e.PropertyName == nameof(Settings.AllowAlternateMaximizedAppearance))
            {
                UpdateMaximizedWindowTimer();
            }
        }

        private void UpdateMaximizedWindowTimer()
        {
            if (AlternatesAppearanceWhenMaximized())
            {
                _maximizedWindowTimer?.Start();
            }
            else
            {
                _maximizedWindowTimer?.Stop();
                HasMaximizedWindow = false;
            }
        }

        private bool AlternatesAppearanceWhenMaximized()
        {
            return Settings.Instance.AllowAlternateMaximizedAppearance &&
                   Application.Current.TryFindResource("AlternatesAppearanceWhenMaximized") is bool b && b;
        }

        public bool ShowsTask(ApplicationWindow window)
        {
            if (!window.ShowInTaskbar)
            {
                return false;
            }

            if (!Settings.Instance.ShowMultiMon || Settings.Instance.MultiMonMode == MultiMonOption.AllTaskbars)
            {
                return true;
            }

            if (Settings.Instance.MultiMonMode == MultiMonOption.SameAsWindowAndPrimary && Screen.Primary)
            {
                return true;
            }

            IntPtr hMonitor = window.HMonitor;
            if (Screen.Primary && !windowManager.IsValidHMonitor(hMonitor))
            {
                return true;
            }

            return hMonitor == Screen.HMonitor;
        }

        private bool IsAnyWindowMaximizedOnThisScreen()
        {
            foreach (ApplicationWindow window in _shellManager.Tasks.GroupedWindows)
            {
                if (window.ShowStyle == NativeMethods.WindowShowStyle.ShowMaximized && ShowsTask(window))
                {
                    return true;
                }
            }

            return false;
        }

        private void MaximizedWindowTimer_Tick(object sender, EventArgs e)
        {
            HasMaximizedWindow = IsAnyWindowMaximizedOnThisScreen();
        }

        #region Free placement
        private Point? _freeDragStart;
        private DockMagnifier _dockMagnifier;

        private bool ThemeAllowsDrag =>
            Application.Current.TryFindResource("AllowDragWhenLocked") is bool allowDrag && allowDrag;

        // Free placement is only possible when the theme floats over windows (no reserved space).
        private bool CanFreeFloat => AppBarMode == AppBarMode.None && ThemeAllowsDrag && Screen.Primary;

        private bool IsFreeFloating => CanFreeFloat && Settings.Instance.IsFreePosition;

        public override bool UpdatePosition()
        {
            if (IsFreeFloating)
            {
                if (IsDragging)
                {
                    // AppBarWindow calls this whenever the window moves; while dragging, the
                    // cursor owns the position, so don't snap back to the saved spot.
                    return true;
                }

                PositionFree();
                return true;
            }

            if (SizeToContent != SizeToContent.Manual)
            {
                SizeToContent = SizeToContent.Manual;
            }

            bool result;
            if (CanFreeFloat)
            {
                // Floating themes don't reserve screen space, and the app bar logic doesn't
                // reliably resize the window then (it can keep the small size left over from free
                // placement, showing a small bar in the corner). Place the edge strip ourselves.
                SetDockedRect();
                result = true;
            }
            else
            {
                result = base.UpdatePosition();
            }

            // Apply right away so the first frame already shows the bar at its spot (no flash of
            // the centered layout), then again once layout settles in case sizes were stale.
            ApplyDockPosition();
            Dispatcher.BeginInvoke(new Action(ApplyDockPosition), DispatcherPriority.Loaded);
            return result;
        }

        // The window as a full-length strip along the docked edge (in DIPs).
        private void SetDockedRect()
        {
            double scale = ScreenScale;
            double screenLeft = Screen.Bounds.Left / scale;
            double screenTop = Screen.Bounds.Top / scale;
            double screenWidth = Screen.Bounds.Width / scale;
            double screenHeight = Screen.Bounds.Height / scale;

            if (Orientation == Orientation.Horizontal)
            {
                Width = screenWidth;
                Height = DesiredHeight;
                Left = screenLeft;
                Top = AppBarEdge == AppBarEdge.Top ? screenTop : screenTop + screenHeight - DesiredHeight;
            }
            else
            {
                Width = DesiredWidth;
                Height = screenHeight;
                Top = screenTop;
                Left = AppBarEdge == AppBarEdge.Left ? screenLeft : screenLeft + screenWidth - DesiredWidth;
            }
        }

        // Places the bar body at the remembered spot along the docked edge (instead of centered).
        // Local values on the template's PillHost override the theme's per-edge setters.
        private void ApplyDockPosition()
        {
            FrameworkElement pill = GetPillElement();
            if (pill == null)
            {
                return;
            }

            pill.ClearValue(FrameworkElement.MarginProperty);
            pill.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
            pill.ClearValue(FrameworkElement.VerticalAlignmentProperty);

            double along = Settings.Instance.DockPosition;
            if (!CanFreeFloat || Settings.Instance.IsFreePosition || along < 0)
            {
                return;
            }

            pill.UpdateLayout();
            Thickness themeMargin = pill.Margin;

            if (Orientation == Orientation.Horizontal)
            {
                double length = ActualWidth;
                double size = pill.ActualWidth > 0 ? pill.ActualWidth : pill.DesiredSize.Width;
                double start = along * length - size / 2;
                start = Math.Max(themeMargin.Left, Math.Min(start, length - size - themeMargin.Right));
                pill.HorizontalAlignment = HorizontalAlignment.Left;
                pill.Margin = new Thickness(start, themeMargin.Top, 0, themeMargin.Bottom);
            }
            else
            {
                double length = ActualHeight;
                double size = pill.ActualHeight > 0 ? pill.ActualHeight : pill.DesiredSize.Height;
                double start = along * length - size / 2;
                start = Math.Max(themeMargin.Top, Math.Min(start, length - size - themeMargin.Bottom));
                pill.VerticalAlignment = VerticalAlignment.Top;
                pill.Margin = new Thickness(themeMargin.Left, start, themeMargin.Right, 0);
            }
        }

        private double ScreenScale => DpiScale > 0 ? DpiScale : 1;

        // Places the window so its center sits at the saved fraction of the screen, sized to its content.
        private void PositionFree()
        {
            if (SizeToContent != SizeToContent.Width)
            {
                SizeToContent = SizeToContent.Width;
            }

            Height = DesiredHeight;

            double scale = ScreenScale;
            double screenLeft = Screen.Bounds.Left / scale;
            double screenTop = Screen.Bounds.Top / scale;
            double screenWidth = Screen.Bounds.Width / scale;
            double screenHeight = Screen.Bounds.Height / scale;
            // SizeToContent resizes the window asynchronously, so ActualWidth can still be the old
            // (docked, full-screen) width here. Position using the content's measured width instead.
            double width = GetContentWidth();
            double height = Height;

            double left = screenLeft + Settings.Instance.FreePositionX * screenWidth - width / 2;
            double top = screenTop + Settings.Instance.FreePositionY * screenHeight - height / 2;

            // The bar may hang past the screen edges, but keep enough of it visible to grab it again.
            const double minVisible = 120;
            Left = Math.Max(screenLeft + minVisible - width, Math.Min(left, screenLeft + screenWidth - minVisible));
            Top = Math.Max(screenTop, Math.Min(top, screenTop + screenHeight - height));
        }

        private void SaveFreePositionFromWindow()
        {
            double scale = ScreenScale;
            double centerX = (Left + ActualWidth / 2) * scale;
            double centerY = (Top + ActualHeight / 2) * scale;

            Settings.Instance.FreePositionX = Math.Clamp((centerX - Screen.Bounds.Left) / Screen.Bounds.Width, 0, 1);
            Settings.Instance.FreePositionY = Math.Clamp((centerY - Screen.Bounds.Top) / Screen.Bounds.Height, 0, 1);
        }

        // The point of the window (in DIPs) that stays under the cursor while dragging: the brand label.
        private Point _dragHandle;
        private bool _isManualDragging;

        private void BeginFreeDrag()
        {
            // Set first: it stops the size-changed handler from re-centering the bar mid-setup.
            IsDragging = true;

            if (!Settings.Instance.IsFreePosition)
            {
                Settings.Instance.IsFreePosition = true;

                if (Orientation == Orientation.Vertical)
                {
                    Settings.Instance.Edge = AppBarEdge.Bottom; // switches to the horizontal layout
                }
            }

            // Drop the docked placement (bar body offset along the edge) before measuring.
            ApplyDockPosition();

            // Use a fixed size while dragging, so nothing resizes asynchronously under the pointer.
            SizeToContent = SizeToContent.Manual;
            UpdateLayout();
            Width = GetContentWidth();
            Height = DesiredHeight;
            UpdateLayout();

            // The bar is always held by its brand name (Sartt), wherever it was grabbed.
            _dragHandle = GetDragHandleCenter(0.1);

            IsDragging = true;
            _isManualDragging = true;
            MoveWithCursor();

            if (!IsMouseCaptured)
            {
                CaptureMouse();
            }

            // Follow the cursor on a timer too, so fast moves never leave the bar behind,
            // whether or not mouse events keep arriving.
            _dragTimer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            _dragTimer.Tick -= DragTimer_Tick;
            _dragTimer.Tick += DragTimer_Tick;
            _dragTimer.Start();
        }

        private DispatcherTimer _dragTimer;

        private void DragTimer_Tick(object sender, EventArgs e)
        {
            const int VK_LBUTTON = 0x01;
            if (NativeMethods.GetAsyncKeyState(VK_LBUTTON) < 0)
            {
                MoveWithCursor();
            }
            else
            {
                EndManualDrag();
            }
        }

        private void MoveWithCursor()
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            double scale = ScreenScale;
            Left = cursor.X / scale - _dragHandle.X;
            Top = cursor.Y / scale - _dragHandle.Y;
        }

        private void EndManualDrag()
        {
            if (!_isManualDragging)
            {
                return;
            }

            _isManualDragging = false;
            _dragTimer?.Stop();

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            // IsDragging stays true until EndFreeDrag has decided where the bar goes: while it's
            // true, reposition requests are ignored, so nothing can move the bar for a frame in
            // between (e.g. to a saved spot clamped inside the screen).
            EndFreeDrag();
        }

        // Width the window will have once sized to its content.
        private double GetContentWidth()
        {
            TaskbarContentControl.Measure(new Size(double.PositiveInfinity, DesiredHeight));
            double width = TaskbarContentControl.DesiredSize.Width;
            return width > 0 ? width : ActualWidth;
        }

        // Center of the drag handle (the brand label), relative to the window as it will be once
        // sized to its content: measured from the bar body, which then starts at its left margin.
        private Point GetDragHandleCenter(double fallbackFraction)
        {
            FrameworkElement pill = GetPillElement();
            if (pill == null || pill.ActualWidth <= 0)
            {
                return new Point(GetContentWidth() / 2, DesiredHeight / 2);
            }

            Point inPill = BrandLabel.IsVisible && BrandLabel.ActualWidth > 0
                ? BrandLabel.TranslatePoint(new Point(BrandLabel.ActualWidth / 2, BrandLabel.ActualHeight / 2), pill)
                : new Point(fallbackFraction * pill.ActualWidth, pill.ActualHeight / 2);

            return new Point(pill.Margin.Left + inPill.X, pill.Margin.Top + inPill.Y);
        }

        // Themes opt in with "AnimateAppear": the bar body grows in and fades in when the taskbar
        // opens and when it lands on a new edge.
        public void PlayAppearAnimation()
        {
            if (!(Application.Current.TryFindResource("AnimateAppear") is bool animate && animate))
            {
                return;
            }

            FrameworkElement pill = GetPillElement();
            if (pill == null)
            {
                return;
            }

            if (pill.RenderTransform is not System.Windows.Media.ScaleTransform scaleTransform || scaleTransform.IsFrozen)
            {
                scaleTransform = new System.Windows.Media.ScaleTransform(1, 1);
                pill.RenderTransform = scaleTransform;
            }

            pill.RenderTransformOrigin = new Point(0.5, 0.5);

            var duration = new Duration(TimeSpan.FromMilliseconds(380));
            var ease = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.45 };

            scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.82, 1, duration) { EasingFunction = ease });
            scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.82, 1, duration) { EasingFunction = ease });
            pill.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))));
        }

        // After docking (especially when switching between horizontal and vertical), the task
        // buttons take a few layout passes to reach their final size. Keep the body hidden until
        // its size stops changing, place it, then reveal it with the landing animation; revealing
        // earlier shows it moving as it settles.
        private void RevealWhenSettled(FrameworkElement pill)
        {
            if (pill == null)
            {
                PlayAppearAnimation();
                return;
            }

            Size lastSize = Size.Empty;
            int stableTicks = 0;
            int totalTicks = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                UpdateLayout();
                ApplyDockPosition();
                UpdateLayout();

                Size size = new Size(Math.Round(pill.ActualWidth), Math.Round(pill.ActualHeight));
                stableTicks = size == lastSize && size.Width > 0 ? stableTicks + 1 : 0;
                lastSize = size;
                totalTicks++;

                if (stableTicks >= 2 || totalTicks >= 16)
                {
                    timer.Stop();
                    pill.Visibility = Visibility.Visible;
                    PlayAppearAnimation();
                }
            };
            timer.Start();
        }

        private FrameworkElement GetPillElement()
        {
            return TaskbarContentControl.Template?.FindName("PillHost", TaskbarContentControl) as FrameworkElement;
        }

        private void EndFreeDrag()
        {
            // Dropped against an edge: dock there. Anywhere else: stay where it was dropped.
            var cursor = System.Windows.Forms.Cursor.Position;
            var bounds = Screen.Bounds;
            int snap = (int)(40 * ScreenScale);
            AppBarEdge? edge = null;

            // The shape only changes on drop, never while the bar is being held and moved.
            if (cursor.Y >= bounds.Bottom - snap) edge = AppBarEdge.Bottom;
            else if (cursor.Y <= bounds.Top + snap) edge = AppBarEdge.Top;
            else if (cursor.X <= bounds.Left + snap) edge = AppBarEdge.Left;
            else if (cursor.X >= bounds.Right - snap) edge = AppBarEdge.Right;

            if (edge is AppBarEdge newEdge)
            {
                // Remember where along the edge it was dropped, so it doesn't jump to the center.
                var bounds2 = Screen.Bounds;
                bool horizontalEdge = newEdge == AppBarEdge.Top || newEdge == AppBarEdge.Bottom;
                double scale = ScreenScale;
                double centerX = (Left + ActualWidth / 2) * scale;
                Settings.Instance.DockPosition = horizontalEdge
                    ? Math.Clamp((centerX - bounds2.Left) / bounds2.Width, 0, 1)
                    : Math.Clamp((double)(cursor.Y - bounds2.Top) / bounds2.Height, 0, 1);

                // Keep the body hidden (Visibility, which no opacity animation can override) while
                // the window switches from floating to docked, and reveal it only once it sits at
                // its final spot; otherwise a frame of the centered layout flashes on screen.
                FrameworkElement landingPill = GetPillElement();
                if (landingPill != null)
                {
                    landingPill.Visibility = Visibility.Hidden;
                }

                Settings.Instance.IsFreePosition = false;
                if (Settings.Instance.Edge != newEdge)
                {
                    Settings.Instance.Edge = newEdge; // repositions via the Edge setting handler
                }
                else
                {
                    UpdatePosition();
                }

                IsDragging = false;
                RevealWhenSettled(landingPill);
                return;
            }

            SaveFreePositionFromWindow();
            IsDragging = false;
            PositionFree(); // pull it back on screen if it was dropped partly outside
            SaveFreePositionFromWindow();
        }
        #endregion

        #region AppBarWindow overrides
        protected override void OnSourceInitialized(object sender, EventArgs e)
        {
            base.OnSourceInitialized(sender, e);

            SetLayoutRounding();
            SetBlur(AllowsBlur());
            UpdateTrayPosition();
            ReleaseWorkAreaIfFloating();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref NativeMethods.Rect pvParam, uint fWinIni);

        private void ReleaseWorkAreaIfFloating()
        {
            if (AppBarMode != AppBarMode.None)
            {
                return;
            }

            // When floating over windows, maximized apps should use the full screen. Clear any
            // work area reservation left behind (e.g. by a previous instance that didn't exit cleanly).
            var bounds = Screen.Bounds;
            var rect = new NativeMethods.Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            const uint SPI_SETWORKAREA = 0x002F;
            const uint SPIF_SENDCHANGE = 0x2;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref rect, SPIF_SENDCHANGE);
        }
            
        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            base.WndProc(hwnd, msg, wParam, lParam, ref handled);

            if ((msg == (int)NativeMethods.WM.SYSCOLORCHANGE || 
                    msg == (int)NativeMethods.WM.SETTINGCHANGE) && 
                Settings.Instance.Theme.StartsWith(DictionaryManager.THEME_DEFAULT))
            {
                handled = true;

                // If the color scheme changes, re-apply the current theme to get updated colors.
                _dictionaryManager.SetThemeFromSettings();
            }
            else if (msg == (int)NativeMethods.WM.SETTINGCHANGE && wParam == (IntPtr)NativeMethods.SPI.SETWORKAREA && Settings.Instance.ShowMultiMon)
            {
                windowManager.NotifyWorkAreaChange();
            }

            return IntPtr.Zero;
        }

        protected override void CustomClosing()
        {
            if (_maximizedWindowTimer != null)
            {
                _maximizedWindowTimer.Stop();
                _maximizedWindowTimer.Tick -= MaximizedWindowTimer_Tick;
                _maximizedWindowTimer = null;
            }

            if (AllowClose)
            {
                QuickLaunchToolbar.Visibility = Visibility.Collapsed;

                Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
                _startMenuMonitor.StartMenuVisibilityChanged -= StartMenuMonitor_StartMenuVisibilityChanged;
                _shellManager.TasksService.WindowActivated -= TasksService_WindowActivated;
            }
        }

        protected override void SetScreenProperties(ScreenSetupReason reason)
        {
            if (reason == ScreenSetupReason.DpiChange)
            {
                // DPI change is per-monitor, update ourselves
                UpdatePosition();
                SetLayoutRounding();
                return;
            }

            if (Settings.Instance.ShowMultiMon)
            {
                // Re-create RetroBar windows based on new screen setup
                windowManager.NotifyDisplayChange(reason);
            }
            else
            {
                // Update window as necessary
                base.SetScreenProperties(reason);
            }
        }

        protected override bool ShouldAllowAutoHide()
        {
            return (!_startMenuOpen || !Screen.Primary) && _openMenus < 1 && base.ShouldAllowAutoHide();
        }

        // In auto-hide mode, keeps a nearly invisible strip along the docked edge so the pointer is
        // always "on the taskbar" there: thin (2px) while hidden, to bring it back; and, while shown,
        // also covering the transparent gap between the bar body and the edge, so it doesn't hide
        // again while the pointer rests on the edge.
        private void UpdateAutoHideRevealStrip(bool expanded)
        {
            if (AppBarMode != AppBarMode.AutoHide)
            {
                AutoHideRevealStrip.Visibility = Visibility.Collapsed;
                return;
            }

            double thickness = 2;
            if (expanded && GetPillElement() is FrameworkElement pill)
            {
                Thickness margin = pill.Margin;
                thickness += AppBarEdge switch
                {
                    AppBarEdge.Top => margin.Top,
                    AppBarEdge.Left => margin.Left,
                    AppBarEdge.Right => margin.Right,
                    _ => margin.Bottom
                };
            }
            AutoHideRevealStrip.ClearValue(WidthProperty);
            AutoHideRevealStrip.ClearValue(HeightProperty);
            AutoHideRevealStrip.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutoHideRevealStrip.VerticalAlignment = VerticalAlignment.Stretch;

            switch (AppBarEdge)
            {
                case AppBarEdge.Top:
                    AutoHideRevealStrip.VerticalAlignment = VerticalAlignment.Top;
                    AutoHideRevealStrip.Height = thickness;
                    break;
                case AppBarEdge.Left:
                    AutoHideRevealStrip.HorizontalAlignment = HorizontalAlignment.Left;
                    AutoHideRevealStrip.Width = thickness;
                    break;
                case AppBarEdge.Right:
                    AutoHideRevealStrip.HorizontalAlignment = HorizontalAlignment.Right;
                    AutoHideRevealStrip.Width = thickness;
                    break;
                default:
                    AutoHideRevealStrip.VerticalAlignment = VerticalAlignment.Bottom;
                    AutoHideRevealStrip.Height = thickness;
                    break;
            }

            AutoHideRevealStrip.Visibility = Visibility.Visible;
        }

        protected override void OnAutoHideAnimationBegin(bool isHiding)
        {
            base.OnAutoHideAnimationBegin(isHiding);

            if (!isHiding)
            {
                // Grow before the body slides in, so the pointer never ends up on a see-through gap.
                UpdateAutoHideRevealStrip(true);
            }

            // Prevent focus indicators and tooltips while hidden
            ResetControlFocus();

            if (!isHiding && Opacity < 1)
            {
                Opacity = 1;
                OnPropertyChanged(nameof(Opacity));
            }
            if (!isHiding)
            {
                StartButton?.UpdateFloatingStartVisible(true);
            }
        }

        protected override void OnAutoHideAnimationComplete(bool isHiding)
        {
            base.OnAutoHideAnimationComplete(isHiding);

            if (isHiding)
            {
                UpdateAutoHideRevealStrip(false);
            }

            if (isHiding && Settings.Instance.AutoHideTransparent && AllowsTransparency && AllowAutoHide)
            {
                Opacity = 0.01;
                OnPropertyChanged(nameof(Opacity));
            }
            if (isHiding && AllowAutoHide)
            {
                StartButton?.UpdateFloatingStartVisible(false);
            }
        }
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        // When floating over windows, an ordinary maximized window covers the whole screen and
        // looks "full screen". Only real full-screen apps (no title bar, e.g. games, videos)
        // should push the taskbar behind them.
        private bool IsMaximizedNotFullScreen(FullScreenApp app)
        {
            const int GWL_STYLE = -16;
            const int WS_CAPTION = 0x00C00000;
            return AppBarMode == AppBarMode.None &&
                   IsZoomed(app.hWnd) &&
                   (GetWindowLong(app.hWnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        }

        protected override void OnFullScreenEnter(FullScreenApp app)
        {
            if (IsMaximizedNotFullScreen(app))
            {
                return;
            }

            base.OnFullScreenEnter(app);
            StartButton?.UpdateFloatingStartTopmost(false);
        }

        protected override void OnFullScreenLeave()
        {
            base.OnFullScreenLeave();
            StartButton?.UpdateFloatingStartTopmost(true);
        }
        #endregion

        #region Taskbar events
        private void Taskbar_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DpiScale))
            {
                OnPropertyChanged(nameof(IsScaled));
            }
        }

        private void Taskbar_OnLocationChanged(object sender, EventArgs e)
        {
            UpdateTrayPosition();
            StartButton?.UpdateFloatingStartCoordinates();
        }

        private void Taskbar_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsFreeFloating && !IsDragging && e.WidthChanged)
            {
                // Grow/shrink around the saved center as apps open and close.
                PositionFree();
            }

            UpdateTrayPosition();
            StartButton?.UpdateFloatingStartCoordinates();
        }

        private void Taskbar_Deactivated(object sender, EventArgs e)
        {
            if (AppBarMode != AppBarMode.AutoHide)
            {
                // Prevent focus indicators and tooltips while not the active window
                // When auto-hide is enabled, this is performed by auto-hide events instead
                ResetControlFocus();
            }
        }
        #endregion

        #region Context menu
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            Visibility positionVisibility = CanFreeFloat ? Visibility.Visible : Visibility.Collapsed;
            PositionMenuItem.Visibility = positionVisibility;
            PositionMenuSeparator.Visibility = positionVisibility;

            if (_updater.IsUpdateAvailable)
            {
                UpdateAvailableMenuItem.Visibility = Visibility.Visible;
            }

            if (NativeMethods.GetAsyncKeyState((int)System.Windows.Forms.Keys.ShiftKey) < 0 && Settings.Instance.ShowExitMenuItem)
            {
                RestartMenuItem.Visibility = Visibility.Visible;
            }
            else
            {
                RestartMenuItem.Visibility = Visibility.Collapsed;
            }
        }

        private void DockMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string target)
            {
                return;
            }

            AppBarEdge edge = target switch
            {
                "Top" => AppBarEdge.Top,
                "Left" => AppBarEdge.Left,
                "Right" => AppBarEdge.Right,
                "Bottom" => AppBarEdge.Bottom,
                // Center on the current edge (bottom when it's floating freely).
                _ => Settings.Instance.IsFreePosition ? AppBarEdge.Bottom : Settings.Instance.Edge,
            };

            DockTo(edge, -1);
        }

        // Docks to an edge at a spot along it (negative = centered), hidden until it has settled
        // there and then revealed with the landing animation.
        private void DockTo(AppBarEdge edge, double along)
        {
            FrameworkElement pill = GetPillElement();
            if (pill != null)
            {
                pill.Visibility = Visibility.Hidden;
            }

            Settings.Instance.DockPosition = along;
            Settings.Instance.IsFreePosition = false;

            if (Settings.Instance.Edge != edge)
            {
                Settings.Instance.Edge = edge; // repositions via the Edge setting handler
            }
            else
            {
                UpdatePosition();
            }

            RevealWhenSettled(pill);
        }

        private void SetTimeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ShellHelper.StartProcess("timedate.cpl");
        }

        private void CustomizeNotificationsMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            PropertiesWindow propWindow = PropertiesWindow.Open(_shellManager.NotificationArea, _dictionaryManager, Screen, DpiScale, Orientation == Orientation.Horizontal ? DesiredHeight : DesiredWidth);
            propWindow.OpenCustomizeNotifications();
        }

        private void TaskManagerMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ShellHelper.StartTaskManager();
        }

        private void UpdateAvailableMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _updater.DownloadUrl,
                UseShellExecute = true
            };

            Process.Start(psi);
        }

        private void PropertiesMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            PropertiesWindow.Open(_shellManager.NotificationArea, _dictionaryManager, Screen, DpiScale, Orientation == Orientation.Horizontal ? DesiredHeight : DesiredWidth);
        }

        private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ExitByUser();
        }

        private void RestartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).RestartApp();
        }
        #endregion

        private void RecalculateSize(bool performResize = true)
        {
            _unlockedMargin = Settings.Instance.TaskbarScale * (Application.Current.FindResource("TaskbarUnlockedSize") as double? ?? 0);
            DesiredRowHeight = Settings.Instance.TaskbarScale * (Application.Current.FindResource("TaskbarRowHeight") as double? ?? 0);
            double newWidth = (Settings.Instance.TaskbarScale * (Application.Current.FindResource("TaskbarWidth") as double? ?? 0)) + DesiredRowHeight * (Settings.Instance.TaskbarWidth - 1);
            double newHeight = (Settings.Instance.TaskbarScale * (Application.Current.FindResource("TaskbarHeight") as double? ?? 0)) + DesiredRowHeight * (Rows - 1);

            if (AppBarMode == AppBarMode.AutoHide || !Settings.Instance.LockTaskbar)
            {
                newHeight += _unlockedMargin;
                newWidth += _unlockedMargin;
            }

            bool heightChanged = newHeight != DesiredHeight;
            bool widthChanged = newWidth != DesiredWidth;

            DesiredHeight = newHeight;
            DesiredWidth = newWidth;

            if (!performResize)
            {
                return;
            }

            if ((Orientation == Orientation.Horizontal && heightChanged) || (Orientation == Orientation.Vertical && widthChanged))
            {
                UpdatePosition();
            }
        }

        private void ResetControlFocus()
        {
            FocusDummyButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
        }

        private void SetLayoutRounding()
        {
            // Layout rounding causes incorrect sizing on non-integer scales
            if (DpiScale % 1 != 0)
            {
                UseLayoutRounding = false;
            }
            else
            {
                UseLayoutRounding = true;
            }
        }

        public void SetStartMenuOpen(bool isOpen)
        {
            bool currentAutoHide = AllowAutoHide;
            _startMenuOpen = isOpen;

            if (AllowAutoHide != currentAutoHide)
            {
                OnPropertyChanged(nameof(AllowAutoHide));
            }
        }

        public void SetTrayHost()
        {
            _shellManager.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = (NativeMethods.ABEdge)AppBarEdge,
                rc = WindowRect
            });
        }

        public void AddOpenMenu()
        {
            bool currentAutoHide = AllowAutoHide;
            _openMenus++;

            if (AllowAutoHide != currentAutoHide)
            {
                OnPropertyChanged(nameof(AllowAutoHide));
            }
        }

        public void RemoveOpenMenu()
        {
            bool currentAutoHide = AllowAutoHide;
            _openMenus--;

            if (AllowAutoHide != currentAutoHide)
            {
                OnPropertyChanged(nameof(AllowAutoHide));
            }
        }

        private void UpdateTrayPosition()
        {
            if (Screen.Primary)
            {
                SetTrayHost();
            }
        }

        private void UpdateStartButton()
        {
            if (!Screen.Primary && !Settings.Instance.ShowStartButtonMultiMon)
            {
                StartButton.Visibility = Visibility.Collapsed;
                return;
            }

            StartButton.Visibility = Visibility.Visible;
        }

        private bool HasFullScreenApp()
        {
            bool hasFullScreenApp = false;

            foreach (var app in _fullScreenHelper.FullScreenApps)
            {
                if (app.screen.DeviceName == Screen.DeviceName || app.screen.IsVirtualScreen)
                {
                    hasFullScreenApp = true;
                    break;
                }
            }

            return hasFullScreenApp;
        }

        private bool AllowsBlur()
        {
            return Settings.Instance.AllowBlurBehind &&
                   (Application.Current.FindResource("AllowsTransparency") as bool? ?? false);
        }

        #region Unlocked taskbar drag hook
        private void Taskbar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (CanFreeFloat)
            {
                // Wait for real movement before undocking, so a plain click does nothing.
                // Capture so moves are still seen if the pointer leaves the thin bar quickly.
                _freeDragStart = e.GetPosition(this);
                CaptureMouse();
                return;
            }

            if (!IsLocked || ThemeAllowsDrag)
            {
                // Start low-level mouse hook to receive current drag position.
                // When locked, IsMouseInResizeArea is false, so this only moves between edges.
                // The hook should be stopped upon mouse up
                StartMouseDragHook();
            }
        }

        private AppBarEdge DragCoordsToScreenEdge(int x, int y)
        {
            // The areas of the screen which determine the dragged-to edge are divided in an X.
            // To determine the edge, split the screen into quadrants, and then split the quadrants diagonally, alternating.
            double relativeX = ((double)x - Screen.Bounds.Left) / Screen.Bounds.Width;
            double relativeY = ((double)y - Screen.Bounds.Top) / Screen.Bounds.Height;

            // We will use the relative coordinates to form quadrants
            // Determine the edge based on the quadrant

            if (relativeX < 0.5 && relativeY < 0.5)
            {
                // top-left quadrant
                if (relativeX >= relativeY)
                {
                    return AppBarEdge.Top;
                }
                else
                {
                    return AppBarEdge.Left;
                }
            }
            else if (relativeX >= 0.5 && relativeY < 0.5)
            {
                // top-right quadrant
                // adjust relativeX to the same base as relativeY
                relativeX -= 0.5;

                if (relativeX + relativeY < 0.5)
                {
                    return AppBarEdge.Top;
                }
                else
                {
                    return AppBarEdge.Right;
                }
            }
            else if (relativeX < 0.5 && relativeY >= 0.5)
            {
                // bottom-left quadrant
                // adjust relativeY to the same base as relativeX
                relativeY -= 0.5;

                if (relativeX + relativeY < 0.5)
                {
                    return AppBarEdge.Left;
                }
                else
                {
                    return AppBarEdge.Bottom;
                }
            }
            else
            {
                // bottom-right quadrant
                if (relativeX >= relativeY)
                {
                    return AppBarEdge.Right;
                }
                else
                {
                    return AppBarEdge.Bottom;
                }
            }
        }

        private void MouseDragHook_LowLevelMouseEvent(object sender, LowLevelMouseHook.LowLevelMouseEventArgs e)
        {
            switch (e.Message)
            {
                case NativeMethods.WM.MOUSEMOVE:
                    if (_mouseDragStart == null)
                    {
                        return;
                    }

                    if (_mouseDragResize)
                    {
                        Dispatcher.BeginInvoke(() => {
                            int mouseX = e.HookStruct.pt.X;
                            int mouseY = e.HookStruct.pt.Y;
                            // Calculate where the resize edge should be, in case the actual resize operation is lagging behind the mouse
                            double scaledRowHeight = DesiredRowHeight * DpiScale;
                            if (Orientation == Orientation.Horizontal)
                            {
                                double taskbarEdge = AppBarEdge == AppBarEdge.Top ? Screen.Bounds.Top + (DesiredHeight * DpiScale) : Screen.Bounds.Bottom - (DesiredHeight * DpiScale);
                                if ((AppBarEdge == AppBarEdge.Top && mouseY < taskbarEdge - SystemParameters.MinimumVerticalDragDistance ||
                                     AppBarEdge == AppBarEdge.Bottom && mouseY > taskbarEdge + SystemParameters.MinimumVerticalDragDistance) &&
                                     Settings.Instance.RowCount > 1)
                                {
                                    // If mouse is inside the taskbar and more than the minimum drag distance away, decrement size
                                    Settings.Instance.RowCount -= 1;
                                }
                                else if ((AppBarEdge == AppBarEdge.Top && mouseY >= taskbarEdge + scaledRowHeight ||
                                          AppBarEdge == AppBarEdge.Bottom && mouseY <= taskbarEdge - scaledRowHeight) &&
                                          Settings.Instance.RowCount < Settings.Instance.RowLimit)
                                {
                                    // If mouse is outside the taskbar and at least one row height away, increment size
                                    Settings.Instance.RowCount += 1;
                                }
                            }
                            else
                            {
                                double taskbarEdge = AppBarEdge == AppBarEdge.Left ? Screen.Bounds.Left + (DesiredWidth * DpiScale) : Screen.Bounds.Right - (DesiredWidth * DpiScale);
                                if ((AppBarEdge == AppBarEdge.Left && mouseX > taskbarEdge + scaledRowHeight ||
                                     AppBarEdge == AppBarEdge.Right && mouseX < taskbarEdge - scaledRowHeight) &&
                                    Settings.Instance.TaskbarWidth < Settings.Instance.TaskbarWidthLimit)
                                {
                                    Settings.Instance.TaskbarWidth += 1;
                                }
                                else if ((AppBarEdge == AppBarEdge.Left && mouseX < taskbarEdge - SystemParameters.MinimumHorizontalDragDistance ||
                                          AppBarEdge == AppBarEdge.Right && mouseX > taskbarEdge + SystemParameters.MinimumHorizontalDragDistance) &&
                                    Settings.Instance.TaskbarWidth > 1)
                                {
                                    Settings.Instance.TaskbarWidth -= 1;
                                }
                            }
                        });
                        return;
                    }

                    if (Math.Abs(e.HookStruct.pt.X - (double)(_mouseDragStart?.X)) <= SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(e.HookStruct.pt.Y - (double)(_mouseDragStart?.Y)) <= SystemParameters.MinimumVerticalDragDistance)
                    {
                        return;
                    }

                    AppBarEdge newEdge = DragCoordsToScreenEdge(e.HookStruct.pt.X, e.HookStruct.pt.Y);
                    if (newEdge != AppBarEdge)
                    {
                        Settings.Instance.Edge = newEdge;
                    }
                    break;
                case NativeMethods.WM.LBUTTONUP:
                case NativeMethods.WM.LBUTTONDOWN:
                case NativeMethods.WM.MBUTTONUP:
                case NativeMethods.WM.MBUTTONDOWN:
                case NativeMethods.WM.RBUTTONUP:
                case NativeMethods.WM.RBUTTONDOWN:
                case NativeMethods.WM.XBUTTONUP:
                case NativeMethods.WM.XBUTTONDOWN:
                    StopMouseDragHook();
                    break;
            }
        }

        private void StartMouseDragHook()
        {
            if (_mouseDragHook != null)
            {
                return;
            }

            _mouseDragHook = new LowLevelMouseHook();
            _mouseDragHook.LowLevelMouseEvent += MouseDragHook_LowLevelMouseEvent;
            _mouseDragHook.Initialize();
            _mouseDragStart = new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
            _mouseDragResize = IsMouseInResizeArea();

            ShellLogger.Debug($"Mouse drag hook started");
        }

        private void StopMouseDragHook()
        {
            _mouseDragHook.LowLevelMouseEvent -= MouseDragHook_LowLevelMouseEvent;
            _mouseDragHook.Dispose();
            _mouseDragHook = null;
            _mouseDragStart = null;
            _mouseDragResize = false;

            ShellLogger.Debug("Mouse drag hook removed");
        }

        private bool IsMouseInResizeArea()
        {
            if (IsLocked) return false;

            int resizeRegionSize = (int)((_unlockedMargin > 0 ? _unlockedMargin : SystemParameters.MinimumVerticalDragDistance * Settings.Instance.TaskbarScale) * DpiScale);
            int mouseX = System.Windows.Forms.Cursor.Position.X;
            int mouseY = System.Windows.Forms.Cursor.Position.Y;

            if (AppBarEdge == AppBarEdge.Bottom && mouseY <= (int)(Top * DpiScale) + resizeRegionSize)
            {
                return true;
            }
            else if (AppBarEdge == AppBarEdge.Top && mouseY >= (int)((Top + Height) * DpiScale) - resizeRegionSize)
            {
                return true;
            }
            else if (AppBarEdge == AppBarEdge.Left && mouseX >= (int)((Left + Width) * DpiScale) - resizeRegionSize)
            {
                return true;
            }
            else if (AppBarEdge == AppBarEdge.Right && mouseX <= (int)(Left * DpiScale) + resizeRegionSize)
            {
                return true;
            }

            return false;
        }

        private void Taskbar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isManualDragging)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    MoveWithCursor();
                }
                else
                {
                    EndManualDrag();
                }
                return;
            }

            if (_freeDragStart is Point start)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    _freeDragStart = null;
                    ReleaseMouseCapture();
                }
                else
                {
                    Vector moved = e.GetPosition(this) - start;
                    if (Math.Abs(moved.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(moved.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        // Keep the capture from the mouse press: releasing and re-capturing here
                        // raises a late LostMouseCapture that would cancel the drag right away.
                        _freeDragStart = null;
                        BeginFreeDrag();
                    }
                    return;
                }
            }

            // Show resize cursor for resizable taskbars
            if (IsMouseInResizeArea() || _mouseDragResize)
            {
                Cursor = Orientation == Orientation.Horizontal ? Cursors.SizeNS : Cursors.SizeWE;
            }
            else
            {
                Cursor = Cursors.Arrow;
            }
        }
        #endregion
    }
}
