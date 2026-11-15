using System.Windows;
using System.Windows.Media;

namespace PS2FTPCONNECTOR
{
    public partial class ConfirmDialog : Window
    {
        public ConfirmDialog(string title, string message, bool isWarning = false)
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtMessage.Text = message;

            // Als het een gevaarlijke waarschuwing is, kleurt de hele popup neon-oranje/rood!
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
                BtnYes.Content = "[ ! ] FORCE EXIT";
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

        // Handige hulpmethode die je overal met 1 regel kunt aanroepen!
        public static bool Ask(Window owner, string title, string message, bool isWarning = false)
        {
            var dlg = new ConfirmDialog(title, message, isWarning) { Owner = owner };
            return dlg.ShowDialog() == true;
        }
    }
}
