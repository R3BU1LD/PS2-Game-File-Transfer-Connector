using System;
using System.Threading.Tasks;
using System.Windows;
using FluentFTP;

namespace PS2FTPCONNECTOR
{
    public partial class MainWindow : Window
    {
        // Nu gekoppeld aan de juiste FluentFTP asynchrone klasse
        private AsyncFtpClient _ftpClient;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            string ip = IpTextBox.Text;
            int port = int.Parse(PortTextBox.Text);

            // Correcte initialisatie van de Async Client
            _ftpClient = new AsyncFtpClient(ip, "anonymous", "anonymous", port);

            _ftpClient.Config.EncryptionMode = FtpEncryptionMode.None;
            _ftpClient.Config.DataConnectionType = FtpDataConnectionType.PASV;
            _ftpClient.Config.ConnectTimeout = 15000;

            try
            {
                StatusLabel.Content = "Verbinden met PS2...";

                // Maakt asynchroon verbinding met de PS2
                await _ftpClient.Connect();

                StatusLabel.Content = "PS2FTPCONNECTOR: Verbonden!";

                await LoadDirectoryListing();
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Fout: {ex.Message}";
            }
        }

        private async Task LoadDirectoryListing()
        {
            FileListBox.Items.Clear();

            // Haalt asynchroon de mappenlijst op
            var items = await _ftpClient.GetListing();

            foreach (var item in items)
            {
                FileListBox.Items.Add($"[{item.Type}] {item.Name}");
            }
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_ftpClient == null || !_ftpClient.IsConnected)
            {
                MessageBox.Show("Maak eerst verbinding met de PS2!");
                return;
            }

            string localPath = @"C:\Games\PS2Game.iso";
            string remotePath = "/hdd0:/DVD/PS2Game.iso";

            StatusLabel.Content = "Uploaden naar PS2...";

            // Upload asynchroon het bestand naar de console
            var status = await _ftpClient.UploadFile(localPath, remotePath);

            if (status == FtpStatus.Success)
            {
                StatusLabel.Content = "Upload voltooid!";
                await LoadDirectoryListing();
            }
            else
            {
                StatusLabel.Content = "Upload mislukt.";
            }
        }
    }
}
