using System;
using System.Windows;

namespace Ome
{
    public partial class ConfigWindow : Window
    {
        private bool _initializing;

        public ConfigWindow()
        {
            InitializeComponent();

            // Owner is assigned after construction but before ShowDialog, so read
            // its state in Loaded. The flag stops the programmatic IsChecked
            // assignment from firing the Changed handler.
            Loaded += (s, e) =>
            {
                _initializing = true;
                ReplayGainCheckBox.IsChecked = (Owner as MainWindow)?.UseReplayGain ?? false;
                _initializing = false;
            };
        }

        // Event handler for the ReplayGain checkbox (fires for both check and uncheck)
        private void ReplayGainCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing) return;
            (Owner as MainWindow)?.SetReplayGainEnabled(ReplayGainCheckBox.IsChecked == true);
        }

        // Event handler for the Load Config button
        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            // Implement the logic for loading the configuration
            var mainWindow = this.Owner as MainWindow;
            if (mainWindow != null)
            {
                // Open file dialog to select config file and load it
                Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
                if (openFileDialog.ShowDialog() == true)
                {
                    mainWindow.LoadConfiguration(openFileDialog.FileName);
                    this.Close(); // Close the Config window after loading
                }
            }
        }

        // Event handler for the Save Config button
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            // Implement the logic for saving the configuration
            var mainWindow = this.Owner as MainWindow;
            if (mainWindow != null)
            {
                // Open file dialog to save config file
                Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "config",         // Default file name
                    DefaultExt = ".json",        // Default file extension
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*" // Filter files by extension
                };
                if (saveFileDialog.ShowDialog() == true)
                {
                    mainWindow.SaveConfiguration(saveFileDialog.FileName);
                }
            }
        }

        // Event handler for the Exit Application button
        private void ExitApplicationButton_Click(object sender, RoutedEventArgs e)
        {
            // Exit the entire application
            Application.Current.Shutdown();
        }
    }
}
