using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Navigation;
using System.Diagnostics;

namespace RoundedTB
{
    /// <summary>
    /// Interaction logic for AboutWindow.xaml
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            WPFUI.Background.Manager.Apply(WPFUI.Background.BackgroundType.Mica, this);
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ShellOpen(e.Uri.ToString());
            e.Handled = true;
        }

        private void configButton_Click(object sender, RoutedEventArgs e)
        {
            ShellOpen(((MainWindow)Application.Current.MainWindow).configPath);
        }

        private void logButton_Click(object sender, RoutedEventArgs e)
        {
            ShellOpen(((MainWindow)Application.Current.MainWindow).logPath);
        }

        /// <summary>
        /// Opens a URL or file with the shell. .NET Core defaults UseShellExecute to false, so
        /// passing a non-executable path to Process.Start throws Win32Exception and takes the app
        /// with it - which is what happened when clicking any link in this dialog.
        /// </summary>
        private void ShellOpen(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open {target}: {ex.Message}");
                MessageBox.Show($"Couldn't open:\n{target}", "RoundedTB", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
