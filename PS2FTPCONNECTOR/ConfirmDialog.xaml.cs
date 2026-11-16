using System.Windows;
using System.Windows.Media;

namespace PS2FTPCONNECTOR
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, bool isWarning = false, string yesButtonText = "[ ✓ ] CONFIRM")
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            BtnYes.Content = yesButtonText;

            if (isWarning)
            {
                var warnColor = Color.FromRgb(255, 60, 60);
                var warnBrush = new SolidColorBrush(warnColor);
                DialogBorder.BorderBrush = warnBrush;
                DialogGlow.Color = warnColor;
                TxtTitle.Foreground = new SolidColorBrush(Color.FromRgb(255, 153, 0));
                BtnYes.Background = warnBrush;
                BtnYes.BorderBrush = warnBrush;
                BtnYes.Foreground = Brushes.White;
                if (yesButtonText == "[ ✓ ] CONFIRM") BtnYes.Content = "[ ! ] PROCEED";
            }
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // A handy helper method that you can call from anywhere with just a single line of code!
        public static bool Ask(Window owner, string title, string message, bool isWarning = false)
        {
            var dlg = new ConfirmDialog(title, message, isWarning) { Owner = owner };
            return dlg.ShowDialog() == true;
        }
        // A convenient one-button notification in the same sci-fi style (replaces all standard MessageBox.Show alerts!)
        public static void Notify(Window owner, string title, string message, bool isWarning = false)
        {
            var dlg = new ConfirmDialog(title, message, isWarning) { Owner = owner };
            dlg.BtnNo.Visibility = Visibility.Collapsed;
            dlg.BtnYes.Content = "[ ✓ ] ACKNOWLEDGE";
            dlg.ShowDialog();
        }
    }
}
