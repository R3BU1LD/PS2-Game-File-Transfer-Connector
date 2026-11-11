using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PS2FTPCONNECTOR
{
    public class Ps2GameJob : INotifyPropertyChanged
    {
        private string _status = "QUEUED";
        private string _title;
        private string _gameId;
        public string IsoPath { get; set; }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
        public string GameId { get => _gameId; set { _gameId = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : Window
    {
        public ObservableCollection<Ps2GameJob> GameQueue { get; set; } = new ObservableCollection<Ps2GameJob>();
        public ObservableCollection<Ps2GameJob> InstalledGames { get; set; } = new ObservableCollection<Ps2GameJob>();

        // Onze nieuwe netwerk-engine!
        private readonly HdlStreamer _streamer = new HdlStreamer();

        public MainWindow()
        {
            InitializeComponent();
            LvQueue.ItemsSource = GameQueue;
            LvPs2Disk.ItemsSource = InstalledGames;

            // Koppel de events van de streamer direct aan onze UI
            _streamer.OnLog += LogWrite;
            _streamer.OnProgress += UpdateProgressUi;

            LogWrite(">> SYSTEM INITIATED. AWAITING OPERATOR...");
        }

        private void LogWrite(string text)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
                TxtLog.ScrollToEnd();
            });
        }

        private void UpdateProgressUi(double percentage, double speed, string eta)
        {
            Dispatcher.Invoke(() =>
            {
                MyProgressBar.Value = percentage;
                PercentageText.Text = $"{percentage:F1}%";
                TxtSpeed.Text = $"{speed:F2} MB/s";
                TxtEta.Text = eta;
            });
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            // ALS WE AL VERBONDEN ZIJN: Verbreek de verbinding (DISCONNECT)
            if (_streamer.IsConnected)
            {
                _streamer.Disconnect();
                InstalledGames.Clear();
                TxtNoConnection.Visibility = Visibility.Visible;
                TxtIpAddress.IsEnabled = true;
                BtnReloadGames.IsEnabled = false;

                // Zet de knop weer terug naar CONNECT (Groen)
                BtnConnect.Content = "CONNECT";
                BtnConnect.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));
                BtnConnect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));

                StatusText.Text = ">> CONNECTION CLOSED. PS2 PORT RELEASED.";
                LogWrite(">> DISCONNECTED FROM PS2. Console returned to idle state.");
                return;
            }

            // ALS WE NOG NIET VERBONDEN ZIJN: Maak verbinding (CONNECT)
            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip)) { MessageBox.Show("Vul een geldig IP-adres in."); return; }

            BtnConnect.IsEnabled = false;
            LogWrite($">> ATTEMPTING HANDSHAKE WITH HDL NODE AT {ip}...");
            StatusText.Text = ">> CONNECTING TO HDL GAME INSTALLER SERVER...";
            InstalledGames.Clear();

            bool isServerAlive = await Task.Run(() => _streamer.ConnectAndHandshake(ip));

            BtnConnect.IsEnabled = true;
            if (isServerAlive)
            {
                TxtNoConnection.Visibility = Visibility.Collapsed;
                TxtIpAddress.IsEnabled = false; // Vergrendel IP-veld tijdens actieve sessie
                BtnReloadGames.IsEnabled = true;
                StatusText.Text = ">> CONNECTION ESTABLISHED TO HDL NODE!";
                LogWrite($">> SUCCESS: Connected to HDLGameInstaller server on {ip}:45061.");

                // Verander de knop naar DISCONNECT (Rood)
                BtnConnect.Content = "DISCONNECT";
                BtnConnect.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 60, 60));
                BtnConnect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 60, 60));

                var gamesOnHdd = await Task.Run(() => _streamer.LoadInstalledGames());
                foreach (var g in gamesOnHdd) InstalledGames.Add(g);
            }
            else
            {
                TxtNoConnection.Visibility = Visibility.Visible;
                StatusText.Text = ">> CONNECTION REFUSED ON PORT 45061.";
                LogWrite(">> ERROR: Connection refused. Check of HDLGameInstaller open staat op de PS2.");
            }
        }

        // Zorg dat de PS2 ook netjes wordt vrijgegeven als je de hele PC-app afsluit
        protected override void OnClosed(EventArgs e)
        {
            _streamer.Disconnect();
            base.OnClosed(e);
        }



        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "PS2 ISO Bestanden (*.iso)|*.iso", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                LogWrite($">> LOADING DATASET: {openFileDialog.FileNames.Length} ISO bestanden geselecteerd.");
                foreach (string selectedIso in openFileDialog.FileNames)
                {
                    string title = Path.GetFileNameWithoutExtension(selectedIso);
                    string id = _streamer.ExtractGameId(selectedIso) ?? "UNKNOWN_ID";
                    GameQueue.Add(new Ps2GameJob { IsoPath = selectedIso, Title = title, GameId = id, Status = "QUEUED" });
                    LogWrite($">> NODE REGISTERED: [{id}] {title}");
                }
                UpdateQueueStatusHeader();
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip) || GameQueue.Count == 0) { MessageBox.Show("Geen doel-IP of wachtrij aanwezig."); return; }

            BtnInstall.IsEnabled = false; BtnBrowse.IsEnabled = false; BtnClearQueue.IsEnabled = false;
            LogWrite(">> INITIATING BATCH DATA INJECTION STREAM...");

            for (int i = 0; i < GameQueue.Count; i++)
            {
                var job = GameQueue[i];
                if (job.Status == "DONE") continue;

                job.Status = "UPLOADING...";
                UpdateProgressUi(0, 0, "CALCULATING...");
                StatusText.Text = $">> INJECTING [{i + 1}/{GameQueue.Count}]: {job.Title}";
                LogWrite($">> START STREAM NODE [{i + 1}/{GameQueue.Count}]: {job.Title} ({job.GameId})");

                bool success = await Task.Run(() => _streamer.UploadIso(ip, job));

                if (success)
                {
                    job.Status = "DONE";
                    LogWrite($">> SUCCESS: Node {job.Title} succesvol overgedragen.");
                }
                else
                {
                    job.Status = "FAILED";
                    LogWrite($">> CRITICAL INTERRUPT: Overdracht gefaald voor {job.Title}.");
                    if (MessageBox.Show($"Stream afgebroken bij '{job.Title}'. Wilt u doorgaan met de rest?", "Stream Fout", MessageBoxButton.YesNo) == MessageBoxResult.No) break;
                }
            }

            LogWrite(">> BATCH PROCESS TERMINATED.");
            StatusText.Text = ">> BATCH STREAM PROCESS COMPLETED.";
            UpdateProgressUi(100, 0, "00:00:00");
            BtnInstall.IsEnabled = true; BtnBrowse.IsEnabled = true; BtnClearQueue.IsEnabled = true;
            UpdateQueueStatusHeader();
        }
        private async void BtnReloadGames_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected)
            {
                MessageBox.Show("Maak eerst verbinding met de PS2 voordat je de lijst ververst.", "Geen verbinding");
                return;
            }

            BtnReloadGames.IsEnabled = false;
            LogWrite(">> REFRESHING INSTALLED GAMES LIST FROM PS2 HDD...");
            StatusText.Text = ">> SCANNING CONSOLE HARD DRIVE...";
            InstalledGames.Clear();

            var gamesOnHdd = await Task.Run(() => _streamer.LoadInstalledGames());
            foreach (var g in gamesOnHdd) InstalledGames.Add(g);

            StatusText.Text = $">> HDD SCAN COMPLETE: {InstalledGames.Count} GAMES FOUND.";
            BtnReloadGames.IsEnabled = true;
        }


        private void LvQueue_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) { TxtGameTitle.Text = job.Title; TxtGameId.Text = job.GameId; } }
        private void TxtGameTitle_LostFocus(object sender, RoutedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) job.Title = TxtGameTitle.Text; }
        private void TxtGameId_LostFocus(object sender, RoutedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) job.GameId = TxtGameId.Text; }
        private void BtnClearQueue_Click(object sender, RoutedEventArgs e) { GameQueue.Clear(); TxtGameTitle.Text = ""; TxtGameId.Text = ""; UpdateQueueStatusHeader(); LogWrite(">> MATRIX STACK PURGED."); }
        private void UpdateQueueStatusHeader() => TxtQueueStatus.Text = $"QUEUE: {GameQueue.Count} GAMES LOADED";
    }
}
