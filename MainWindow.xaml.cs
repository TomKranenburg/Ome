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
        private string SoundFolderPath;
        private Dictionary<string, WaveOutEvent> PlayingSounds = new Dictionary<string, WaveOutEvent>();
        private Dictionary<string, LoopStream> LoopStreams = new Dictionary<string, LoopStream>();
        private Dictionary<string, AudioFileReader> AudioReaders = new Dictionary<string, AudioFileReader>();
        private Dictionary<string, double> TrackVolumes = new Dictionary<string, double>();
        private Dictionary<string, Slider> VolumeSliders = new Dictionary<string, Slider>();
        private Dictionary<string, ToggleButton> PlayToggleButtons = new Dictionary<string, ToggleButton>();

        public string ConfigFilePath;

        public MainWindow()
        {
            InitializeComponent();

            // Load the sound folder path and files
            SetSoundFolderPath();
            LoadSoundButtons();

            // Adjust window size based on loaded content
            AdjustWindowHeight();
            AdjustWindowWidth();

            // Handle command-line arguments after initialization
            var args = Environment.GetCommandLineArgs();
            HandleCommandLineArgs(args);
        }

        /// <summary>
        /// Handles command-line arguments passed to the application.
        /// </summary>
        /// <param name="args">The command-line arguments.</param>
        public void HandleCommandLineArgs(string[] args)
        {
            if (args.Length > 1)
            {
                ConfigFilePath = args[1];
                LoadConfiguration(ConfigFilePath);
            }

            // Handle minimize (--minimize or -m)
            if (args.Contains("--minimize") || args.Contains("-m"))
            {
                // Minimize the window only after everything is loaded
                this.WindowState = WindowState.Minimized;
            }

            // Handle pause (--pause or -p)
            if (args.Contains("--pause") || args.Contains("-p"))
            {
                PauseAllTracks();
            }

            // Handle resume (--resume or -r)
            if (args.Contains("--resume") || args.Contains("-r"))
            {
                ResumeAllTracks();
            }

            if (args.Length > 2)
            {
                SoundFolderPath = args[2];
                LoadSoundButtons();  // Reload the sound buttons if the folder changes
            }
        }

        /// <summary>
        /// Pauses all currently playing tracks.
        /// </summary>
        public void PauseAllTracks()
        {
            foreach (var track in new List<string>(PlayingSounds.Keys))
            {
                if (PlayingSounds[track].PlaybackState == PlaybackState.Playing)
                {
                    PlayingSounds[track].Pause();
                }
            }

            foreach (var toggleButton in PlayToggleButtons.Values)
            {
                if (toggleButton.IsChecked == true)
                {
                    toggleButton.Content = "Resume";
                }
            }
        }

        /// <summary>
        /// Resumes all previously paused tracks.
        /// </summary>
        public void ResumeAllTracks()
        {
            foreach (var track in new List<string>(PlayingSounds.Keys))
            {
                if (PlayingSounds[track].PlaybackState == PlaybackState.Paused)
                {
                    PlayingSounds[track].Play();
                }
            }

            foreach (var toggleButton in PlayToggleButtons.Values)
            {
                if (toggleButton.Content.ToString() == "Resume")
                {
                    toggleButton.Content = "Stop";
                }
            }
        }

        /// <summary>
        /// Event handler for the Play/Pause toggle button.
        /// </summary>
        private void PlayPauseToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var toggleButton = sender as ToggleButton;

            if (toggleButton.IsChecked == true)
            {
                // If the toggle button is checked, pause all tracks
                PauseAllTracks();
            }
            else
            {
                // If the toggle button is unchecked, resume all tracks
                ResumeAllTracks();
            }
        }

        /// <summary>
        /// Event handler for the Reset button to reset all tracks.
        /// </summary>
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            ResetAllTracks();
        }

        /// <summary>
        /// Sets the folder path for audio files, checking the current directory and the "Audio" folder.
        /// </summary>
        private void SetSoundFolderPath()
        {
            // Change to use AppContext.BaseDirectory instead of AppDomain.CurrentDomain.BaseDirectory
            string currentDirectory = AppContext.BaseDirectory;
            SoundFolderPath = currentDirectory;

            var flacFiles = Directory.GetFiles(SoundFolderPath, "*.flac");
            var mp3Files = Directory.GetFiles(SoundFolderPath, "*.mp3");
            var wavFiles = Directory.GetFiles(SoundFolderPath, "*.wav");

            if (flacFiles.Length == 0 && mp3Files.Length == 0 && wavFiles.Length == 0)
            {
                string audioFolder = Path.Combine(currentDirectory, "Audio");
                if (Directory.Exists(audioFolder))
                {
                    SoundFolderPath = audioFolder;
                }
            }
        }

        /// <summary>
        /// Loads the audio files (flac, mp3, wav) into the UI as buttons with volume sliders.
        /// </summary>
        private void LoadSoundButtons()
        {
            if (!Directory.Exists(SoundFolderPath)) return;

            ButtonsPanel.Children.Clear(); // Clear existing buttons before reloading

            var audioFiles = new List<string>();
            audioFiles.AddRange(Directory.GetFiles(SoundFolderPath, "*.flac"));
            audioFiles.AddRange(Directory.GetFiles(SoundFolderPath, "*.mp3"));
            audioFiles.AddRange(Directory.GetFiles(SoundFolderPath, "*.wav"));

            foreach (var audioFile in audioFiles)
            {
                var FileName = System.IO.Path.GetFileNameWithoutExtension(audioFile);

                var StackPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

                var FileLabel = new Label { Content = FileName, Foreground = Brushes.White, Width = 150, Margin = new Thickness(5) };
                StackPanel.Children.Add(FileLabel);

                var PlayToggleButton = new ToggleButton { Content = "Play", Tag = audioFile, Width = 75, Margin = new Thickness(5) };
                PlayToggleButton.Checked += PlayToggleButton_Checked;
                PlayToggleButton.Unchecked += PlayToggleButton_Unchecked;
                StackPanel.Children.Add(PlayToggleButton);

                PlayToggleButtons[audioFile] = PlayToggleButton;

                var VolumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 100, Margin = new Thickness(5) };
                VolumeSlider.Tag = audioFile;
                VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
                StackPanel.Children.Add(VolumeSlider);

                VolumeSliders[audioFile] = VolumeSlider;

                if (TrackVolumes.ContainsKey(audioFile))
                {
                    VolumeSlider.Value = TrackVolumes[audioFile];
                }

                ButtonsPanel.Children.Add(StackPanel);
            }
        }

        /// <summary>
        /// Adjusts the window height based on the number of tracks loaded.
        /// </summary>
        private void AdjustWindowHeight()
        {
            double trackButtonHeight = 52;
            double totalHeight = (trackButtonHeight * ButtonsPanel.Children.Count) + 80;

            double screenHeight = SystemParameters.FullPrimaryScreenHeight;

            this.Height = Math.Min(totalHeight, screenHeight);
        }

        /// <summary>
        /// Adjusts the window width to fit the audio track buttons and controls.
        /// </summary>
        private void AdjustWindowWidth()
        {
            double labelWidth = 150;
            double buttonWidth = 75;
            double sliderWidth = 150;
            double marginWidth = 30;

            this.Width = labelWidth + buttonWidth + sliderWidth + marginWidth;
        }

        /// <summary>
        /// Event handler for the Play/Stop toggle button. Starts playback when checked.
        /// </summary>
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

        /// <summary>
        /// Event handler for the Play/Stop toggle button. Stops playback when unchecked.
        /// </summary>
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

        /// <summary>
        /// Starts playing an audio file.
        /// </summary>
        private void StartSound(string FilePath)
        {
            AudioFileReader Reader = new AudioFileReader(FilePath);

            AudioReaders[FilePath] = Reader;

            if (TrackVolumes.ContainsKey(FilePath))
            {
                Reader.Volume = (float)TrackVolumes[FilePath];
            }

            var Loop = new LoopStream(Reader);
            var OutputDevice = new WaveOutEvent();
            OutputDevice.Init(Loop);
            OutputDevice.Play();

            PlayingSounds[FilePath] = OutputDevice;
            LoopStreams[FilePath] = Loop;
        }

        /// <summary>
        /// Stops playing an audio file.
        /// </summary>
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

        /// <summary>
        /// Event handler for the volume slider. Updates the volume of the playing audio.
        /// </summary>
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var Slider = sender as Slider;
            var FilePath = Slider.Tag as string;

            if (AudioReaders.ContainsKey(FilePath))
            {
                AudioReaders[FilePath].Volume = (float)Slider.Value;
                TrackVolumes[FilePath] = Slider.Value;
            }
        }

        /// <summary>
        /// Saves the current configuration (track volumes, window size, play states) to a file.
        /// </summary>
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

        /// <summary>
        /// Loads a saved configuration (track volumes, window size, play states) from a file.
        /// </summary>
        public void LoadConfiguration(string filePath)
        {
            if (!File.Exists(filePath)) return;

            var json = File.ReadAllText(filePath);
            var configData = JsonConvert.DeserializeObject<ConfigData>(json);

            if (configData != null)
            {
                foreach (var toggleButton in PlayToggleButtons.Values)
                {
                    toggleButton.IsChecked = false;
                    toggleButton.Content = "Play";
                }

                foreach (var slider in VolumeSliders.Values)
                {
                    slider.Value = 0.5;
                }

                foreach (var track in new List<string>(PlayingSounds.Keys))
                {
                    StopSound(track);
                }

                this.Width = configData.Window.Width;
                this.Height = configData.Window.Height;
                this.Left = configData.Window.Left;
                this.Top = configData.Window.Top;

                foreach (var trackConfig in configData.Tracks)
                {
                    TrackVolumes[trackConfig.FilePath] = trackConfig.Volume;

                    if (VolumeSliders.ContainsKey(trackConfig.FilePath))
                    {
                        VolumeSliders[trackConfig.FilePath].Value = trackConfig.Volume;
                    }

                    if (trackConfig.IsPlaying)
                    {
                        StartSound(trackConfig.FilePath);
                        if (PlayToggleButtons.ContainsKey(trackConfig.FilePath))
                        {
                            PlayToggleButtons[trackConfig.FilePath].IsChecked = true;
                            PlayToggleButtons[trackConfig.FilePath].Content = "Stop";
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Stops all audio playback and disposes of resources when the window is closing.
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
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

            if (!string.IsNullOrEmpty(ConfigFilePath))
            {
                SaveConfiguration(ConfigFilePath);
            }
        }

        /// <summary>
        /// Resets all tracks to their default state (stops playback, resets volume).
        /// </summary>
        public void ResetAllTracks()
        {
            foreach (var track in new List<string>(PlayingSounds.Keys))
            {
                StopSound(track);
            }

            foreach (var toggleButton in PlayToggleButtons.Values)
            {
                toggleButton.IsChecked = false;
                toggleButton.Content = "Play";
            }

            foreach (var slider in VolumeSliders.Values)
            {
                slider.Value = 0.5;
            }

            TrackVolumes.Clear();
        }

        /// <summary>
        /// Event handler for the Menu button. Opens the configuration window.
        /// </summary>
        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigWindow();
            configWindow.Owner = this;
            configWindow.ShowDialog();
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
                        break;
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
