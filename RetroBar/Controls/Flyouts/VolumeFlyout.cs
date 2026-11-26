using RetroBar.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RetroBar.Controls.Flyouts
{
    /// <summary>
    /// Volume flyout: mute button, volume slider and output device list.
    /// </summary>
    public class VolumeFlyout : StackPanel, IDisposable
    {
        private readonly AudioController _audio = new AudioController();
        private readonly Slider _slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10 };
        private readonly TextBlock _percent = new TextBlock { Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _muteGlyph = new TextBlock();
        private readonly TextBlock _deviceName = new TextBlock();
        private readonly StackPanel _devices = new StackPanel();
        private readonly DispatcherTimer _syncTimer;
        private bool _updating;
        private int _ticks;

        // Mixer: one row per app.
        private readonly StackPanel _mixer = new StackPanel();
        private readonly List<MixerRow> _rows = new List<MixerRow>();
        private string _mixerSignature = "";

        private sealed class MixerRow
        {
            public AudioController.AppAudio App;
            public FrameworkElement Element;
            public Slider Slider;
            public TextBlock Percent;
            public TextBlock MuteGlyph;
            public bool Updating;
        }

        public VolumeFlyout()
        {
            Width = 330;

            var title = new TextBlock { Text = "Volume" };
            title.SetResourceReference(StyleProperty, "FlyoutTitle");
            Children.Add(title);

            _deviceName.SetResourceReference(StyleProperty, "FlyoutMuted");
            _deviceName.Margin = new Thickness(0, 2, 0, 12);
            Children.Add(_deviceName);

            // Mute button, slider, percentage
            var row = new DockPanel { LastChildFill = true };
            var muteButton = new Button { Content = _muteGlyph, ToolTip = "Mudo" };
            muteButton.SetResourceReference(StyleProperty, "FlyoutIconButton");
            _muteGlyph.SetResourceReference(StyleProperty, "FlyoutGlyph");
            muteButton.Click += (_, _) =>
            {
                _audio.IsMuted = !_audio.IsMuted;
                SyncFromSystem();
            };
            DockPanel.SetDock(muteButton, Dock.Left);
            row.Children.Add(muteButton);

            _percent.SetResourceReference(StyleProperty, "FlyoutText");
            DockPanel.SetDock(_percent, Dock.Right);
            row.Children.Add(_percent);

            _slider.SetResourceReference(StyleProperty, "FlyoutSlider");
            _slider.Margin = new Thickness(12, 0, 8, 0);
            _slider.VerticalAlignment = VerticalAlignment.Center;
            _slider.ValueChanged += (_, e) =>
            {
                if (_updating) return;
                _audio.Volume = (float)(e.NewValue / 100.0);
                if (_audio.IsMuted && e.NewValue > 0)
                {
                    _audio.IsMuted = false;
                }
                UpdateLabels();
            };
            row.Children.Add(_slider);
            Children.Add(row);

            // Per-app volume mixer
            // Header: "Mixer" and, when there are more apps than fit, page arrows (3 apps per page).
            var mixerHeader = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
            var nextPage = MakePageArrow("", 1);
            var previousPage = MakePageArrow("", -1);
            _pageText.SetResourceReference(StyleProperty, "FlyoutMuted");
            _pageText.VerticalAlignment = VerticalAlignment.Center;
            _pageText.Margin = new Thickness(0, 0, 6, 0);
            _pager.Orientation = Orientation.Horizontal;
            _pager.Children.Add(_pageText);
            _pager.Children.Add(previousPage);
            _pager.Children.Add(nextPage);
            DockPanel.SetDock(_pager, Dock.Right);
            mixerHeader.Children.Add(_pager);
            var mixerLabel = new TextBlock { Text = "Mixer", VerticalAlignment = VerticalAlignment.Center };
            mixerLabel.SetResourceReference(StyleProperty, "FlyoutMuted");
            mixerHeader.Children.Add(mixerLabel);
            Children.Add(mixerHeader);

            // Fixed height for a full page, so the panel doesn't jump between pages.
            _mixer.MinHeight = AppsPerPage * RowHeight;
            _mixer.ClipToBounds = true;
            Children.Add(_mixer);
            BuildMixer();

            // Output devices
            var outputLabel = new TextBlock { Text = "Saída", Margin = new Thickness(0, 16, 0, 4) };
            outputLabel.SetResourceReference(StyleProperty, "FlyoutMuted");
            Children.Add(outputLabel);
            Children.Add(_devices);

            BuildDeviceList();
            SyncFromSystem();

            // Follow changes made elsewhere (keyboard volume keys, other apps) while open.
            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _syncTimer.Tick += (_, _) =>
            {
                if (!_slider.IsMouseCaptureWithin)
                {
                    SyncFromSystem();
                }

                // Apps start and stop playing: refresh the mixer list every couple of seconds.
                if (++_ticks % 5 == 0 && !_mixer.IsMouseCaptureWithin)
                {
                    BuildMixer();
                }
                else
                {
                    SyncMixer();
                }
            };
            _syncTimer.Start();
        }

        private void SyncFromSystem()
        {
            _updating = true;
            _slider.Value = Math.Round(_audio.Volume * 100);
            _updating = false;
            UpdateLabels();
        }

        private void UpdateLabels()
        {
            int value = (int)Math.Round(_slider.Value);
            _percent.Text = value.ToString();

            if (_audio.IsMuted || value == 0)
            {
                _muteGlyph.Text = ""; // muted
            }
            else if (value < 34)
            {
                _muteGlyph.Text = "";
            }
            else if (value < 67)
            {
                _muteGlyph.Text = "";
            }
            else
            {
                _muteGlyph.Text = "";
            }

            _slider.Opacity = _audio.IsMuted ? 0.45 : 1;
        }

        private void BuildMixer()
        {
            var apps = _audio.GetApps();
            string signature = string.Join("|", apps.Select(a => a.Name + ":" + a.ProcessId));

            if (signature == _mixerSignature && _rows.Count == apps.Count)
            {
                // Same apps: just point the rows at the fresh session objects.
                for (int i = 0; i < apps.Count; i++)
                {
                    _rows[i].App = apps[i];
                }
                SyncMixer();
                return;
            }

            _mixerSignature = signature;
            _rows.Clear();

            foreach (var app in apps)
            {
                var row = new MixerRow { App = app };
                row.Element = BuildMixerRow(row);
                _rows.Add(row);
            }

            ShowPage(Math.Min(_page, Math.Max(0, PageCount - 1)), 0);
            SyncMixer();
        }

        private const int AppsPerPage = 3;
        private const double RowHeight = 50;
        private int _page;
        private readonly StackPanel _pager = new StackPanel();
        private readonly TextBlock _pageText = new TextBlock();

        private int PageCount => (_rows.Count + AppsPerPage - 1) / AppsPerPage;

        private Button MakePageArrow(string glyph, int direction)
        {
            var text = new TextBlock { Text = glyph, FontSize = 10 };
            text.SetResourceReference(StyleProperty, "FlyoutGlyph");
            var button = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0) };
            button.SetResourceReference(StyleProperty, "FlyoutIconButton");
            button.Width = 24;
            button.Height = 24;
            button.Click += (_, _) =>
            {
                int pages = PageCount;
                if (pages <= 1) return;
                ShowPage((_page + direction + pages) % pages, direction);
            };
            return button;
        }

        private void ShowPage(int page, int direction)
        {
            _page = page;
            _mixer.Children.Clear();

            foreach (var row in _rows.Skip(page * AppsPerPage).Take(AppsPerPage))
            {
                _mixer.Children.Add(row.Element);
            }

            if (_rows.Count == 0)
            {
                var none = new TextBlock { Text = "Nenhum app tocando som" };
                none.SetResourceReference(StyleProperty, "FlyoutMuted");
                none.Margin = new Thickness(0, 0, 0, 4);
                _mixer.Children.Add(none);
            }

            int pages = PageCount;
            _pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            _pageText.Text = $"{page + 1}/{Math.Max(1, pages)}";

            // Slide the page in from the side it comes from.
            if (direction != 0)
            {
                var translate = new TranslateTransform();
                _mixer.RenderTransform = translate;
                var duration = new Duration(TimeSpan.FromMilliseconds(220));
                translate.BeginAnimation(TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(30 * direction, 0, duration)
                    {
                        EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                    });
                _mixer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.2, 1, duration));
            }
        }

        private FrameworkElement BuildMixerRow(MixerRow row)
        {
            // App icon
            FrameworkElement icon;
            ImageSource image = GetAppIcon(row.App);
            if (image != null)
            {
                icon = new Image { Source = image, Width = 22, Height = 22 };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            }
            else
            {
                var glyph = new TextBlock { Text = row.App.IsSystemSounds ? "" : "", FontSize = 15 };
                glyph.SetResourceReference(StyleProperty, "FlyoutGlyph");
                icon = glyph;
            }
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(2, 0, 12, 0);

            // Name and percentage
            var name = new TextBlock { Text = row.App.Name, ToolTip = row.App.Name };
            name.SetResourceReference(StyleProperty, "FlyoutText");
            row.Percent = new TextBlock { Margin = new Thickness(8, 0, 0, 0) };
            row.Percent.SetResourceReference(StyleProperty, "FlyoutMuted");
            var header = new DockPanel();
            DockPanel.SetDock(row.Percent, Dock.Right);
            header.Children.Add(row.Percent);
            header.Children.Add(name);

            // Slider
            row.Slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, 2, 0, 0) };
            row.Slider.SetResourceReference(StyleProperty, "FlyoutSlider");
            row.Slider.ValueChanged += (_, e) =>
            {
                if (row.Updating) return;
                row.App.Volume = (float)(e.NewValue / 100.0);
                if (row.App.IsMuted && e.NewValue > 0)
                {
                    row.App.IsMuted = false;
                }
                UpdateMixerRow(row);
            };

            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            center.Children.Add(header);
            center.Children.Add(row.Slider);

            // Mute
            row.MuteGlyph = new TextBlock { FontSize = 13 };
            row.MuteGlyph.SetResourceReference(StyleProperty, "FlyoutGlyph");
            var mute = new Button { Content = row.MuteGlyph, ToolTip = "Mudo", Margin = new Thickness(10, 0, 0, 0) };
            mute.SetResourceReference(StyleProperty, "FlyoutIconButton");
            mute.Width = 30;
            mute.Height = 30;
            mute.Click += (_, _) =>
            {
                row.App.IsMuted = !row.App.IsMuted;
                UpdateMixerRow(row);
            };

            var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 7) };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(mute, Dock.Right);
            panel.Children.Add(icon);
            panel.Children.Add(mute);
            panel.Children.Add(center);
            return panel;
        }

        private void SyncMixer()
        {
            foreach (var row in _rows)
            {
                if (row.Slider == null || row.Slider.IsMouseCaptureWithin)
                {
                    continue;
                }

                row.Updating = true;
                row.Slider.Value = Math.Round(row.App.Volume * 100);
                row.Updating = false;
                UpdateMixerRow(row);
            }
        }

        private static void UpdateMixerRow(MixerRow row)
        {
            bool muted = row.App.IsMuted;
            row.Percent.Text = ((int)Math.Round(row.Slider.Value)).ToString();
            row.MuteGlyph.Text = muted || row.Slider.Value == 0 ? "" : "";
            row.Slider.Opacity = muted ? 0.45 : 1;
        }

        private static readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static ImageSource GetAppIcon(AudioController.AppAudio app)
        {
            if (string.IsNullOrEmpty(app.ExecutablePath))
            {
                return null;
            }

            if (_iconCache.TryGetValue(app.ExecutablePath, out ImageSource cached))
            {
                return cached;
            }

            ImageSource source = null;
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app.ExecutablePath);
                if (icon != null)
                {
                    source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }
            }
            catch
            {
                // Fall back to a glyph.
            }

            _iconCache[app.ExecutablePath] = source;
            return source;
        }

        private void BuildDeviceList()
        {
            _devices.Children.Clear();

            foreach (var device in _audio.GetOutputDevices())
            {
                if (device.IsDefault)
                {
                    _deviceName.Text = device.Name;
                }

                var check = new TextBlock { Text = device.IsDefault ? "" : "", Width = 22, FontSize = 12 };
                check.SetResourceReference(StyleProperty, "FlyoutGlyph");
                check.HorizontalAlignment = HorizontalAlignment.Left;

                var name = new TextBlock { Text = device.Name };
                name.SetResourceReference(StyleProperty, "FlyoutText");
                if (device.IsDefault)
                {
                    name.FontWeight = FontWeights.SemiBold;
                }

                var content = new DockPanel();
                DockPanel.SetDock(check, Dock.Left);
                content.Children.Add(check);
                content.Children.Add(name);

                var button = new Button { Content = content, ToolTip = device.Name };
                button.SetResourceReference(StyleProperty, "FlyoutListButton");
                string id = device.Id;
                button.Click += (_, _) =>
                {
                    _audio.SetDefaultOutputDevice(id);
                    BuildDeviceList();
                    SyncFromSystem();
                };
                _devices.Children.Add(button);
            }

            if (_devices.Children.Count == 0)
            {
                var none = new TextBlock { Text = "Nenhum dispositivo de saída encontrado" };
                none.SetResourceReference(StyleProperty, "FlyoutMuted");
                _devices.Children.Add(none);
            }
        }

        public void Dispose()
        {
            _syncTimer.Stop();
            _audio.Dispose();
        }
    }
}
