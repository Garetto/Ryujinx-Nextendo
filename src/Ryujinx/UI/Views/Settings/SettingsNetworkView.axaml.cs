using Avalonia.Interactivity;
using Avalonia.Media;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Common.Configuration;
using System;

namespace Ryujinx.Ava.UI.Views.Settings
{
    public partial class SettingsNetworkView : RyujinxControl<SettingsViewModel>
    {
        private readonly Random _random;

        public SettingsNetworkView()
        {
            _random = new Random();
            InitializeComponent();

            // [Nextendo] Serveur personnalisé. Vit ici et non dans l'onglet Nextendo Network :
            // ce réglage ÉTEINT Nextendo, il n'en fait pas partie.
            ServerOverrideToggle.IsChecked = NextendoServerOverride.Enabled;
            ServerHostBox.Text = NextendoServerOverride.ServerHostText;
            ServerPortBox.Text = NextendoServerOverride.ServerPort.ToString();
            NatHostBox.Text = NextendoServerOverride.NatHostText;
            OverrideFields.IsEnabled = NextendoServerOverride.Enabled;
            ServerOverrideToggle.IsCheckedChanged += (_, _) =>
                OverrideFields.IsEnabled = ServerOverrideToggle.IsChecked == true;
            SaveOverrideButton.Click += (_, _) => SaveOverride();
        }

        private void GenLdnPassButton_OnClick(object sender, RoutedEventArgs e)
        {
            byte[] code = new byte[4];
            _random.NextBytes(code);
            ViewModel.LdnPassphrase = $"Ryujinx-{BitConverter.ToUInt32(code):x8}";
        }

        private void ClearLdnPassButton_OnClick(object sender, RoutedEventArgs e)
        {
            ViewModel.LdnPassphrase = string.Empty;
        }

        private void TestLanPlayButton_OnClick(object sender, RoutedEventArgs e)
        {
            _ = ViewModel.TestLanPlayConnection();
        }

        /// <summary>
        /// [Nextendo] Enregistre le mode « serveur personnalisé ». Il ne prend effet qu'au
        /// prochain démarrage : la table de redirection est lue une fois, et la relire à chaud
        /// changerait l'adresse d'un jeu déjà connecté.
        /// </summary>
        private void SaveOverride()
        {
            bool actif = ServerOverrideToggle.IsChecked == true;
            string serverHost = (ServerHostBox.Text ?? "").Trim();
            string natHost = (NatHostBox.Text ?? "").Trim();
            bool hasValidPort = int.TryParse(ServerPortBox.Text, out int serverPort) && serverPort is >= 1 and <= 65535;

            if (actif && !NextendoServerOverride.IsValidHost(serverHost))
            {
                ShowOverrideStatus("Enter a valid IPv4 address or DNS host name.", false);

                return;
            }

            if (actif && !hasValidPort)
            {
                ShowOverrideStatus("Enter a server port from 1 through 65535.", false);

                return;
            }

            if (actif && !string.IsNullOrEmpty(natHost) && !NextendoServerOverride.IsValidHost(natHost))
            {
                ShowOverrideStatus("The optional NAT probe host must be an IPv4 address or DNS host name.", false);

                return;
            }

            NextendoServerOverride.Save(actif, serverHost, serverPort, natHost);
            ShowOverrideStatus(LocaleManager.Instance[LocaleKeys.Dialog_Nextendo_OverrideSaved], true);
        }

        private void ShowOverrideStatus(string texte, bool ok)
        {
            OverrideStatusText.Text = texte;
            OverrideStatusText.Foreground = Brush.Parse(ok ? "#3EE8C8" : "#E8333E");
            OverrideStatusText.IsVisible = !string.IsNullOrEmpty(texte);
        }
    }
}
