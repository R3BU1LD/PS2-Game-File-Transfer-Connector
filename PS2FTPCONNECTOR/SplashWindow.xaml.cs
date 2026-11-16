using System.Threading.Tasks;
using System.Windows;

namespace PS2FTPCONNECTOR
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            Loaded += SplashWindow_Loaded;
        }

        private async void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string[] steps =
            {
                ">> INITIALIZING TCP/IP SOCKET MATRIX...",
                ">> LOADING HDL PROTOCOL v0x0C HANDSHAKE MODULE...",
                ">> PREPARING AUTO-LOGGING & DUPLICATE ENGINE...",
                ">> SYSTEM READY. LAUNCHING HUD..."
            };

            for (int i = 0; i <= 100; i += 5)
            {
                SplashProgress.Value = i;
                if (i == 0) TxtSplashStatus.Text = steps[0];
                else if (i == 30) TxtSplashStatus.Text = steps[1];
                else if (i == 65) TxtSplashStatus.Text = steps[2];
                else if (i == 90) TxtSplashStatus.Text = steps[3];

                  await Task.Delay(100);
                //await Task.Delay(1700);
            }

            MainWindow main = new MainWindow();
            main.Show();
            this.Close();
        }
    }
}
