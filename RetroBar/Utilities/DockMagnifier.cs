using ManagedShell.AppBar;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RetroBar.Utilities
{
    /// <summary>
    /// Dock-style magnification: the app icon under the pointer grows, and its neighbours grow
    /// less the further away they are. Enabled by themes through the "DockMagnification"
    /// resource (the maximum scale; 0 or 1 disables it).
    /// </summary>
    public class DockMagnifier
    {
        // Distance (in DIPs, from the pointer to an icon's center) at which icons stop growing.
        // Themes can override it with "DockMagnificationRadius"; smaller = neighbours grow less.
        private static double Radius =>
            Application.Current.TryFindResource("DockMagnificationRadius") is double radius && radius > 0 ? radius : 62;
        private static readonly Duration ResetDuration = new Duration(TimeSpan.FromMilliseconds(220));

        private readonly Taskbar _taskbar;
        private readonly HashSet<Image> _scaled = new HashSet<Image>();

        public DockMagnifier(Taskbar taskbar)
        {
            _taskbar = taskbar;
            _taskbar.PreviewMouseMove += Taskbar_PreviewMouseMove;
            _taskbar.MouseLeave += Taskbar_MouseLeave;
        }

        private static double MaxScale =>
            Application.Current.TryFindResource("DockMagnification") is double scale ? scale : 0;

        private void Taskbar_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            double maxScale = MaxScale;
            if (maxScale <= 1 || _taskbar.IsDragging)
            {
                ResetAll();
                return;
            }

            bool horizontal = _taskbar.Orientation == Orientation.Horizontal;
            Point mouse = e.GetPosition(_taskbar);
            Point origin = GetGrowOrigin();

            foreach (Image icon in FindAppIcons())
            {
                if (icon.ActualWidth <= 0)
                {
                    continue;
                }

                Point center = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), _taskbar);
                double distance = horizontal ? Math.Abs(center.X - mouse.X) : Math.Abs(center.Y - mouse.Y);
                double t = Math.Max(0, 1 - distance / Radius);
                t = t * t * (3 - 2 * t); // smoothstep, so the falloff feels soft
                double scale = 1 + (maxScale - 1) * t;

                ScaleTransform transform = EnsureTransform(icon, origin);
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleX = scale;
                transform.ScaleY = scale;

                if (scale > 1)
                {
                    _scaled.Add(icon);
                }
            }
        }

        private void Taskbar_MouseLeave(object sender, MouseEventArgs e)
        {
            ResetAll();
        }

        private void ResetAll()
        {
            if (_scaled.Count == 0)
            {
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (Image icon in _scaled)
            {
                if (icon.RenderTransform is ScaleTransform transform)
                {
                    transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, ResetDuration) { EasingFunction = ease });
                    transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, ResetDuration) { EasingFunction = ease });
                }
            }

            _scaled.Clear();
        }

        // Icons grow away from the screen edge the bar sits on.
        private Point GetGrowOrigin()
        {
            return _taskbar.AppBarEdge switch
            {
                AppBarEdge.Top => new Point(0.5, 0),
                AppBarEdge.Left => new Point(0, 0.5),
                AppBarEdge.Right => new Point(1, 0.5),
                _ => new Point(0.5, 1),
            };
        }

        private static ScaleTransform EnsureTransform(Image icon, Point origin)
        {
            icon.RenderTransformOrigin = origin;

            if (icon.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
            {
                return existing;
            }

            var transform = new ScaleTransform(1, 1);
            icon.RenderTransform = transform;
            return transform;
        }

        private List<Image> _iconCache;
        private DateTime _iconCacheTime;

        // App icons are the images styled as task or pinned (toolbar) icons. Walking the visual
        // tree on every mouse move is wasteful, so the list is reused for a short while and
        // whenever all its icons are still on screen.
        private IEnumerable<Image> FindAppIcons()
        {
            bool fresh = _iconCache != null &&
                         (DateTime.Now - _iconCacheTime).TotalMilliseconds < 750 &&
                         _iconCache.TrueForAll(icon => icon.IsLoaded);
            if (fresh)
            {
                return _iconCache;
            }

            object taskIconStyle = _taskbar.TryFindResource("TaskIcon");
            object toolbarIconStyle = _taskbar.TryFindResource("ToolbarIcon");
            var results = new List<Image>();
            Collect(_taskbar, taskIconStyle, toolbarIconStyle, results);
            _iconCache = results;
            _iconCacheTime = DateTime.Now;
            return results;
        }

        private static void Collect(DependencyObject parent, object taskIconStyle, object toolbarIconStyle, List<Image> results)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is Image image && image.Style != null &&
                    (ReferenceEquals(image.Style, taskIconStyle) || ReferenceEquals(image.Style, toolbarIconStyle)))
                {
                    results.Add(image);
                    continue;
                }

                Collect(child, taskIconStyle, toolbarIconStyle, results);
            }
        }
    }
}
