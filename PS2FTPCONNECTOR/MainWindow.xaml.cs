using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace PS2FTPCONNECTOR
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Wordt uitgevoerd als je op de 'Bladeren...' knop klikt.
        /// </summary>
        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "PS2 ISO Bestanden (*.iso)|*.iso",
                Title = "Selecteer een PS2 Game ISO"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string selectedIso = openFileDialog.FileName;
                TxtIsoPath.Text = selectedIso;

                // Haal automatisch de bestandsnaam (zonder .iso) op als voorgestelde titel
                TxtGameTitle.Text = Path.GetFileNameWithoutExtension(selectedIso);

                // Probeer de Game ID (bijv. SLES_504.22) automatisch uit de ISO te vissen
                StatusText.Text = "Game ID scannen...";
                string gameId = ExtractGameIdFromIso(selectedIso);

                if (!string.IsNullOrEmpty(gameId))
                {
                    TxtGameId.Text = gameId;
                    StatusText.Text = "ISO succesvol geladen.";
                }
                else
                {
                    TxtGameId.Text = "";
                    StatusText.Text = "ISO geladen, maar Game ID niet gevonden. Vul deze handmatig in.";
                }
            }
        }

        /// <summary>
        /// Wordt uitgevoerd als je op 'Start Installatie' klikt.
        /// </summary>
        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            string ip = TxtIpAddress.Text.Trim();
            string isoPath = TxtIsoPath.Text.Trim();
            string title = TxtGameTitle.Text.Trim();
            string gameId = TxtGameId.Text.Trim();

            // Validatie van invoervelden
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(isoPath) || string.IsNullOrEmpty(title) || string.IsNullOrEmpty(gameId))
            {
                MessageBox.Show("Vul alstublieft alle velden in voordat u de installatie start.", "Invoer ontbreekt", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Knoppen uitschakelen tijdens installatie om dubbel klikken te voorkomen
            BtnInstall.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            StatusText.Text = "Verbinding maken en installatie starten...";
            MyProgressBar.Value = 0;
            PercentageText.Text = "0%";

            // Start de installatie op de achtergrond zodat de UI soepel blijft draaien
            await Task.Run(() => RunHdlDump(ip, isoPath, title, gameId));

            // Knoppen weer inschakelen na voltooiing
            BtnInstall.IsEnabled = true;
            BtnBrowse.IsEnabled = true;
        }

        /// <summary>
        /// Stuurt hdl_dump aan en leest de progressie uit via de console-output.
        /// </summary>
        private void RunHdlDump(string ip, string isoPath, string title, string gameId)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "hdl_dump.exe", // Zorg dat hdl_dump.exe in de build-map (bin/Debug of bin/Release) staat!
                    Arguments = $"inject_dvd {ip} \"{isoPath}\" \"{title}\" {gameId}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            // Zoek naar een percentage in de output (bijv: "45%")
                            var match = Regex.Match(e.Data, @"(\d+)%");
                            if (match.Success)
                            {
                                int percentage = int.Parse(match.Groups[1].Value);

                                // Update de WPF UI elementen veilig vanaf de achtergrond-thread
                                Dispatcher.Invoke(() =>
                                {
                                    MyProgressBar.Value = percentage;
                                    PercentageText.Text = $"{percentage}%";
                                    StatusText.Text = $"Bezig met overdragen van data...";
                                });
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.WaitForExit();

                    // Afhandeling op basis van hoe het proces is geëindigd
                    Dispatcher.Invoke(() =>
                    {
                        if (process.ExitCode == 0)
                        {
                            MyProgressBar.Value = 100;
                            PercentageText.Text = "100%";
                            StatusText.Text = "Installatie succesvol voltooid!";
                            MessageBox.Show("De game is succesvol op de PS2 geïnstalleerd!", "Succes", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            StatusText.Text = $"Fout opgetreden tijdens installatie (Code: {process.ExitCode}).";
                            MessageBox.Show("Er is een fout opgetreden. Controleer het IP-adres en of de PS2 server actief is.", "Fout", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Kan hdl_dump.exe niet uitvoeren.";
                    MessageBox.Show($"Fout bij starten van hdl_dump: {ex.Message}\n\nZorg ervoor dat hdl_dump.exe in dezelfde map staat als dit programma.", "Opstartfout", MessageBoxButton.OK, MessageBoxImage.Error);
                    BtnInstall.IsEnabled = true;
                    BtnBrowse.IsEnabled = true;
                });
            }
        }

        /// <summary>
        /// Scant de eerste sectoren van de ISO om de PS2 Game ID (SYSTEM.CNF) te vinden.
        /// </summary>
        private string ExtractGameIdFromIso(string isoPath)
        {
            try
            {
                using (FileStream fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // PS2 ISO's hebben hun systeembestanden vaak rond sector 16 (0x8000) liggen.
                    // We scannen de eerste paar megabytes om de SYSTEM.CNF tekst te vinden.
                    byte[] buffer = new byte[1024 * 1024 * 4]; // 4 MB buffer
                    int bytesRead = fs.Read(buffer, 0, buffer.Length);
                    string text = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                    // Zoek naar patronen zoals SLES_123.45, SLUS_123.45, SCUS_123.45, etc.
                    var match = Regex.Match(text, @"(SLES|SLUS|SCUS|SCDH|SLPM|SLPS|SCPM)_[0-9]{3}\.[0-9]{2}");
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }
            catch
            {
                // Mocht het lezen falen, return null zodat de gebruiker het zelf kan invullen
            }
            return null;
        }
    }
}
