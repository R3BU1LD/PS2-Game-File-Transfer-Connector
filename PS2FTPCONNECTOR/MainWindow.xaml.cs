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

        private readonly HdlStreamer _streamer = new HdlStreamer();
        private readonly string _autoLogPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Logs",
            $"PS2FTP_Session_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
        private readonly object _logFileLock = new object();
        private volatile bool _stopBatchRequested = false;

        private void SetConsoleOperationControlsEnabled(bool enabled)
        {
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

            _streamer.OnLog += LogWrite;
            _streamer.OnProgress += UpdateProgressUi;

            LogWrite(">> SYSTEM INITIATED. AWAITING OPERATOR...");
        }

        private void LogWrite(string text)
        {
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}";
            try
            {
                lock (_logFileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_autoLogPath)!);
                    File.AppendAllText(_autoLogPath, logLine + Environment.NewLine);
                }
            }
            catch { }

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
            BtnStopBatch.Content = Lang.T("[ ⏳ ] STOPPING...", "[ ⏳ ] STOPPEN...");
            StatusText.Text = Lang.T(">> SAFE STOP REQUESTED: FINISHING CURRENT ISO BEFORE HALTING...", ">> VEILIGE STOP AANGEVRAAGD: HUIDIGE ISO WORDT NETJES AFGEMAAKT...");
            LogWrite(">> [OPERATOR] Safe Stop requested. Queue will halt once the active game finishes!");
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
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

                BtnConnect.Content = Lang.T("CONNECT", "VERBINDEN");
                BtnConnect.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));
                BtnConnect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));

                StatusText.Text = Lang.T(">> CONNECTION CLOSED. PS2 PORT RELEASED.", ">> VERBINDING VERBROKEN. PS2-POORT VRIJGEGEVEN.");
                LogWrite(">> DISCONNECTED FROM PS2. Console returned to idle state.");
                return;
            }

            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                ConfirmDialog.Notify(this,
                    Lang.T("// INVALID IP ADDRESS", "// ONGELDIG IP-ADRES"),
                    Lang.T("Please enter a valid PlayStation 2 IP address.", "Voer eerst een geldig PlayStation 2 IP-adres in."),
                    isWarning: true);
                return;
            }

            BtnConnect.IsEnabled = false;
            LogWrite($">> ATTEMPTING HANDSHAKE WITH HDL NODE AT {ip}...");
            StatusText.Text = Lang.T(">> CONNECTING TO HDL GAME INSTALLER SERVER...", ">> VERBINDEN MET HDL GAME INSTALLER SERVER...");
            InstalledGames.Clear();

            bool isServerAlive = await Task.Run(() => _streamer.ConnectAndHandshake(ip));

            BtnConnect.IsEnabled = true;
            if (isServerAlive)
            {
                TxtNoConnection.Visibility = Visibility.Collapsed;
                TxtIpAddress.IsEnabled = false;
                BtnReloadGames.IsEnabled = true;
                StatusText.Text = Lang.T(">> CONNECTION ESTABLISHED TO HDL NODE!", ">> VERBINDING TOT STAND GEBRACHT MET PS2!");
                LogWrite($">> SUCCESS: Connected to HDLGameInstaller server on {ip}:45061.");

                BtnConnect.Content = Lang.T("DISCONNECT", "VERBREKEN");
                BtnConnect.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 60, 60));
                BtnConnect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 60, 60));

                var gamesOnHdd = await Task.Run(() => _streamer.LoadInstalledGames());
                foreach (var g in gamesOnHdd)
                {
                    if (g.Sectors > 0) g.SizeText = Ps2GameJob.FormatBytes((long)g.Sectors * 2048L);
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
                StatusText.Text = Lang.T(">> CONNECTION REFUSED ON PORT 45061.", ">> VERBINDING GEWEIGERD OP POORT 45061.");
                LogWrite(">> ERROR: Connection refused. Check if HDLGameInstaller is running on the PS2.");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _streamer.Disconnect();
            base.OnClosed(e);
        }
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "PS2 ISO Files (*.iso)|*.iso", Multiselect = true };
            if (openFileDialog.ShowDialog() == true)
            {
                LogWrite($">> LOADING DATASET: {openFileDialog.FileNames.Length} ISO file(s) selected.");
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
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
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
                    UpdateQueueStatusHeader();
                else
                    LogWrite(">> [WARN] No valid .iso files recognized when using Drag & Drop.");
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
                LogWrite($">> [COMPARE] {dupCount} duplicate game(s) detected against PS2 HDD (marked with red border).");
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
            if (string.IsNullOrEmpty(ip) || GameQueue.Count == 0)
            {
                ConfirmDialog.Notify(this,
                    Lang.T("// BATCH NOT READY", "// BATCH NIET GEREED"),
                    Lang.T("Please enter a valid PS2 IP address and add at least one ISO to the queue.",
                           "Voer een geldig PS2 IP-adres in en voeg minimaal één ISO toe aan de wachtrij."),
                    isWarning: true);
                return;
            }

            bool overwriteDuplicates = ChkOverwriteDuplicates.IsChecked == true;
            _stopBatchRequested = false;

            BtnInstall.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            BtnClearQueue.IsEnabled = false;
            BtnStopBatch.IsEnabled = true;
            BtnStopBatch.Content = Lang.T("[ ⏹ ] SAFE STOP", "[ ⏹ ] VEILIG STOPPEN");
            SetConsoleOperationControlsEnabled(false);

            try
            {
                LogWrite($">> INITIATING BATCH STREAM (OVERWRITE MODE: {(overwriteDuplicates ? "ON" : "OFF / SKIP DUPLICATES")})...");

                for (int i = 0; i < GameQueue.Count; i++)
                {
                    if (_stopBatchRequested)
                    {
                        LogWrite(">> [PAUSED] Batch neatly paused at the operator's request. No corrupted data!");
                        StatusText.Text = Lang.T(">> BATCH SAFELY PAUSED. READY TO RESUME.", ">> BATCH VEILIG GEPAUZEERD. KLAAR OM TE HERVATTEN.");
                        break;
                    }

                    var job = GameQueue[i];
                    if (job.Status == "DONE" || job.Status == "SKIPPED") continue;

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
                        if (!ConfirmDialog.Ask(this,
                            Lang.T("// STREAM INTERRUPTED", "// STREAM ONDERBROKEN"),
                            Lang.T($"Stream interrupted for '{job.Title}'.\n\nDo you want to continue with the remaining games in the queue?",
                                   $"Overdracht onderbroken voor '{job.Title}'.\n\nWil je doorgaan met de resterende games in de wachtrij?"),
                            isWarning: true)) break;
                    }
                }

                if (!_stopBatchRequested)
                {
                    LogWrite(">> BATCH PROCESS TERMINATED.");
                    StatusText.Text = Lang.T(">> BATCH STREAM PROCESS COMPLETED.", ">> BATCH OVERDRACHT VOLTOOID.");
                    UpdateProgressUi(100, 0, "00:00:00");
                }
            }
            catch (Exception ex)
            {
                LogWrite($">> [ERROR] Batch stopped unexpectedly: {ex.Message}");
                StatusText.Text = Lang.T(">> BATCH STOPPED DUE TO AN UNEXPECTED ERROR.", ">> BATCH GESTOPT DOOR EEN ONVERWACHTE FOUT.");
                ConfirmDialog.Notify(this,
                    Lang.T("// BATCH STREAM ERROR", "// BATCH STREAM FOUT"),
                    Lang.T($"The batch could not be completed:\n{ex.Message}", $"De batch kon niet worden voltooid:\n{ex.Message}"),
                    isWarning: true);
            }
            finally
            {
                BtnStopBatch.IsEnabled = false;
                BtnStopBatch.Content = Lang.T("[ ⏹ ] SAFE STOP", "[ ⏹ ] VEILIG STOPPEN");
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
                ConfirmDialog.Notify(this,
                    Lang.T("// NO CONNECTION", "// GEEN VERBINDING"),
                    Lang.T("Connect to the PS2 first before refreshing the list.",
                           "Maak eerst verbinding met de PS2 voordat je de lijst ververst."));
                return;
            }

            TxtFreeSpace.Text = "PS2 FREE SPACE: " + await Task.Run(() => _streamer.GetFreeSpaceFormatted());
            BtnReloadGames.IsEnabled = false;
            BtnDeleteGame.IsEnabled = false;
            LogWrite(">> REFRESHING INSTALLED GAMES LIST FROM PS2 HDD...");
            StatusText.Text = Lang.T(">> SCANNING CONSOLE HARD DRIVE...", ">> CONSOLE HARDE SCHIJF SCANNEN...");
            InstalledGames.Clear();

            var gamesOnHdd = await Task.Run(() => _streamer.LoadInstalledGames());
            foreach (var g in gamesOnHdd)
            {
                if (g.Sectors > 0) g.SizeText = Ps2GameJob.FormatBytes((long)g.Sectors * 2048L);
                InstalledGames.Add(g);
            }
            RefreshDuplicateHighlights();
            StatusText.Text = Lang.T($">> HDD SCAN COMPLETE: {InstalledGames.Count} GAMES FOUND.",
                                     $">> HDD SCAN VOLTOOID: {InstalledGames.Count} GAMES GEVONDEN.");
            BtnReloadGames.IsEnabled = true;
            BtnDeleteGame.IsEnabled = InstalledGames.Count > 0;
        }

        private async void BtnDeleteGame_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected) return;

            if (LvPs2Disk.SelectedItem is not Ps2GameJob selectedGame)
            {
                ConfirmDialog.Notify(this,
                    Lang.T("// NO SELECTION", "// GEEN SELECTIE"),
                    Lang.T("Please select a game in the list first to remove it.",
                           "Selecteer eerst een game in de lijst om deze te verwijderen."));
                return;
            }

            bool confirm = ConfirmDialog.Ask(
                this,
                Lang.T("// DELETE PARTITION WARNING", "// WAARSCHUWING: PARTITIE VERWIJDEREN"),
                Lang.T($"Permanently delete '{selectedGame.Title}' ({selectedGame.GameId}) [{selectedGame.SizeText}] from your PlayStation 2 hard drive?\n\nThis action cannot be undone!",
                       $"Weet je zeker dat je '{selectedGame.Title}' ({selectedGame.GameId}) [{selectedGame.SizeText}] definitief wilt verwijderen van je PlayStation 2 harde schijf?\n\nDeze actie kan niet ongedaan worden gemaakt!"),
                isWarning: true);
            if (!confirm) return;

            BtnDeleteGame.IsEnabled = false;
            BtnReloadGames.IsEnabled = false;
            LogWrite($">> DELETING PARTITION [{selectedGame.PartName}]: {selectedGame.Title}...");

            bool deleted = await Task.Run(() => _streamer.DeleteGame(selectedGame.PartName));

            if (deleted)
            {
                InstalledGames.Remove(selectedGame);
                LogWrite($">> SUCCESS: '{selectedGame.Title}' removed from the PS2 HDD.");
                TxtFreeSpace.Text = "PS2 FREE SPACE: " + await Task.Run(() => _streamer.GetFreeSpaceFormatted());
            }
            else
            {
                LogWrite($">> ERROR: Removing '{selectedGame.Title}' failed.");
                ConfirmDialog.Notify(this,
                    Lang.T("// GAME DELETION ERROR", "// FOUT BIJ VERWIJDEREN"),
                    Lang.T("Unable to remove the game from the PS2.",
                           "Kon de game niet verwijderen van de PS2."),
                    isWarning: true);
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
                ConfirmDialog.Notify(this,
                    Lang.T("// NO BACKUP SELECTION", "// GEEN BACKUP-SELECTIE"),
                    Lang.T("Please select an installed game from the PS2 list first before starting a backup.",
                           "Selecteer eerst een geïnstalleerde game uit de PS2-lijst voordat je een backup start."));
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
                LogWrite($">> BACKUP COMPLETED: Saved as {saveDialog.FileName}");
                StatusText.Text = Lang.T(">> BACKUP STREAM COMPLETED.", ">> BACKUP OVERDRACHT VOLTOOID.");
                UpdateProgressUi(100, 0, "00:00:00");
            }
            else
            {
                LogWrite(">> [ERROR] Backup aborted or failed.");
                StatusText.Text = Lang.T(">> BACKUP FAILED.", ">> BACKUP MISLUKT.");
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
                ConfirmDialog.Notify(this,
                    Lang.T("// CONSOLE OFFLINE", "// CONSOLE OFFLINE"),
                    Lang.T("Please connect to your PlayStation 2 console first before sending a power-off command.",
                           "Maak eerst verbinding met je PlayStation 2 console voordat je een uitschakelcommando stuurt."));
                return;
            }

            bool powerConfirm = ConfirmDialog.Ask(
                this,
                Lang.T("// CONSOLE SHUTDOWN", "// CONSOLE UITSCHAKELEN"),
                Lang.T("Are you sure you want to send the remote power-off command to your PlayStation 2?",
                       "Weet je zeker dat je het uitschakelcommando naar je PlayStation 2 wilt sturen?"),
                isWarning: false);

            if (powerConfirm && _streamer.PowerOffPs2())
            {
                InstalledGames.Clear();
                TxtNoConnection.Visibility = Visibility.Visible;
                TxtIpAddress.IsEnabled = true;
                BtnReloadGames.IsEnabled = false;
                BtnDeleteGame.IsEnabled = false;
                TxtFreeSpace.Text = "PS2 FREE SPACE: -- GB";

                BtnConnect.Content = Lang.T("CONNECT", "VERBINDEN");
                BtnConnect.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));
                BtnConnect.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 102));

                LogWrite(">> POWER-OFF SIGNAL SENT! PS2 is shutting down.");
                StatusText.Text = Lang.T(">> CONSOLE POWERED OFF.", ">> CONSOLE UITGESCHAKELD.");
            }
        }

        private async void BtnSaveGameSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!_streamer.IsConnected || LvPs2Disk.SelectedItem is not Ps2GameJob selectedGame)
            {
                ConfirmDialog.Notify(this,
                    Lang.T("// NO CONSOLE SELECTION", "// GEEN CONSOLE-SELECTIE"),
                    Lang.T("Please select an installed game while connected to the PS2 console.",
                           "Selecteer eerst een geïnstalleerde game terwijl je verbonden bent met de PS2."));
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
                ConfirmDialog.Notify(this,
                    Lang.T("// SETTINGS SYNC ERROR", "// FOUT BIJ OPSLAAN INSTELLINGEN"),
                    Lang.T("Could not save game compatibility settings to the PS2 hard drive.","Kon de compatibiliteitsinstellingen niet opslaan op de PS2 harde schijf."),
                    isWarning: true);
            }
            BtnSaveGameSettings.IsEnabled = _streamer.IsConnected && LvPs2Disk.SelectedItem == selectedGame;
        }
        private void BtnSaveLog_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveDialog = new SaveFileDialog
            {
                Filter = "Log Files(*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
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
                        sb.AppendLine("--- BATCH QUEUE STATUS OVERVIEW ---");
                        foreach (var job in GameQueue)
                            sb.AppendLine($"[{job.Status,-12}] [{job.GameId}] {job.Title}");
                        sb.AppendLine();
                    }

                    sb.AppendLine("--- FULL TERMINAL LOG ---");
                    sb.AppendLine(TxtLog.Text);

                    File.WriteAllText(saveDialog.FileName, sb.ToString());
                    LogWrite($">> LOG SAVED MANUALLY TO: {saveDialog.FileName}");
                }
                catch (Exception ex)
                {
                    ConfirmDialog.Notify(this,
                        Lang.T("// LOG EXPORT ERROR", "// FOUT BIJ EXPORTEREN LOG"),
                        Lang.T($"Could not save log file:\n{ex.Message}", $"Kon het logbestand niet opslaan:\n{ex.Message}"),
                        isWarning: true);
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

        private void LvQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvQueue.SelectedItem is Ps2GameJob job)
            {
                TxtGameTitle.Text = job.Title;
                TxtGameId.Text = job.GameId;
            }
        }

        private void TxtGameTitle_LostFocus(object sender, RoutedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) job.Title = TxtGameTitle.Text; }
        private void TxtGameId_LostFocus(object sender, RoutedEventArgs e) { if (LvQueue.SelectedItem is Ps2GameJob job) job.GameId = TxtGameId.Text; }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            GameQueue.Clear();
            TxtGameTitle.Text = "";
            TxtGameId.Text = "";
            UpdateQueueStatusHeader();
            LogWrite(">> MATRIX STACK PURGED.");
        }

        private void BtnRemoveSelectedQueue_Click(object sender, RoutedEventArgs e)
        {
            if (LvQueue.SelectedItem is not Ps2GameJob selectedJob)
            {
                ConfirmDialog.Notify(this,
                    Lang.T("// NO QUEUE SELECTION", "// GEEN WACHTRIJ-SELECTIE"),
                    Lang.T("Please select a game in the batch queue first to remove it.",
                           "Selecteer eerst een game in de wachtrij om deze te verwijderen."));
                return;
            }

            if (selectedJob.Status == "UPLOADING...")
            {
                ConfirmDialog.Notify(this,
                    Lang.T("// ACTIVE STREAM LOCK", "// ACTIEVE OVERDRACHT"),
                    Lang.T("Cannot remove a game while it is actively uploading to the PS2.",
                           "Kan een game niet verwijderen terwijl deze actief naar de PS2 wordt geüpload."),
                    isWarning: true);
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
                ConfirmDialog.Notify(this,
                    Lang.T("// QUEUE STATUS", "// WACHTRIJ STATUS"),
                    Lang.T("There are currently no duplicates in the batch queue.",
                           "Er staan momenteel geen duplicaten in de wachtrij."));
            }
        }

        private void UpdateQueueStatusHeader()
        {
            TxtQueueStatus.Text = $"QUEUE: {GameQueue.Count} GAMES LOADED";
            RefreshDuplicateHighlights();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        private void BtnMaximizeRestore_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
                BtnMaximizeRestore.Content = "□";
            }
            else
            {
                this.MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
                this.MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
                this.WindowState = WindowState.Maximized;
                BtnMaximizeRestore.Content = "❐";
            }
        }

        private void BtnToggleLang_Click(object sender, RoutedEventArgs e)
        {
            Lang.IsDutch = !Lang.IsDutch;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            BtnToggleLang.Content = Lang.IsDutch ? "🌐 NL" : "🌐 EN";

            // Connection & Left Control Panel
            BtnConnect.Content = _streamer.IsConnected
                ? Lang.T("DISCONNECT", "VERBREKEN")
                : Lang.T("CONNECT", "VERBINDEN");
            BtnBrowse.Content = Lang.T("[ + ] SELECT ISO(S)", "[ + ] SELECTEER ISO('S)");
            ChkOverwriteDuplicates.Content = Lang.T("OVERWRITE EXISTING GAMES ON PS2", "BESTAANDE GAMES OVERSCHRIJVEN OP PS2");
            BtnInstall.Content = Lang.T("START BATCH UPLOAD", "START BATCH UPLOAD");
            BtnStopBatch.Content = Lang.T("[ ⏹ ] SAFE STOP", "[ ⏹ ] VEILIG STOPPEN");
            BtnClearQueue.Content = Lang.T("CLEAR QUEUE", "WACHTRIJ WISSEN");
            BtnRemoveSelectedQueue.Content = Lang.T("REMOVE SELECTED", "VERWIJDER SELECTIE");
            BtnRemoveDuplicates.Content = Lang.T("[ ✕ ] REMOVE DUPLICATES", "[ ✕ ] VERWIJDER DUPLICATEN");
            BtnPowerOff.Content = Lang.T("[ ⏻ ] POWER OFF PS2", "[ ⏻ ] PS2 UITSCHAKELEN");
            BtnSaveLog.Content = Lang.T("SAVE LOG AS...", "LOG OPSLAAN ALS...");

            // Tabs
            if (MainTabs.Items.Count >= 2)
            {
                if (MainTabs.Items[0] is TabItem tab0)
                    tab0.Header = Lang.T(" ▶ BATCH UPLOAD QUEUE  ", " ▶ BATCH UPLOAD WACHTRIJ  ");
                if (MainTabs.Items[1] is TabItem tab1)
                    tab1.Header = Lang.T(" 💾 INSTALLED GAMES ON PS2 HDD  ", " 💾 GEÏNSTALLEERDE GAMES OP PS2 HDD  ");
            }

            // Tab 2: Installed Games Actions & Settings
            BtnReloadGames.Content = Lang.T("[ ↻ ] REFRESH LIST", "[ ↻ ] LIJST VERVERSEN");
            BtnBackupGame.Content = Lang.T("[ 💾 ] BACKUP TO PC (.ISO)", "[ 💾 ] BACKUP NAAR PC (.ISO)");
            BtnDeleteGame.Content = Lang.T("[ 🗑 ] DELETE FROM PS2", "[ 🗑 ] VERWIJDER VAN PS2");
            BtnSaveGameSettings.Content = Lang.T("[ ✔ ] SAVE CHANGES TO PS2", "[ ✔ ] WIJZIGINGEN OPSLAAN");

            
            // Tooltips on top-right window controls
            BtnMinimize.ToolTip = Lang.T("Minimize Window", "Minimaliseren");
            BtnMaximizeRestore.ToolTip = Lang.T("Maximize / Restore Window", "Maximaliseren / Vorige grootte");
            BtnTopClose.ToolTip = Lang.T("Safe Exit", "Veilig afsluiten");
            LogWrite(Lang.T(">> [LANG] Interface switched to ENGLISH.", ">> [TAAL] Interface omgezet naar NEDERLANDS."));
        }
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnInstall.IsEnabled)
            {
                bool forceExit = ConfirmDialog.Ask(
                this,
                Lang.T("// ACTIVE STREAM WARNING", "// WAARSCHUWING: ACTIEVE STREAM"),
                Lang.T("An ISO transfer is currently in progress!\n\nExiting now may corrupt the active partition on your PS2 HDD. Use 'SAFE STOP' to finish cleanly.\n\nForce exit anyway?",
                "Er is momenteel een ISO-overdracht bezig!\n\nNu afsluiten kan de actieve partitie op je PS2 HDD beschadigen. Gebruik 'VEILIG STOPPEN' om netjes af te ronden.\n\nToch geforceerd afsluiten?"),
                isWarning: true);
                if (!forceExit) return;
            }
            else
            {
                bool confirm = ConfirmDialog.Ask(
                this,
                Lang.T("// SYSTEM EXIT CONFIRMATION", "// SYSTEEM AFSLUITEN"),
                Lang.T("Are you sure you want to exit ?\nThe active PS2 network connection will be safely released.",
                "Weet je zeker dat je wilt afsluiten?\nDe actieve PS2-netwerkverbinding wordt veilig vrijgegeven."),
                isWarning: false);
                if (!confirm) return;
            }
            _streamer.Disconnect();
            this.Close();
        }
    }
}

