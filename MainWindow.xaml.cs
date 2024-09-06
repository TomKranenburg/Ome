using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using NAudio.Wave;
using System.Windows.Controls.Primitives;

namespace Ome
{
    public partial class MainWindow : Window
    {
        private string SoundFolderPath; // Will be set dynamically to the current working directory or "Audio" folder
        private Dictionary<string, WaveOutEvent> PlayingSounds = new Dictionary<string, WaveOutEvent>();
        private Dictionary<string, LoopStream> LoopStreams = new Dictionary<string, LoopStream>();
        private Dictionary<string, AudioFileReader> AudioReaders = new Dictionary<string, AudioFileReader>();
        private Dictionary<string, double> TrackVolumes = new Dictionary<string, double>();
        private Dictionary<string, Slider> VolumeSliders = new Dictionary<string, Slider>(); // Store slider references
        private Dictionary<string, ToggleButton> PlayToggleButtons = new Dictionary<string, ToggleButton>(); // Store toggle button references
        public string ConfigFilePath;  // Config file path for loading/saving configurations

        public MainWindow()
        {
            InitializeComponent();

            // Set the SoundFolderPath to the current working directory
            SetSoundFolderPath();

            // Load configuration and folder path if provided as arguments
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                ConfigFilePath = args[1];
                LoadConfiguration(ConfigFilePath);
            }

            LoadSoundButtons();

            // Adjust the window height and width dynamically after loading the buttons
            AdjustWindowHeight();
            AdjustWindowWidth();
        }

        private void SetSoundFolderPath()
        {
            // Get the current working directory of the executable
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            SoundFolderPath = currentDirectory;

            // Check if there are .flac files in the current directory
            var flacFiles = Directory.GetFiles(SoundFolderPath, "*.flac");

            // If no .flac files are found, check in the "Audio" folder within the current directory
            if (flacFiles.Length == 0)
            {
                string audioFolder = Path.Combine(currentDirectory, "Audio");
                if (Directory.Exists(audioFolder))
                {
                    SoundFolderPath = audioFolder;
                    flacFiles = Directory.GetFiles(SoundFolderPath, "*.flac");
                }
            }

            // If no files are still found, inform the user that no sound files are detected
            //if (flacFiles.Length == 0)
            //{
                //MessageBox.Show("No .flac audio files found in the current directory or 'Audio' folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            //}
        }

        private void LoadSoundButtons()
        {
            if (!Directory.Exists(SoundFolderPath))
            {
                MessageBox.Show("Sound folder not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var FlacFiles = Directory.GetFiles(SoundFolderPath, "*.flac");
            foreach (var FlacFile in FlacFiles)
            {
                var FileName = System.IO.Path.GetFileNameWithoutExtension(FlacFile);

                // Create a StackPanel to hold the label, toggle button, and slider
                var StackPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

                // Create a Label for the file name
                var FileLabel = new Label { Content = FileName, Foreground = Brushes.White, Width = 150, Margin = new Thickness(5) };
                StackPanel.Children.Add(FileLabel);

                // Create a Play/Stop toggle button
                var PlayToggleButton = new ToggleButton { Content = "Play", Tag = FlacFile, Width = 75, Margin = new Thickness(5) };
                PlayToggleButton.Checked += PlayToggleButton_Checked;
                PlayToggleButton.Unchecked += PlayToggleButton_Unchecked;
                StackPanel.Children.Add(PlayToggleButton);

                // Store the toggle button reference for later use
                PlayToggleButtons[FlacFile] = PlayToggleButton;

                // Create a Volume slider
                var VolumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 100, Margin = new Thickness(5) };
                VolumeSlider.Tag = FlacFile;
                VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
                StackPanel.Children.Add(VolumeSlider);

                // Store the slider reference for later use
                VolumeSliders[FlacFile] = VolumeSlider;

                // Check if a volume was previously set for this file
                if (TrackVolumes.ContainsKey(FlacFile))
                {
                    VolumeSlider.Value = TrackVolumes[FlacFile];
                }

                ButtonsPanel.Children.Add(StackPanel);
            }
        }

        private void AdjustWindowHeight()
        {
            // Calculate the total height needed for all track buttons
            double trackButtonHeight = 50; // Estimated height for each track's UI (adjust as necessary)
            double totalHeight = (trackButtonHeight * ButtonsPanel.Children.Count) + 80; // Add extra for margins and padding

            // Get the screen's height
            double screenHeight = SystemParameters.FullPrimaryScreenHeight;

            // Set the window height, but ensure it doesn't exceed the screen height
            this.Height = Math.Min(totalHeight, screenHeight);
        }

        private void AdjustWindowWidth()
        {
            // Estimate the width based on the sum of the label, button, and slider widths
            double labelWidth = 150; // Width of the file name label
            double buttonWidth = 75; // Width of the play/stop button
            double sliderWidth = 150; // Width of the volume slider
            double marginWidth = 30;  // Extra margin to make space for padding and gaps

            // Set the total window width
            this.Width = labelWidth + buttonWidth + sliderWidth + marginWidth;
        }

        private void PlayToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            var Button = sender as ToggleButton;
            var FilePath = Button.Tag as string;

            if (!PlayingSounds.ContainsKey(FilePath))
            {
                StartSound(FilePath);
                Button.Content = "Stop";
            }
        }

        private void PlayToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            var Button = sender as ToggleButton;
            var FilePath = Button.Tag as string;

            if (PlayingSounds.ContainsKey(FilePath))
            {
                StopSound(FilePath);
                Button.Content = "Play";
            }
        }

        private void StartSound(string FilePath)
        {
            var Reader = new AudioFileReader(FilePath);
            AudioReaders[FilePath] = Reader;

            // Set the volume to the previously saved value if it exists
            if (TrackVolumes.ContainsKey(FilePath))
            {
                Reader.Volume = (float)TrackVolumes[FilePath];
            }

            var Loop = new LoopStream(Reader); // Custom loop stream to enable looping
            var OutputDevice = new WaveOutEvent();
            OutputDevice.Init(Loop);
            OutputDevice.Play();

            PlayingSounds[FilePath] = OutputDevice;
            LoopStreams[FilePath] = Loop;
        }

        private void StopSound(string FilePath)
        {
            if (PlayingSounds.ContainsKey(FilePath))
            {
                PlayingSounds[FilePath].Stop();
                PlayingSounds[FilePath].Dispose();
                PlayingSounds.Remove(FilePath);

                if (LoopStreams.ContainsKey(FilePath))
                {
                    LoopStreams[FilePath].Dispose();
                    LoopStreams.Remove(FilePath);
                }

                if (AudioReaders.ContainsKey(FilePath))
                {
                    AudioReaders[FilePath].Dispose();
                    AudioReaders.Remove(FilePath);
                }
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var Slider = sender as Slider;
            var FilePath = Slider.Tag as string;

            if (AudioReaders.ContainsKey(FilePath))
            {
                AudioReaders[FilePath].Volume = (float)Slider.Value;
                TrackVolumes[FilePath] = Slider.Value; // Save the volume for future use
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigWindow();
            configWindow.Owner = this;
            configWindow.ShowDialog();
        }

        public void SaveConfiguration(string filePath)
        {
            var config = new List<TrackConfig>();

            foreach (var track in AudioReaders.Keys)
            {
                config.Add(new TrackConfig
                {
                    FilePath = track,
                    IsPlaying = PlayingSounds.ContainsKey(track),
                    Volume = TrackVolumes.ContainsKey(track) ? TrackVolumes[track] : 0.5
                });
            }

            // Save window location and dimensions
            var windowConfig = new WindowConfig
            {
                Width = this.Width,
                Height = this.Height,
                Left = this.Left,
                Top = this.Top
            };

            var configData = new ConfigData
            {
                Tracks = config,
                Window = windowConfig
            };

            var json = JsonConvert.SerializeObject(configData, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }

        public void LoadConfiguration(string filePath)
        {
            if (!File.Exists(filePath)) return;

            var json = File.ReadAllText(filePath);
            var configData = JsonConvert.DeserializeObject<ConfigData>(json);

            if (configData != null)
            {
                // Reset all buttons and sliders to default values
                foreach (var toggleButton in PlayToggleButtons.Values)
                {
                    toggleButton.IsChecked = false;
                    toggleButton.Content = "Play";
                }

                foreach (var slider in VolumeSliders.Values)
                {
                    slider.Value = 0.5; // Default volume value
                }

                // Stop all currently playing sounds
                foreach (var track in new List<string>(PlayingSounds.Keys))
                {
                    StopSound(track);
                }

                // Load window location and dimensions
                this.Width = configData.Window.Width;
                this.Height = configData.Window.Height;
                this.Left = configData.Window.Left;
                this.Top = configData.Window.Top;

                foreach (var trackConfig in configData.Tracks)
                {
                    TrackVolumes[trackConfig.FilePath] = trackConfig.Volume;

                    // Restore volume slider position
                    if (VolumeSliders.ContainsKey(trackConfig.FilePath))
                    {
                        VolumeSliders[trackConfig.FilePath].Value = trackConfig.Volume;
                    }

                    // Restore play state and toggle button state
                    if (trackConfig.IsPlaying)
                    {
                        StartSound(trackConfig.FilePath);
                        if (PlayToggleButtons.ContainsKey(trackConfig.FilePath))
                        {
                            PlayToggleButtons[trackConfig.FilePath].IsChecked = true;
                            PlayToggleButtons[trackConfig.FilePath].Content = "Stop";
                        }
                    }
                    else
                    {
                        if (PlayToggleButtons.ContainsKey(trackConfig.FilePath))
                        {
                            PlayToggleButtons[trackConfig.FilePath].IsChecked = false;
                            PlayToggleButtons[trackConfig.FilePath].Content = "Play";
                        }
                    }
                }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Ensure all sounds are stopped and disposed of when the window is closed
            foreach (var Sound in PlayingSounds.Values)
            {
                Sound.Stop();
                Sound.Dispose();
            }

            foreach (var Loop in LoopStreams.Values)
            {
                Loop.Dispose();
            }

            foreach (var Reader in AudioReaders.Values)
            {
                Reader.Dispose();
            }

            // Save the configuration automatically if a config file path is provided
            if (!string.IsNullOrEmpty(ConfigFilePath))
            {
                SaveConfiguration(ConfigFilePath);
            }
        }
    }

    public class TrackConfig
    {
        public string FilePath { get; set; }
        public bool IsPlaying { get; set; }
        public double Volume { get; set; }
    }

    public class WindowConfig
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
    }

    public class ConfigData
    {
        public List<TrackConfig> Tracks { get; set; }
        public WindowConfig Window { get; set; }
    }

    // Custom loop stream class to enable looping
    public class LoopStream : WaveStream
    {
        private readonly WaveStream SourceStream;

        public LoopStream(WaveStream sourceStream)
        {
            this.SourceStream = sourceStream;
        }

        public override WaveFormat WaveFormat => SourceStream.WaveFormat;

        public override long Length => SourceStream.Length;

        public override long Position
        {
            get => SourceStream.Position;
            set => SourceStream.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;

            while (totalBytesRead < count)
            {
                int bytesRead = SourceStream.Read(buffer, offset + totalBytesRead, count - totalBytesRead);
                if (bytesRead == 0)
                {
                    if (SourceStream.Position == 0 || SourceStream.Position == SourceStream.Length)
                    {
                        break; // reached end of stream or not started yet
                    }
                    SourceStream.Position = 0;
                }
                totalBytesRead += bytesRead;
            }

            return totalBytesRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SourceStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}