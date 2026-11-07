using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PS2FTPCONNECTOR
{
    // Model voor de Wachtrij
    public class Ps2GameJob : INotifyPropertyChanged
    {
        private string _status = "QUEUED";
        private string _title;
        private string _gameId;

        public string IsoPath { get; set; }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }
        public string GameId
        {
            get => _gameId;
            set { _gameId = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class MainWindow : Window
    {
        // De dynamische lijst gekoppeld aan de ListView
        public ObservableCollection<Ps2GameJob> GameQueue { get; set; } = new ObservableCollection<Ps2GameJob>();

        public MainWindow()
        {
            InitializeComponent();
            LvQueue.ItemsSource = GameQueue;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "PS2 ISO Bestanden (*.iso)|*.iso",
                Title = "Selecteer een of meerdere PS2 Game ISO's",
                Multiselect = true // MULTI-SELECT INGESCHAKELD!
            };

            if (openFileDialog.ShowDialog() == true)
            {
                StatusText.Text = "Data-scans uitvoeren op geselecteerde ISO's...";

                foreach (string selectedIso in openFileDialog.FileNames)
                {
                    string suggestedTitle = Path.GetFileNameWithoutExtension(selectedIso);
                    string extractedId = ExtractGameIdFromIso(selectedIso) ?? "UNKNOWN_ID";

                    GameQueue.Add(new Ps2GameJob
                    {
                        IsoPath = selectedIso,
                        Title = suggestedTitle,
                        GameId = extractedId,
                        Status = "QUEUED"
                    });
                }

                UpdateQueueStatusHeader();
                StatusText.Text = "ISO's toegevoegd aan matrix stack.";
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtIpAddress.Text.Trim();
            if (string.IsNullOrEmpty(ip) || GameQueue.Count == 0)
            {
                MessageBox.Show("Zorg voor een geldig IP-adres en minimaal 1 game in de wachtrij.", "Systeemfout", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnInstall.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            BtnClearQueue.IsEnabled = false;

            // Loop chronologisch door alle games in de wachtrij heen
            for (int i = 0; i < GameQueue.Count; i++)
            {
                var job = GameQueue[i];

                // Sla over als hij al succesvol was
                if (job.Status == "DONE") continue;

                job.Status = "UPLOADING...";
                MyProgressBar.Value = 0;
                PercentageText.Text = "0%";
                StatusText.Text = $">> INJECTING [{i + 1}/{GameQueue.Count}]: {job.Title}";

                // Voer hdl_dump uit voor deze specifieke game
                bool success = await Task.Run(() => RunHdlDump(ip, job.IsoPath, job.Title, job.GameId));

                if (success)
                {
                    job.Status = "DONE";
                }
                else
                {
                    job.Status = "FAILED";
                    StatusText.Text = $">> INTERRUPT: Fout bij game: {job.Title}";
                    var result = MessageBox.Show($"De installatie van '{job.Title}' is mislukt. Wilt u doorgaan met de rest van de lijst?", "Batch Fout", MessageBoxButton.YesNo, MessageBoxImage.Error);
                    if (result == MessageBoxResult.No) break;
                }
            }

            StatusText.Text = ">> BATCH STREAM STREAM PROCESS COMPLETED.";
            MyProgressBar.Value = 100;
            PercentageText.Text = "100%";
            BtnInstall.IsEnabled = true;
            BtnBrowse.IsEnabled = true;
            BtnClearQueue.IsEnabled = true;
            UpdateQueueStatusHeader();
        }

        private bool RunHdlDump(string ip, string isoPath, string title, string gameId)
        {
            try
            {
                // Haal de totale grootte van het ISO-bestand op in bytes
                FileInfo isoInfo = new FileInfo(isoPath);
                double totalMegabytes = isoInfo.Length / (1024.0 * 1024.0);

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "hdl_dump.exe",
                    Arguments = $"inject_dvd {ip} \"{isoPath}\" \"{title}\" {gameId}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Start een timer om de snelheid te meten
                Stopwatch transferTimer = new Stopwatch();

                using (Process process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            var match = Regex.Match(e.Data, @"(\d+)%");
                            if (match.Success)
                            {
                                // Start de timer zodra de eerste data daadwerkelijk binnenkomt
                                if (!transferTimer.IsRunning) transferTimer.Start();

                                int percentage = int.Parse(match.Groups[1].Value);

                                // Berekeningen voor Snelheid en Tijd
                                double elapsedSeconds = transferTimer.Elapsed.TotalSeconds;
                                double speedMbPerSecond = 0;
                                string etaString = "CALCULATING...";

                                if (percentage > 0 && elapsedSeconds > 0.5)
                                {
                                    // Hoeveel MB is er nu verzonden?
                                    double mbTransferred = (totalMegabytes * percentage) / 100.0;

                                    // Actuele gemiddelde snelheid
                                    speedMbPerSecond = mbTransferred / elapsedSeconds;

                                    // Resterende MB's en resterende tijd berekenen
                                    double mbRemaining = totalMegabytes - mbTransferred;
                                    double secondsRemaining = mbRemaining / speedMbPerSecond;

                                    TimeSpan timeRemaining = TimeSpan.FromSeconds(secondsRemaining);
                                    etaString = string.Format("{0:D2}:{1:D2}:{2:D2}", timeRemaining.Hours, timeRemaining.Minutes, timeRemaining.Seconds);
                                }

                                // Update alle UI indicators veilig op de hoofdthread
                                Dispatcher.Invoke(() =>
                                {
                                    MyProgressBar.Value = percentage;
                                    PercentageText.Text = $"{percentage}%";

                                    // Toon de berekende snelheid en ETA
                                    TxtSpeed.Text = $"{speedMbPerSecond:F2} MB/s";
                                    TxtEta.Text = etaString;
                                });
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.WaitForExit();

                    transferTimer.Stop();

                    // Reset de statistieken na afronding van de game
                    Dispatcher.Invoke(() =>
                    {
                        TxtSpeed.Text = "0.00 MB/s";
                        TxtEta.Text = "00:00:00";
                    });

                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }


        // Update de live editor velden als je een item selecteert in de lijst
        private void LvQueue_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvQueue.SelectedItem is Ps2GameJob selectedJob)
            {
                TxtGameTitle.Text = selectedJob.Title;
                TxtGameId.Text = selectedJob.GameId;
            }
        }

        // Sla wijzigingen in de editor live op in de geselecteerde wachtrij-item
        private void TxtGameTitle_LostFocus(object sender, RoutedEventArgs e)
        {
            if (LvQueue.SelectedItem is Ps2GameJob selectedJob)
            {
                selectedJob.Title = TxtGameTitle.Text;
            }
        }

        private void TxtGameId_LostFocus(object sender, RoutedEventArgs e)
        {
            if (LvQueue.SelectedItem is Ps2GameJob selectedJob)
            {
                selectedJob.GameId = TxtGameId.Text;
            }
        }

        private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
        {
            GameQueue.Clear();
            TxtGameTitle.Text = "";
            TxtGameId.Text = "";
            UpdateQueueStatusHeader();
            StatusText.Text = ">> MATRIX STACK PURGED.";
        }

        private void UpdateQueueStatusHeader()
        {
            TxtQueueStatus.Text = $"QUEUE: {GameQueue.Count} GAMES LOADED";
        }

        private string ExtractGameIdFromIso(string isoPath)
        {
            try
            {
                using (FileStream fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[1024 * 1024 * 4];
                    int bytesRead = fs.Read(buffer, 0, buffer.Length);
                    string text = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                    var match = Regex.Match(text, @"(SLES|SLUS|SCUS|SCDH|SLPM|SLPS|SCPM)_[0-9]{3}\.[0-9]{2}");
                    if (match.Success) return match.Value;
                }
            }
            catch { }
            return null;
        }
    }
}
