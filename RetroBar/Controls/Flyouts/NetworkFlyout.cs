using ManagedShell.Common.Helpers;
using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace RetroBar.Controls.Flyouts
{
    /// <summary>
    /// Network flyout: current connection, its status and address, plus shortcuts to settings.
    /// </summary>
    public class NetworkFlyout : StackPanel
    {
        private readonly TextBlock _status = new TextBlock();

        public NetworkFlyout()
        {
            Width = 300;

            var title = new TextBlock { Text = "Rede", Margin = new Thickness(0, 0, 0, 12) };
            title.SetResourceReference(StyleProperty, "FlyoutTitle");
            Children.Add(title);

            var connections = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                            n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .ToList();

            bool hasWifiAdapter = NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

            if (connections.Count == 0)
            {
                Children.Add(BuildCard("", "Sem conexão", "Nenhuma rede conectada", null));
            }
            else
            {
                bool first = true;
                foreach (var connection in connections)
                {
                    bool wifi = connection.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    string ip = connection.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();

                    Children.Add(BuildCard(wifi ? "" : "", connection.Name,
                        first ? null : "Conectado", ip != null ? $"IP {ip}" : connection.Description));
                    first = false;
                }
            }

            // Shortcuts
            var separator = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 6) };
            separator.SetResourceReference(Border.BackgroundProperty, "FloatDivider");
            Children.Add(separator);

            if (hasWifiAdapter)
            {
                Children.Add(BuildLink("", "Redes Wi-Fi", "ms-settings:network-wifi"));
            }
            Children.Add(BuildLink("", "Configurações de rede e Internet", "ms-settings:network"));
            Children.Add(BuildLink("", "Opções de adaptador", "ncpa.cpl"));

            CheckInternetAsync();
        }

        private FrameworkElement BuildCard(string glyph, string name, string status, string detail)
        {
            var icon = new TextBlock { Text = glyph, FontSize = 20 };
            icon.SetResourceReference(StyleProperty, "FlyoutGlyph");

            var iconBox = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(12), Child = icon, Margin = new Thickness(0, 0, 12, 0) };
            iconBox.SetResourceReference(Border.BackgroundProperty, "FloatActive");

            var nameText = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold };
            nameText.SetResourceReference(StyleProperty, "FlyoutText");

            TextBlock statusText;
            if (status == null)
            {
                // The main connection's status is filled in once the internet check finishes.
                statusText = _status;
                statusText.Text = "Verificando conexão...";
            }
            else
            {
                statusText = new TextBlock { Text = status };
            }
            statusText.SetResourceReference(StyleProperty, "FlyoutMuted");

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(nameText);
            texts.Children.Add(statusText);
            if (detail != null)
            {
                var detailText = new TextBlock { Text = detail };
                detailText.SetResourceReference(StyleProperty, "FlyoutMuted");
                texts.Children.Add(detailText);
            }

            var card = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(iconBox, Dock.Left);
            card.Children.Add(iconBox);
            card.Children.Add(texts);
            return card;
        }

        private FrameworkElement BuildLink(string glyph, string text, string target)
        {
            var icon = new TextBlock { Text = glyph, FontSize = 13, Width = 26, HorizontalAlignment = HorizontalAlignment.Left };
            icon.SetResourceReference(StyleProperty, "FlyoutGlyph");
            icon.HorizontalAlignment = HorizontalAlignment.Left;

            var label = new TextBlock { Text = text };
            label.SetResourceReference(StyleProperty, "FlyoutText");

            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(icon);
            content.Children.Add(label);

            var button = new Button { Content = content };
            button.SetResourceReference(StyleProperty, "FlyoutListButton");
            button.Click += (_, _) =>
            {
                FlyoutHost.Close();
                ShellHelper.StartProcess(target);
            };
            return button;
        }

        private async void CheckInternetAsync()
        {
            bool? internet = await Task.Run(() =>
            {
                try
                {
                    Type type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                    dynamic networkListManager = Activator.CreateInstance(type);
                    return (bool?)(bool)networkListManager.IsConnectedToInternet;
                }
                catch
                {
                    return null;
                }
            });

            _status.Text = internet switch
            {
                true => "Conectado à Internet",
                false => "Conectado, sem Internet",
                _ => "Conectado"
            };
        }
    }
}
