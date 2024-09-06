using System;
using System.Windows;
using Microsoft.Win32;

namespace Ome
{
    public partial class ConfigWindow : Window
    {
        public ConfigWindow()
        {
            InitializeComponent();
        }

        // Event handler for the reset button
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            // Get the main window instance and call the reset method
            if (this.Owner is MainWindow mainWindow)
            {
                mainWindow.ResetAllTracks();
            }
        }

        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                var mainWindow = (MainWindow)Owner;
                mainWindow.SaveConfiguration(saveFileDialog.FileName);
            }
        }

        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var mainWindow = (MainWindow)Owner;
                mainWindow.ConfigFilePath = openFileDialog.FileName;
                mainWindow.LoadConfiguration(mainWindow.ConfigFilePath);

                // Close the menu window after loading a config
                this.Close();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}