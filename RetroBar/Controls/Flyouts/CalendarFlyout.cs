using ManagedShell.Common.Helpers;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RetroBar.Controls.Flyouts
{
    /// <summary>
    /// Clock flyout: live time, the full date and a month calendar that can be paged.
    /// </summary>
    public class CalendarFlyout : StackPanel, IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly TextBlock _time = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _date = new TextBlock();
        private readonly TextBlock _monthTitle = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private readonly UniformGrid _days = new UniformGrid { Columns = 7 };
        private readonly DispatcherTimer _clockTimer;
        private DateTime _month;

        public CalendarFlyout()
        {
            Width = 280;
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

            _time.SetResourceReference(TextBlock.FontFamilyProperty, "BrandFont");
            _time.SetResourceReference(TextBlock.ForegroundProperty, "FloatForeground");
            Children.Add(_time);

            _date.SetResourceReference(StyleProperty, "FlyoutMuted");
            _date.FontSize = 12.5;
            _date.Margin = new Thickness(0, 0, 0, 12);
            Children.Add(_date);

            var separator = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) };
            separator.SetResourceReference(Border.BackgroundProperty, "FloatDivider");
            Children.Add(separator);

            // Month header with previous / next
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var next = MakeArrow("", 1);
            var previous = MakeArrow("", -1);
            DockPanel.SetDock(next, Dock.Right);
            DockPanel.SetDock(previous, Dock.Right);
            header.Children.Add(next);
            header.Children.Add(previous);
            _monthTitle.SetResourceReference(StyleProperty, "FlyoutTitle");
            _monthTitle.Cursor = System.Windows.Input.Cursors.Hand;
            _monthTitle.ToolTip = "Voltar para hoje";
            _monthTitle.MouseLeftButtonUp += (_, _) => ShowMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), 0);
            header.Children.Add(_monthTitle);
            Children.Add(header);

            // Weekday names
            var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
            for (int i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)_culture.DateTimeFormat.FirstDayOfWeek + i) % 7);
                string name = _culture.DateTimeFormat.GetAbbreviatedDayName(day);
                var label = new TextBlock { Text = name.Substring(0, Math.Min(3, name.Length)).TrimEnd('.'), TextAlignment = TextAlignment.Center };
                label.SetResourceReference(StyleProperty, "FlyoutMuted");
                weekdays.Children.Add(label);
            }
            Children.Add(weekdays);
            Children.Add(_days);

            // Shortcut
            var settings = new TextBlock { Text = "Ajustar data e hora" };
            settings.SetResourceReference(StyleProperty, "FlyoutText");
            var settingsButton = new Button { Content = settings, Margin = new Thickness(0, 10, 0, 0) };
            settingsButton.SetResourceReference(StyleProperty, "FlyoutListButton");
            settingsButton.Click += (_, _) =>
            {
                FlyoutHost.Close();
                ShellHelper.StartProcess("ms-settings:dateandtime");
            };
            Children.Add(settingsButton);

            UpdateClock();
            ShowMonth(_month, 0);

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();
        }

        private Button MakeArrow(string glyph, int direction)
        {
            var text = new TextBlock { Text = glyph, FontSize = 11 };
            text.SetResourceReference(StyleProperty, "FlyoutGlyph");
            var button = new Button { Content = text, Width = 28, Height = 28, Margin = new Thickness(4, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, "FlyoutIconButton");
            button.Width = 28;
            button.Height = 28;
            button.Click += (_, _) => ShowMonth(_month.AddMonths(direction), direction);
            return button;
        }

        private void UpdateClock()
        {
            DateTime now = DateTime.Now;
            _time.Text = now.ToString("HH:mm:ss", _culture);
            string longDate = now.ToString("D", _culture);
            _date.Text = longDate.Length > 0 ? char.ToUpper(longDate[0], _culture) + longDate.Substring(1) : longDate;
        }

        private void ShowMonth(DateTime month, int direction)
        {
            _month = month;
            string title = month.ToString("MMMM yyyy", _culture);
            _monthTitle.Text = char.ToUpper(title[0], _culture) + title.Substring(1);

            _days.Children.Clear();
            int offset = ((int)month.DayOfWeek - (int)_culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            DateTime start = month.AddDays(-offset);

            for (int i = 0; i < 42; i++)
            {
                DateTime day = start.AddDays(i);
                bool inMonth = day.Month == month.Month;
                bool today = day.Date == DateTime.Today;

                var button = new Button { Content = day.Day.ToString(_culture), Focusable = false };
                button.SetResourceReference(StyleProperty, "FlyoutDayButton");
                if (today)
                {
                    button.SetResourceReference(BackgroundProperty, "FloatAccent");
                    button.Foreground = System.Windows.Media.Brushes.Black;
                    button.FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    button.Background = System.Windows.Media.Brushes.Transparent;
                    if (!inMonth)
                    {
                        button.Opacity = 0.35;
                    }
                }
                _days.Children.Add(button);
            }

            // Slide the days in from the direction of travel.
            if (direction != 0)
            {
                var translate = new System.Windows.Media.TranslateTransform();
                _days.RenderTransform = translate;
                var duration = new Duration(TimeSpan.FromMilliseconds(220));
                translate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    new DoubleAnimation(24 * direction, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                _days.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, duration));
            }
        }

        public void Dispose()
        {
            _clockTimer.Stop();
        }
    }
}
