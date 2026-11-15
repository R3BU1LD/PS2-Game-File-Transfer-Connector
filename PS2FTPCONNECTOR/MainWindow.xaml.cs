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
        public string PartName { get; set; }
        public uint Sectors { get; set; }
        public byte CompatFlags { get; set; }
        public byte TrType { get; set; }
        public byte TrMode { get; set; }
        public byte DiscType { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : Window
    {
        public ObservableCollection<Ps2GameJob> GameQueue { get; set; } = new ObservableCollection<Ps2GameJob>();
        public ObservableCollection<Ps2GameJob> InstalledGames { get; set; } = new ObservableCollection<Ps2GameJob>();

        // Onze nieuwe netwerk-engine!
        private readonly HdlStreamer _streamer = new HdlStreamer();
        private readonly string _autoLogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Logs",
            $"PS2FTP_Session_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        private readonly object _logFileLock = new object();
        private volatile bool _stopBatchRequested = false;

        public MainWindow()
        {
            InitializeComponent();
            LvQueue.ItemsSource = GameQueue;
            LvPs2Disk.ItemsSource = InstalledGames;
            this.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) this.DragMove(); };
            if (File.Exists("ps2_ip.cfg")) TxtIpAddress.Text = File.ReadAllText("ps2_ip.cfg").Trim();

            // Koppel de events van de streamer direct aan onze UI
            _streamer.OnLog += LogWrite;
            _streamer.OnProgress += UpdateProgressUi;

            LogWrite(">> SYSTEM INITIATED. AWAITING OPERATOR...");
        }

        private void LogWrite(string text)
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}";

            // 1. Automatisch opslaan op schijf (thread-safe, ook tijdens background uploads)
            try
            {
                lock (_logFileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_autoLogPath)!);
                    File.AppendAllText(_autoLogPath, logLine + Environment.NewLine);
                }
            }
            catch
            {
                // Voorkom crash als logbestand tijdelijk vergrendeld is
            }

            // 2. Toon direct in de UI terminal
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
                TxtLog.ScrollToEnd();
            });
        }
        private void BtnStopBatch_Click(object sender, RoutedEventArgs e)
        {
            _stopBatchRequested = true;
            BtnStopBatch.IsEnabled = false;
            BtnStopBatch.Content = "[ ⏳ ] STOPPING...";
            StatusText.Text = ">> SAFE STOP REQUESTED: FINISHING CURRENT ISO BEFORE HALTING...";
            LogWrite(">> [OPERATOR] Safe Stop aangevraagd. Wachtrij stopt zodra huidige game klaar is!");
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
                BtnBackupGame.IsEnabled = false;
                BtnDeleteGame.IsEnabled = false;
                TxtFreeSpace.Text = "PS2 FREE SPACE: -- GB";
                
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
                BtnDeleteGame.IsEnabled = true;
                BtnBackupGame.IsEnabled = true;
                TxtFreeSpace.Text = "PS2 FREE SPACE: " + await Task.Run(() => _streamer.GetFreeSpaceFormatted());
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

        private void LvQueue_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }
        // Plaats de nieuwe Drag & Drop functie hier, helemaal onderaan de klasse:
        private void LvQueue_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] droppedFiles = (string[])e.Data.GetData(DataFormats.FileDrop);
                int addedCount = 0;

                foreach (string file in droppedFiles)
                {
                    if (Path.GetExtension(file).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    {
                        string title = Path.GetFileNameWithoutExtension(file);
                        string id = _streamer.ExtractGameId(file) ?? "UNKNOWN_ID";
                        GameQueue.Add(new Ps2GameJob { IsoPath = file, Title = title, GameId = id, Status = "QUEUED" });
                        LogWrite($">> DRAG & DROP REGISTERED: [{id}] {title}");
                        addedCount++;
                    }
                }

                if (addedCount > 0)
                {
                    UpdateQueueStatusHeader();
                }
                else
                {
                    LogWrite(">> [WARN] Geen geldige .iso bestanden herkend bij Drag & Drop.");
                }
            }
        }




        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip) || GameQueue.Count == 0) { MessageBox.Show("Geen doel-IP of wachtrij aanwezig."); return; }

            _stopBatchRequested = false;
            BtnInstall.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            BtnClearQueue.IsEnabled = false;
            BtnStopBatch.IsEnabled = true;
            BtnStopBatch.Content = "[ ⏹ ] SAFE STOP";

            LogWrite(">> INITIATING BATCH DATA INJECTION STREAM...");

            for (int i = 0; i < GameQueue.Count; i++)
            {
                if (_stopBatchRequested)
                {
                    LogWrite(">> [PAUSED] Batch netjes gepauzeerd op verzoek van operator. Geen corrupte data!");
                    StatusText.Text = ">> BATCH SAFELY PAUSED. READY TO RESUME.";
                    break;
                }

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

            if (!_stopBatchRequested)
            {
                LogWrite(">> BATCH PROCESS TERMINATED.");
                StatusText.Text = ">> BATCH STREAM PROCESS COMPLETED.";
                UpdateProgressUi(100, 0, "00:00:00");
            }

            BtnStopBatch.IsEnabled = false;
            BtnStopBatch.Content = "[ ⏹ ] SAFE STOP";
            BtnInstall.IsEnabled = true;
            BtnBrowse.IsEnabled = true;
            BtnClearQueue.IsEnabled = true;
            UpdateQueueStatusHeader();
        }

        private async void BtnReloadGames_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected)
            {
                MessageBox.Show("Maak eerst verbinding met de PS2 voordat je de lijst ververst.", "Geen verbinding");
                return;
            }
            TxtFreeSpace.Text = "PS2 FREE SPACE: " + await Task.Run(() => _streamer.GetFreeSpaceFormatted());
            BtnReloadGames.IsEnabled = false;
            BtnDeleteGame.IsEnabled = false;
            LogWrite(">> REFRESHING INSTALLED GAMES LIST FROM PS2 HDD...");
            StatusText.Text = ">> SCANNING CONSOLE HARD DRIVE...";
            InstalledGames.Clear();

            var gamesOnHdd = await Task.Run(() => _streamer.LoadInstalledGames());
            foreach (var g in gamesOnHdd) InstalledGames.Add(g);

            StatusText.Text = $">> HDD SCAN COMPLETE: {InstalledGames.Count} GAMES FOUND.";
            BtnReloadGames.IsEnabled = true;
            
        }
        private async void BtnDeleteGame_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected) return;

            if (LvPs2Disk.SelectedItem is not Ps2GameJob selectedGame)
            {
                MessageBox.Show("Selecteer eerst een game in de lijst om te verwijderen.", "Geen selectie");
                return;
            }

            var confirm = MessageBox.Show($"Weet je zeker dat je '{selectedGame.Title}' ({selectedGame.GameId}) definitief van je PS2 harde schijf wilt verwijderen?", "Game Verwijderen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            BtnDeleteGame.IsEnabled = false;
            BtnReloadGames.IsEnabled = false;
            LogWrite($">> DELETING PARTITION [{selectedGame.PartName}]: {selectedGame.Title}...");

            bool deleted = await Task.Run(() => _streamer.DeleteGame(selectedGame.PartName));

            if (deleted)
            {
                InstalledGames.Remove(selectedGame);
                LogWrite($">> SUCCESS: '{selectedGame.Title}' verwijderd van PS2 HDD.");
                TxtFreeSpace.Text = "PS2 FREE SPACE: " + await Task.Run(() => _streamer.GetFreeSpaceFormatted());
            }
            else
            {
                LogWrite($">> ERROR: Verwijderen van '{selectedGame.Title}' mislukt.");
                MessageBox.Show("Kon de game niet verwijderen van de PS2.", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnBackupGame.IsEnabled = true;
            BtnDeleteGame.IsEnabled = true;
            BtnReloadGames.IsEnabled = true;
        }
        private async void BtnBackupGame_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected) return;

            if (LvPs2Disk.SelectedItem is not Ps2GameJob selectedGame)
            {
                MessageBox.Show("Selecteer eerst een game in de lijst om te backuppen.", "Geen selectie");
                return;
            }

            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Filter = "PS2 ISO Image (*.iso)|*.iso",
                FileName = $"{selectedGame.Title} ({selectedGame.GameId}).iso"
            };

            if (saveDialog.ShowDialog() != true) return;

            BtnBackupGame.IsEnabled = false;
            BtnDeleteGame.IsEnabled = false;
            BtnReloadGames.IsEnabled = false;
            UpdateProgressUi(0, 0, "CALCULATING...");
            StatusText.Text = $">> RIPPING ISO FROM PS2: {selectedGame.Title}...";

            string ip = TxtIpAddress.Text.Trim();
            bool success = await Task.Run(() => _streamer.DownloadIso(ip, selectedGame, saveDialog.FileName));

            if (success)
            {
                LogWrite($">> BACKUP COMPLETED: Opgeslagen als {saveDialog.FileName}");
                StatusText.Text = ">> BACKUP STREAM COMPLETED.";
                UpdateProgressUi(100, 0, "00:00:00");
            }
            else
            {
                LogWrite(">> [ERROR] Backup afgebroken of mislukt.");
                StatusText.Text = ">> BACKUP FAILED.";
            }

            BtnBackupGame.IsEnabled = true;
            BtnDeleteGame.IsEnabled = true;
            BtnReloadGames.IsEnabled = true;
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
        private void BtnPowerOff_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected)
            {
                MessageBox.Show("Maak eerst verbinding met de PS2 om hem uit te schakelen.", "Niet verbonden");
                return;
            }

            if (MessageBox.Show("Weet je zeker dat je de PlayStation 2 nu wilt uitschakelen?", "PS2 Uitschakelen", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                if (_streamer.PowerOffPs2())
                {
                    InstalledGames.Clear();
                    TxtNoConnection.Visibility = Visibility.Visible;
                    TxtIpAddress.IsEnabled = true;
                    BtnReloadGames.IsEnabled = false;
                    BtnDeleteGame.IsEnabled = false;
                    TxtFreeSpace.Text = "PS2 FREE SPACE: -- GB";

                    BtnConnect.Content = "CONNECT";
                    BtnConnect.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));
                    BtnConnect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));

                    LogWrite(">> POWER-OFF SIGNAL SENT! PS2 is shutting down.");
                    StatusText.Text = ">> CONSOLE POWERED OFF.";
                }
            }
        }

        private async void BtnSaveGameSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected || LvPs2Disk.SelectedItem is not Ps2GameJob selectedGame)
            {
                MessageBox.Show("Selecteer een geïnstalleerde game terwijl de PS2 verbonden is.", "Geen selectie");
                return;
            }

            var updatedGame = new Ps2GameJob
            {
                PartName = selectedGame.PartName,
                Title = TxtEditPs2Title.Text,
                GameId = selectedGame.GameId,
                IsoPath = selectedGame.IsoPath,
                Sectors = selectedGame.Sectors,
                CompatFlags = GetCompatibilityFlags(),
                TrType = selectedGame.TrType,
                TrMode = selectedGame.TrMode,
                DiscType = selectedGame.DiscType,
                Status = selectedGame.Status
            };

            BtnSaveGameSettings.IsEnabled = false;
            bool saved = await Task.Run(() => _streamer.UpdateGameSettings(updatedGame));
            if (saved)
            {
                selectedGame.Title = updatedGame.Title;
                selectedGame.CompatFlags = updatedGame.CompatFlags;
                LogWrite($">> SETTINGS SAVED FOR [{selectedGame.GameId}].");
            }
            else
            {
                LogWrite($">> [ERROR] Could not save settings for [{selectedGame.GameId}].");
                MessageBox.Show("De instellingen konden niet op de PS2 worden opgeslagen.", "Opslaan mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            BtnSaveGameSettings.IsEnabled = _streamer.IsConnected && LvPs2Disk.SelectedItem == selectedGame;
        }

        private void BtnSaveLog_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Filter = "Log Bestanden (*.log;*.txt)|*.log;*.txt|Alle bestanden (*.*)|*.*",
                FileName = $"PS2FTP_Report_{DateTime.Now:yyyy-MM-dd_HH-mm}.log"
            };

            if (saveDialog.ShowDialog() == true)
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("==========================================================");
                    sb.AppendLine($" PS2FTPCONNECTOR // TRANSFER REPORT ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
                    sb.AppendLine("==========================================================");
                    sb.AppendLine();

                    if (GameQueue.Count > 0)
                    {
                        sb.AppendLine("--- BATCH QUEUE STATUS OVERZICHT ---");
                        foreach (var job in GameQueue)
                        {
                            sb.AppendLine($"[{job.Status,-12}] [{job.GameId}] {job.Title}");
                        }
                        sb.AppendLine();
                    }

                    sb.AppendLine("--- VOLLEDIGE TERMINAL LOG ---");
                    sb.AppendLine(TxtLog.Text);

                    File.WriteAllText(saveDialog.FileName, sb.ToString());
                    LogWrite($">> LOG SAVED MANUALLY TO: {saveDialog.FileName}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Kon logbestand niet opslaan: {ex.Message}", "Opslaan mislukt", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void LvPs2Disk_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvPs2Disk.SelectedItem is Ps2GameJob selectedGame)
            {
                TxtEditPs2Title.Text = selectedGame.Title;
                SetCompatibilityModes(selectedGame.CompatFlags);
                BtnSaveGameSettings.IsEnabled = _streamer.IsConnected;
                return;
            }

            TxtEditPs2Title.Clear();
            SetCompatibilityModes(0);
            BtnSaveGameSettings.IsEnabled = false;
        }

        private void SetCompatibilityModes(byte flags)
        {
            ChkMode1.IsChecked = (flags & 0x01) != 0;
            ChkMode2.IsChecked = (flags & 0x02) != 0;
            ChkMode3.IsChecked = (flags & 0x04) != 0;
            ChkMode4.IsChecked = (flags & 0x08) != 0;
            ChkMode5.IsChecked = (flags & 0x10) != 0;
            ChkMode6.IsChecked = (flags & 0x20) != 0;
            ChkMode7.IsChecked = (flags & 0x40) != 0;
            ChkMode8.IsChecked = (flags & 0x80) != 0;
        }

        private byte GetCompatibilityFlags()
        {
            byte flags = 0;
            if (ChkMode1.IsChecked == true) flags |= 0x01;
            if (ChkMode2.IsChecked == true) flags |= 0x02;
            if (ChkMode3.IsChecked == true) flags |= 0x04;
            if (ChkMode4.IsChecked == true) flags |= 0x08;
            if (ChkMode5.IsChecked == true) flags |= 0x10;
            if (ChkMode6.IsChecked == true) flags |= 0x20;
            if (ChkMode7.IsChecked == true) flags |= 0x40;
            if (ChkMode8.IsChecked == true) flags |= 0x80;
            return flags;
        }

        private void LvQueue_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) { TxtGameTitle.Text = job.Title; TxtGameId.Text = job.GameId; } }
        private void TxtGameTitle_LostFocus(object sender, RoutedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) job.Title = TxtGameTitle.Text; }
        private void TxtGameId_LostFocus(object sender, RoutedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) job.GameId = TxtGameId.Text; }
        private void BtnClearQueue_Click(object sender, RoutedEventArgs e) { GameQueue.Clear(); TxtGameTitle.Text = ""; TxtGameId.Text = ""; UpdateQueueStatusHeader(); LogWrite(">> MATRIX STACK PURGED."); }
        private void UpdateQueueStatusHeader() => TxtQueueStatus.Text = $"QUEUE: {GameQueue.Count} GAMES LOADED";

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
