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
        private string _title = string.Empty;
        private string _gameId = string.Empty;
        private string _sizeText = "--";
        private bool _isDuplicate;
        public bool IsDuplicate { get => _isDuplicate; set { _isDuplicate = value; OnPropertyChanged(); } }

        public string IsoPath { get; set; } = string.Empty;
        public string Status { get => _status; set { _status = value ?? string.Empty; OnPropertyChanged(); } }
        public string Title { get => _title; set { _title = value ?? string.Empty; OnPropertyChanged(); } }
        public string GameId { get => _gameId; set { _gameId = value ?? string.Empty; OnPropertyChanged(); } }
        public string SizeText { get => _sizeText; set { _sizeText = value ?? string.Empty; OnPropertyChanged(); } }

        public string PartName { get; set; } = string.Empty;
        public uint Sectors { get; set; }
        public byte CompatFlags { get; set; }
        public byte TrType { get; set; }
        public byte TrMode { get; set; }
        public byte DiscType { get; set; }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "--";
            double mb = bytes / (1024.0 * 1024.0);
            return mb >= 1024.0 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
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

        private void SetConsoleOperationControlsEnabled(bool enabled)
        {
            // Vergrendel nooit MainTabs zelf (dat maakt WPF TabControl wit!), maar alleen de PS2-disk knoppen:
            BtnConnect.IsEnabled = enabled;
            BtnPowerOff.IsEnabled = enabled;
            BtnReloadGames.IsEnabled = enabled && _streamer.IsConnected;
            BtnBackupGame.IsEnabled = enabled && _streamer.IsConnected;
            BtnDeleteGame.IsEnabled = enabled && _streamer.IsConnected;
            BtnSaveGameSettings.IsEnabled = enabled && _streamer.IsConnected && LvPs2Disk.SelectedItem != null;
            ChkOverwriteDuplicates.IsEnabled = enabled;
        }

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
                TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
                LogScrollViewer.ScrollToEnd();
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
                foreach (var g in gamesOnHdd)
                {
                    if (g.Sectors > 0)
                        g.SizeText = Ps2GameJob.FormatBytes((long)g.Sectors * 2048L);
                    InstalledGames.Add(g);
                }
                RefreshDuplicateHighlights();
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
                    long fileBytes = new FileInfo(selectedIso).Length;
                    string sizeFormatted = Ps2GameJob.FormatBytes(fileBytes);

                    GameQueue.Add(new Ps2GameJob { IsoPath = selectedIso, Title = title, GameId = id, SizeText = sizeFormatted, Status = "QUEUED" });
                    LogWrite($">> NODE REGISTERED: [{id}] {title} ({sizeFormatted})");
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

        private void LvQueue_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] droppedItems = (string[])e.Data.GetData(DataFormats.FileDrop);
                int addedCount = 0;

                foreach (string item in droppedItems)
                {
                    // Ondersteunt zowel losse .iso bestanden als volledige mappen met .iso bestanden!
                    string[] filesToProcess = Directory.Exists(item)
                        ? Directory.GetFiles(item, "*.iso", SearchOption.AllDirectories)
                        : new[] { item };

                    foreach (string file in filesToProcess)
                    {
                        if (Path.GetExtension(file).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                        {
                            string title = Path.GetFileNameWithoutExtension(file);
                            string id = _streamer.ExtractGameId(file) ?? "UNKNOWN_ID";
                            long fileBytes = new FileInfo(file).Length;
                            string sizeFormatted = Ps2GameJob.FormatBytes(fileBytes);

                            GameQueue.Add(new Ps2GameJob { IsoPath = file, Title = title, GameId = id, SizeText = sizeFormatted, Status = "QUEUED" });
                            LogWrite($">> DRAG & DROP REGISTERED: [{id}] {title} ({sizeFormatted})");
                            addedCount++;
                        }
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

        private void RefreshDuplicateHighlights()
        {
            int dupCount = 0;
            foreach (var job in GameQueue)
            {
                bool isDup = FindInstalledDuplicate(job) != null;
                job.IsDuplicate = isDup;
                if (isDup) dupCount++;
            }

            if (dupCount > 0)
            {
                LogWrite($">> [COMPARE] {dupCount} duplicate game(s) detected against PS2 HDD (marked with red border).");
            }
        }


        private Ps2GameJob? FindInstalledDuplicate(Ps2GameJob candidate)
        {
            foreach (var installed in InstalledGames)
            {
                bool sameId = !string.IsNullOrWhiteSpace(candidate.GameId) &&
                              candidate.GameId != "UNKNOWN_ID" &&
                              string.Equals(installed.GameId?.Trim(), candidate.GameId.Trim(), StringComparison.OrdinalIgnoreCase);

                bool sameTitle = !string.IsNullOrWhiteSpace(candidate.Title) &&
                                 string.Equals(installed.Title?.Trim(), candidate.Title.Trim(), StringComparison.OrdinalIgnoreCase);

                if (sameId || sameTitle) return installed;
            }
            return null;
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip) || GameQueue.Count == 0) { MessageBox.Show("Geen doel-IP of wachtrij aanwezig."); return; }

            bool overwriteDuplicates = ChkOverwriteDuplicates.IsChecked == true;
            _stopBatchRequested = false;

            BtnInstall.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            BtnClearQueue.IsEnabled = false;
            BtnStopBatch.IsEnabled = true;
            BtnStopBatch.Content = "[ ⏹ ] SAFE STOP";
            SetConsoleOperationControlsEnabled(false);

            try
            {
                LogWrite($">> INITIATING BATCH STREAM (OVERWRITE MODE: {(overwriteDuplicates ? "ON" : "OFF / SKIP DUPLICATES")})...");

                for (int i = 0; i < GameQueue.Count; i++)
                {
                    if (_stopBatchRequested)
                    {
                        LogWrite(">> [PAUSED] Batch netjes gepauzeerd op verzoek van operator. Geen corrupte data!");
                        StatusText.Text = ">> BATCH SAFELY PAUSED. READY TO RESUME.";
                        break;
                    }

                    var job = GameQueue[i];
                    if (job.Status == "DONE" || job.Status == "SKIPPED") continue;

                    // Controleer of de game al op de PS2 staat
                    var existingGame = FindInstalledDuplicate(job);
                    if (existingGame != null)
                    {
                        if (!overwriteDuplicates)
                        {
                            job.Status = "SKIPPED";
                            LogWrite($">> [SKIPPED] [{job.GameId}] {job.Title} ({job.SizeText}) is already installed on PS2 ({existingGame.SizeText}).");
                            continue;
                        }

                        LogWrite($">> [OVERWRITE] Removing existing PS2 partition: [{existingGame.GameId}] {existingGame.Title} ({existingGame.SizeText})...");
                        bool removed = await Task.Run(() => _streamer.DeleteGame(existingGame.PartName));
                        if (removed)
                        {
                            InstalledGames.Remove(existingGame);
                            LogWrite(">> [OVERWRITE] Old partition removed. Streaming fresh ISO...");
                        }
                        else
                        {
                            job.Status = "FAILED";
                            LogWrite($">> [ERROR] Could not remove existing partition for {existingGame.Title}.");
                            continue;
                        }
                    }

                    job.Status = "UPLOADING...";
                    UpdateProgressUi(0, 0, "CALCULATING...");
                    StatusText.Text = $">> INJECTING [{i + 1}/{GameQueue.Count}]: {job.Title}";
                    LogWrite($">> START STREAM NODE [{i + 1}/{GameQueue.Count}]: {job.Title} ({job.GameId}) [{job.SizeText}]");

                    bool success = await Task.Run(() => _streamer.UploadIso(ip, job));

                    if (success)
                    {
                        job.Status = "DONE";
                        LogWrite($">> SUCCESS: Node {job.Title} ({job.SizeText}) transferred.");
                    }
                    else
                    {
                        job.Status = "FAILED";
                        LogWrite($">> CRITICAL INTERRUPT: Transfer failed for {job.Title}.");
                        if (MessageBox.Show($"Stream afgebroken bij '{job.Title}'. Wilt u doorgaan met de rest?", "Stream Fout", MessageBoxButton.YesNo) == MessageBoxResult.No) break;
                    }
                }

                if (!_stopBatchRequested)
                {
                    LogWrite(">> BATCH PROCESS TERMINATED.");
                    StatusText.Text = ">> BATCH STREAM PROCESS COMPLETED.";
                    UpdateProgressUi(100, 0, "00:00:00");
                }
            }
            catch (Exception ex)
            {
                LogWrite($">> [ERROR] Batch stopped unexpectedly: {ex.Message}");
                StatusText.Text = ">> BATCH STOPPED DUE TO AN UNEXPECTED ERROR.";
                MessageBox.Show($"De batch kon niet worden afgerond: {ex.Message}", "Batch fout", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStopBatch.IsEnabled = false;
                BtnStopBatch.Content = "[ ⏹ ] SAFE STOP";
                BtnInstall.IsEnabled = true;
                BtnBrowse.IsEnabled = true;
                BtnClearQueue.IsEnabled = true;
                SetConsoleOperationControlsEnabled(true);
                UpdateQueueStatusHeader();
            }
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
            foreach (var g in gamesOnHdd)
            {
                if (g.Sectors > 0)
                    g.SizeText = Ps2GameJob.FormatBytes((long)g.Sectors * 2048L);
                InstalledGames.Add(g);
            }
            RefreshDuplicateHighlights();
            StatusText.Text = $">> HDD SCAN COMPLETE: {InstalledGames.Count} GAMES FOUND.";
            BtnReloadGames.IsEnabled = true;
            BtnDeleteGame.IsEnabled = InstalledGames.Count > 0;
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
        private void BtnRemoveSelectedQueue_Click(object sender, RoutedEventArgs e)
        {
            if (LvQueue.SelectedItem is not Ps2GameJob selectedJob)
            {
                MessageBox.Show("Selecteer eerst een game in de wachtrij om te verwijderen.", "Geen selectie");
                return;
            }

            if (selectedJob.Status == "UPLOADING...")
            {
                MessageBox.Show("Je kunt een game niet verwijderen terwijl deze actief wordt geüpload.", "Upload actief");
                return;
            }

            GameQueue.Remove(selectedJob);
            TxtGameTitle.Text = "";
            TxtGameId.Text = "";
            UpdateQueueStatusHeader();
            LogWrite($">> [QUEUE] Removed selected node: [{selectedJob.GameId}] {selectedJob.Title}");
        }

        private void BtnRemoveDuplicates_Click(object sender, RoutedEventArgs e)
        {
            int removedCount = 0;

            for (int i = GameQueue.Count - 1; i >= 0; i--)
            {
                var job = GameQueue[i];
                if ((job.IsDuplicate || job.Status == "SKIPPED") && job.Status != "UPLOADING...")
                {
                    GameQueue.RemoveAt(i);
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                TxtGameTitle.Text = "";
                TxtGameId.Text = "";
                UpdateQueueStatusHeader();
                LogWrite($">> [CLEANUP] Removed {removedCount} duplicate game(s) from the batch queue.");
            }
            else
            {
                MessageBox.Show("Er staan op dit moment geen duplicaten in de wachtrij.", "Geen duplicaten");
            }
        }
        private void UpdateQueueStatusHeader()
        {
            TxtQueueStatus.Text = $"QUEUE: {GameQueue.Count} GAMES LOADED";
            RefreshDuplicateHighlights();
        }


        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnInstall.IsEnabled)
            {
                bool forceExit = ConfirmDialog.Ask(
                    this,
                    "// ACTIVE STREAM WARNING",
                    "An ISO transfer is currently in progress!\n\nExiting now may corrupt the active partition on your PS2 HDD. Use 'SAFE STOP' to finish cleanly.\n\nForce exit anyway?",
                    isWarning: true);

                if (!forceExit) return;
            }
            else
            {
                bool confirm = ConfirmDialog.Ask(
                    this,
                    "// SYSTEM EXIT CONFIRMATION",
                    "Are you sure ?",
                    isWarning: false);

                if (!confirm) return;
            }

            _streamer.Disconnect();
            this.Close();
        }

    }
}
