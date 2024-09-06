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

            // Set the folder path for the audio files
            SetSoundFolderPath();

            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                ConfigFilePath = args[1];
                LoadConfiguration(ConfigFilePath);
            }

            if (args.Length > 1 && args.Contains("--minimized"))
            {
                this.WindowState = WindowState.Minimized;
            }

            LoadSoundButtons();

            AdjustWindowHeight();
            AdjustWindowWidth();
        }

        /// <summary>
        /// Resets all tracks by stopping playback and setting volume sliders to default.
        /// </summary>
        public void ResetAllTracks()
        {
            // Stop all currently playing sounds
            foreach (var track in new List<string>(PlayingSounds.Keys))
            {
                StopSound(track);
            }

            // Reset all toggle buttons to "Play" state
            foreach (var toggleButton in PlayToggleButtons.Values)
            {
                toggleButton.IsChecked = false;
                toggleButton.Content = "Play";
            }

            // Reset all volume sliders to default value (0.5)
            foreach (var slider in VolumeSliders.Values)
            {
                slider.Value = 0.5;
            }

            // Clear the volumes from the dictionary (reset them)
            TrackVolumes.Clear();
        }

        private void SetSoundFolderPath()
        {
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
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

        private void LoadSoundButtons()
        {
            if (!Directory.Exists(SoundFolderPath))
            {
                return;
            }

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

        private void AdjustWindowHeight()
        {
            double trackButtonHeight = 52;
            double totalHeight = (trackButtonHeight * ButtonsPanel.Children.Count) + 80;

            double screenHeight = SystemParameters.FullPrimaryScreenHeight;

            this.Height = Math.Min(totalHeight, screenHeight);
        }

        private void AdjustWindowWidth()
        {
            double labelWidth = 150;
            double buttonWidth = 75;
            double sliderWidth = 150;
            double marginWidth = 30;

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
            AudioFileReader Reader;
            if (FilePath.EndsWith(".wav"))
            {
                Reader = new AudioFileReader(FilePath); // WAV files are directly supported
            }
            else if (FilePath.EndsWith(".mp3"))
            {
                Reader = new AudioFileReader(FilePath); // MP3 is supported via AudioFileReader
            }
            else // Default case, also covers .flac
            {
                Reader = new AudioFileReader(FilePath);
            }

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
                TrackVolumes[FilePath] = Slider.Value;
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
                        break; // End of stream
                    }
                    SourceStream.Position = 0; // Loop the audio
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
