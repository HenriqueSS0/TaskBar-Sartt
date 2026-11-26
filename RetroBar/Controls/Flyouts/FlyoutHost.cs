using ManagedShell.AppBar;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Controls.Flyouts
{
    /// <summary>
    /// Opens themed flyouts (volume, network, calendar) right above the element that was clicked,
    /// instead of Windows' own flyouts that open in the screen corner. Enabled by themes through the
    /// "SarttFlyouts" resource.
    /// </summary>
    public static class FlyoutHost
    {
        private static Popup _popup;
        private static FrameworkElement _anchor;
        private static DateTime _closedAt = DateTime.MinValue;

        public static bool IsEnabled =>
            Application.Current.TryFindResource("SarttFlyouts") is bool enabled && enabled;

        /// <summary>
        /// Toggles the flyout for this anchor. Returns false when themed flyouts are disabled, so
        /// callers fall back to the default Windows behavior.
        /// </summary>
        public static bool Toggle(FrameworkElement anchor, Func<FrameworkElement> createContent)
        {
            if (!IsEnabled || anchor == null)
            {
                return false;
            }

            // A click on the same anchor that just closed the popup (StaysOpen=False) shouldn't reopen it.
            bool sameAnchorJustClosed = ReferenceEquals(anchor, _anchor) && (DateTime.Now - _closedAt).TotalMilliseconds < 300;
            if (_popup != null && _popup.IsOpen)
            {
                bool same = ReferenceEquals(anchor, _anchor);
                _popup.IsOpen = false;
                if (same)
                {
                    return true;
                }
            }
            else if (sameAnchorJustClosed)
            {
                return true;
            }

            Show(anchor, createContent());
            return true;
        }

        public static void Close()
        {
            if (_popup != null)
            {
                _popup.IsOpen = false;
            }
        }

        private static void Show(FrameworkElement anchor, FrameworkElement content)
        {
            _anchor = anchor;

            var panel = new Border
            {
                Child = content,
                Margin = new Thickness(14),
                RenderTransformOrigin = new Point(0.5, 1)
            };
            panel.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutPanel");

            _popup = new Popup
            {
                Child = panel,
                PlacementTarget = anchor,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None
            };

            var edge = (Window.GetWindow(anchor) as AppBarWindow)?.AppBarEdge ?? AppBarEdge.Bottom;
            _popup.Placement = edge switch
            {
                AppBarEdge.Top => PlacementMode.Bottom,
                AppBarEdge.Left => PlacementMode.Right,
                AppBarEdge.Right => PlacementMode.Left,
                _ => PlacementMode.Top
            };

            _popup.Opened += (_, _) =>
            {
                // Center over the anchor along the bar, with a small gap from it.
                bool horizontalBar = edge == AppBarEdge.Top || edge == AppBarEdge.Bottom;
                if (horizontalBar)
                {
                    _popup.HorizontalOffset = (anchor.ActualWidth - panel.ActualWidth - panel.Margin.Left - panel.Margin.Right) / 2;
                    _popup.VerticalOffset = edge == AppBarEdge.Top ? 4 : -4;
                }
                else
                {
                    _popup.VerticalOffset = (anchor.ActualHeight - panel.ActualHeight - panel.Margin.Top - panel.Margin.Bottom) / 2;
                    _popup.HorizontalOffset = edge == AppBarEdge.Left ? 4 : -4;
                }

                PlayOpenAnimation(panel, edge);
            };
            // While the flyout is open, an auto-hiding taskbar must stay visible: the pointer is
            // over the flyout (another window), which would otherwise count as leaving the bar.
            var taskbar = Window.GetWindow(anchor) as Taskbar;
            taskbar?.AddOpenMenu();

            _popup.Closed += (_, _) =>
            {
                _closedAt = DateTime.Now;
                (content as IDisposable)?.Dispose();
                taskbar?.RemoveOpenMenu();
            };

            _popup.IsOpen = true;
        }

        // Rises (or slides in from the bar) while fading in, with a little overshoot.
        private static void PlayOpenAnimation(FrameworkElement panel, AppBarEdge edge)
        {
            var translate = new TranslateTransform();
            var scale = new ScaleTransform(0.96, 0.96);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            panel.RenderTransform = group;

            var duration = new Duration(TimeSpan.FromMilliseconds(320));
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            const double distance = 14;

            DependencyProperty axis = edge == AppBarEdge.Left || edge == AppBarEdge.Right
                ? TranslateTransform.XProperty
                : TranslateTransform.YProperty;
            double from = edge switch
            {
                AppBarEdge.Top => -distance,
                AppBarEdge.Left => -distance,
                AppBarEdge.Right => distance,
                _ => distance
            };

            translate.BeginAnimation(axis, new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))));
        }
    }
}
