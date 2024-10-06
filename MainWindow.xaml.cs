using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using NAudio.Wave;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Ome
{
    public partial class MainWindow : Window
    {
        private string SoundFolderPath;
        private Dictionary<string, IWavePlayer> PlayingSounds = new Dictionary<string, IWavePlayer>();
        private Dictionary<string, LoopStream> LoopStreams = new Dictionary<string, LoopStream>();
        private Dictionary<string, AudioFileReader> AudioReaders = new Dictionary<string, AudioFileReader>();
        private Dictionary<string, double> TrackVolumes = new Dictionary<string, double>();
        private Dictionary<string, Slider> VolumeSliders = new Dictionary<string, Slider>();
        private Dictionary<string, TextBox> VolumeTextBoxes = new Dictionary<string, TextBox>(); // TextBox for each volume
        private Dictionary<string, ToggleButton> PlayToggleButtons = new Dictionary<string, ToggleButton>();
        private Dictionary<string, Label> LoopCountLabels = new Dictionary<string, Label>();
        private Dictionary<string, Label> PlaybackPositionLabels = new Dictionary<string, Label>();

        public string ConfigFilePath;
        private double GlobalVolume = 0.5;  // Default global volume level (50%)

        public MainWindow()
        {

            // Get the reference to the App instance
            var app = (App)Application.Current;

            // Set ShowActivated based on the NoFocus flag
            this.ShowActivated = !app.NoFocus;

            InitializeComponent();

            // Load the sound folder path and files
            SetSoundFolderPath();
            LoadSoundButtons();

            // Adjust window size based on loaded content
            //AdjustWindowHeight();
            AdjustWindowWidth();

            // Update the window title to reflect the initial play state
            UpdateWindowTitle();

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

            foreach (var arg in args)
            {
                Debug.WriteLine($"Argument: {arg}");
            }

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

        private void UpdateWindowTitle()
        {
            Dispatcher.Invoke(() =>
            {
                string baseTitle = "Ome - Ambient Soundscape Mixer";

                if (PlayingSounds.Count == 0)
                {
                    this.Title = $"{baseTitle} - Stopped";
                    return;
                }

                bool anyPlaying = PlayingSounds.Values.Any(player => player.PlaybackState == PlaybackState.Playing);
                bool anyPaused = PlayingSounds.Values.Any(player => player.PlaybackState == PlaybackState.Paused);

                if (anyPlaying)
                {
                    this.Title = $"{baseTitle} - Playing";
                }
                else if (anyPaused)
                {
                    this.Title = $"{baseTitle} - Paused";
                }
                else
                {
                    this.Title = $"{baseTitle} - Stopped";
                }
            });
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

            // Update the PlayPauseToggleButton's state
            PlayPauseToggleButton.IsChecked = true;

            // Update the window title
            UpdateWindowTitle();
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

            // Update the PlayPauseToggleButton's state
            PlayPauseToggleButton.IsChecked = false;

            // Update the window title
            UpdateWindowTitle();
        }
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (this.WindowState == WindowState.Normal && ((App)Application.Current).NoFocus)
            {
                this.ShowActivated = false;
            }
        }
        /// <summary>
        /// Loads the audio files (flac, mp3, wav) into the UI as buttons with volume sliders and text boxes.
        /// </summary>
        private void LoadSoundButtons()
        {
            if (!Directory.Exists(SoundFolderPath)) return;

            ButtonsPanel.Children.Clear(); // Clear existing buttons before reloading
            PlaybackPositionLabels.Clear(); // Clear existing playback position labels
            LoopCountLabels.Clear(); // Clear existing loop count labels

            var audioFiles = new List<string>();
            audioFiles.AddRange(Directory.GetFiles(SoundFolderPath, "*.flac"));
            audioFiles.AddRange(Directory.GetFiles(SoundFolderPath, "*.mp3"));
            audioFiles.AddRange(Directory.GetFiles(SoundFolderPath, "*.wav"));

            foreach (var audioFile in audioFiles)
            {
                var FileName = System.IO.Path.GetFileNameWithoutExtension(audioFile);

                // StackPanel for track UI
                var StackPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

                // Playback position label
                var PlaybackPositionLabel = new Label
                {
                    Content = "00:00:00",
                    Foreground = Brushes.White,
                    Width = 60,
                    Margin = new Thickness(5),
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                StackPanel.Children.Add(PlaybackPositionLabel);

                // Store the playback position label in the dictionary
                PlaybackPositionLabels[audioFile] = PlaybackPositionLabel;

                // Loop count label
                var LoopCountLabel = new Label
                {
                    Content = "000",
                    Foreground = Brushes.White,
                    Width = 30,
                    Margin = new Thickness(5),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                StackPanel.Children.Add(LoopCountLabel);

                // Store loop count label in a dictionary for updating later
                LoopCountLabels[audioFile] = LoopCountLabel;

                // Label for track name
                var FileLabel = new Label { Content = FileName, Foreground = Brushes.White, Width = 150, Margin = new Thickness(5) };
                StackPanel.Children.Add(FileLabel);

                // Play/Stop toggle button
                var PlayToggleButton = new ToggleButton { Content = "Play", Tag = audioFile, Width = 75, Margin = new Thickness(5) };
                PlayToggleButton.Checked += PlayToggleButton_Checked;
                PlayToggleButton.Unchecked += PlayToggleButton_Unchecked;
                StackPanel.Children.Add(PlayToggleButton);

                PlayToggleButtons[audioFile] = PlayToggleButton;

                // Volume slider
                var VolumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 200, Margin = new Thickness(5) };
                VolumeSlider.Tag = audioFile;
                VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
                StackPanel.Children.Add(VolumeSlider);

                VolumeSliders[audioFile] = VolumeSlider;

                // TextBox for volume input
                var VolumeTextBox = new TextBox
                {
                    Width = 50,
                    Text = "0.500",
                    Margin = new Thickness(5),
                    TextAlignment = TextAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                VolumeTextBox.Tag = audioFile;
                VolumeTextBox.TextChanged += VolumeTextBox_TextChanged;
                StackPanel.Children.Add(VolumeTextBox);

                VolumeTextBoxes[audioFile] = VolumeTextBox;

                if (TrackVolumes.ContainsKey(audioFile))
                {
                    VolumeSlider.Value = TrackVolumes[audioFile];
                    VolumeTextBox.Text = TrackVolumes[audioFile].ToString("0.000");  // Show three decimal places
                }
                else
                {
                    TrackVolumes[audioFile] = 0.5;
                    VolumeTextBox.Text = "0.500";  // Show three decimal places for default value
                }

                // Add the stack panel for this track to the main panel
                ButtonsPanel.Children.Add(StackPanel);
            }
        }

        /// <summary>
        /// Event handler for volume slider. Updates both the track volume and the corresponding text box.
        /// </summary>
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var Slider = sender as Slider;
            var FilePath = Slider.Tag as string;
            if (!AudioReaders.ContainsKey(FilePath))
            {
                AudioFileReader Reader = new AudioFileReader(FilePath);
                AudioReaders[FilePath] = Reader;
            }

            TrackVolumes[FilePath] = Slider.Value;  // Save the individual track volume
            AudioReaders[FilePath].Volume = (float)(Slider.Value * GlobalVolume);  // Update track volume based on global volume

            if (VolumeTextBoxes.ContainsKey(FilePath))
            {
                VolumeTextBoxes[FilePath].Text = Slider.Value.ToString("0.000"); // Update the text box when slider changes
            }

        }

        /// <summary>
        /// Event handler for volume text box. Updates both the slider and the track volume.
        /// </summary>
        private void VolumeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var TextBox = sender as TextBox;
            var FilePath = TextBox.Tag as string;

            if (double.TryParse(TextBox.Text, out double volume) && volume >= 0 && volume <= 1)
            {
                if (!AudioReaders.ContainsKey(FilePath))
                {
                    AudioFileReader reader = new AudioFileReader(FilePath);
                    AudioReaders[FilePath] = reader;
                }

                TrackVolumes[FilePath] = volume;
                AudioReaders[FilePath].Volume = (float)(volume * GlobalVolume);  // Update track volume based on global volume

                if (VolumeSliders.ContainsKey(FilePath))
                {
                    VolumeSliders[FilePath].Value = volume; // Update the slider when text box changes
                }
            }
        }

        /// <summary>
        /// Event handler for the global volume slider. Updates the volume for all tracks.
        /// </summary>
        private void GlobalVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            GlobalVolume = GlobalVolumeSlider.Value;  // Update the global volume value

            // Update the volume of all currently playing tracks
            foreach (var filePath in AudioReaders.Keys)
            {
                // Check if the file path exists in both TrackVolumes and AudioReaders
                if (TrackVolumes.ContainsKey(filePath) && AudioReaders.ContainsKey(filePath))
                {
                    // Adjust the volume by multiplying the individual track volume by the global volume
                    AudioReaders[filePath].Volume = (float)(TrackVolumes[filePath] * GlobalVolume);
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
        /// Adjusts the window height based on the number of tracks loaded.
        /// </summary>
        private void AdjustWindowHeight()
        {
            double trackButtonHeight = 60;
            double totalHeight = (trackButtonHeight * ButtonsPanel.Children.Count) + 50;

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
            double sliderWidth = 100;
            double textBoxWidth = 90;
            double marginWidth = 270;

            double MaxWidth = labelWidth + buttonWidth + sliderWidth + textBoxWidth + marginWidth;

            this.Width = MaxWidth;
            
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
                Button.Background = Brushes.LightBlue; // Set background to light blue when "Stop"
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
                Button.Background = Brushes.White; // Reset background to default (white) when "Play"
            }
        }

        /// <summary>
        /// Starts playing an audio file.
        /// </summary>
        private void StartSound(string FilePath)
        {
            AudioFileReader reader = new AudioFileReader(FilePath);
            AudioReaders[FilePath] = reader;

            // Set the volume to the track volume multiplied by the global volume
            if (TrackVolumes.ContainsKey(FilePath))
            {
                reader.Volume = (float)(TrackVolumes[FilePath] * GlobalVolume);
            }

            // Wrap the reader in a LoopStream to enable looping
            var loopStream = new LoopStream(reader);
            LoopStreams[FilePath] = loopStream;

            // Initialize the WasapiOut for more precise, lower-latency playback
            var outputDevice = new WasapiOut(AudioClientShareMode.Shared, 200);  // 200ms latency
            outputDevice.Init(loopStream);
            outputDevice.Play();

            // Store the WasapiOut object in the PlayingSounds dictionary as IWavePlayer
            PlayingSounds[FilePath] = outputDevice;

            // Update loop count and playback position periodically
            Task.Run(() =>
            {
                while (PlayingSounds.ContainsKey(FilePath))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (LoopStreams.ContainsKey(FilePath))
                        {
                            // Update the playback position label
                            TimeSpan currentTime = LoopStreams[FilePath].CurrentTime;
                            string formattedTime = currentTime.ToString(@"hh\:mm\:ss");
                            if (PlaybackPositionLabels.ContainsKey(FilePath))
                            {
                                PlaybackPositionLabels[FilePath].Content = formattedTime;
                            }

                            // Update loop count
                            int loopCount = LoopStreams[FilePath].LoopCount;
                            LoopCountLabels[FilePath].Content = loopCount.ToString("D3");
                        }
                    });
                    System.Threading.Thread.Sleep(500);  // Update every 500 milliseconds
                }

                // Reset the labels when the track stops
                Dispatcher.Invoke(() =>
                {
                    if (PlaybackPositionLabels.ContainsKey(FilePath))
                    {
                        PlaybackPositionLabels[FilePath].Content = "00:00:00";
                    }

                    if (LoopCountLabels.ContainsKey(FilePath))
                    {
                        LoopCountLabels[FilePath].Content = "000";
                    }
                });
            });

            // Update the window title
            UpdateWindowTitle();
        }

        /// <summary>
        /// Stops playing an audio file.
        /// </summary>
        private void StopSound(string FilePath)
        {
            if (PlayingSounds.ContainsKey(FilePath))
            {
                PlayingSounds[FilePath].Stop();
                PlayingSounds[FilePath].Dispose();  // Dispose of the WasapiOut object
                PlayingSounds.Remove(FilePath);
            }

            if (LoopStreams.ContainsKey(FilePath))
            {
                LoopStreams[FilePath].Dispose();  // Dispose of the LoopStream
                LoopStreams.Remove(FilePath);
            }

            if (AudioReaders.ContainsKey(FilePath))
            {
                AudioReaders[FilePath].Dispose();  // Dispose of the AudioFileReader
                AudioReaders.Remove(FilePath);
            }

            // Reset the playback position label
            if (PlaybackPositionLabels.ContainsKey(FilePath))
            {
                PlaybackPositionLabels[FilePath].Content = "00:00:00";
            }

            // Reset the loop count label
            if (LoopCountLabels.ContainsKey(FilePath))
            {
                LoopCountLabels[FilePath].Content = "000";
            }

            // Update the window title
            UpdateWindowTitle();
        }

        /// <summary>
        /// Saves the current configuration (track volumes, window size, play states) to a file.
        /// </summary>
        public void SaveConfiguration(string filePath)
        {
            // Ensure the file has a .json extension
            if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                filePath += ".json";
            }

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
                Top = this.Top,
                GlobalVolume = this.GlobalVolume  // Save the global volume
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

                // Respect minimum width and height
                this.Width = configData.Window.Width;
                this.Height = configData.Window.Height;
                this.Left = configData.Window.Left;
                this.Top = configData.Window.Top;

                // Restore the global volume
                this.GlobalVolume = configData.Window.GlobalVolume;
                GlobalVolumeSlider.Value = GlobalVolume;

                foreach (var trackConfig in configData.Tracks)
                {
                    TrackVolumes[trackConfig.FilePath] = trackConfig.Volume;

                    if (VolumeSliders.ContainsKey(trackConfig.FilePath))
                    {
                        VolumeSliders[trackConfig.FilePath].Value = trackConfig.Volume;
                    }

                    if (VolumeTextBoxes.ContainsKey(trackConfig.FilePath))
                    {
                        VolumeTextBoxes[trackConfig.FilePath].Text = trackConfig.Volume.ToString("0.000");
                    }

                    if (trackConfig.IsPlaying)
                    {
                        StartSound(trackConfig.FilePath); // Start the sound as usual

                        if (PlayToggleButtons.ContainsKey(trackConfig.FilePath))
                        {
                            // Set the toggle button state to "checked" and change its content to "Stop"
                            PlayToggleButtons[trackConfig.FilePath].IsChecked = true;
                            PlayToggleButtons[trackConfig.FilePath].Content = "Stop";

                            // Manually set the background color to light blue
                            PlayToggleButtons[trackConfig.FilePath].Background = Brushes.LightBlue;
                        }
                    }
                }
            }
            UpdateWindowTitle();
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
                toggleButton.Background = Brushes.White;
            }

            foreach (var slider in VolumeSliders.Values)
            {
                slider.Value = 0.5;
            }

            foreach (var textBox in VolumeTextBoxes.Values)
            {
                textBox.Text = "0.500";
            }

            foreach (var loopLabel in LoopCountLabels.Values)
            {
                loopLabel.Content = "000";  // Reset the loop count display
            }

            foreach (var playbackLabel in PlaybackPositionLabels.Values)
            {
                playbackLabel.Content = "00:00:00";  // Reset the playback position display
            }

            foreach (var loopStream in LoopStreams.Values)
            {
                loopStream.LoopCount = 0;  // Reset the internal loop count in LoopStream
            }

            TrackVolumes.Clear();

            // Update the window title
            UpdateWindowTitle();
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
        public double GlobalVolume { get; set; }  // Add global volume to window config
    }

    public class ConfigData
    {
        public List<TrackConfig> Tracks { get; set; }
        public WindowConfig Window { get; set; }
    }

    // LoopStream class inherits from WaveStream and is responsible for continuously looping audio.
    public class LoopStream : WaveStream
    {
        private readonly WaveStream sourceStream;
        public int LoopCount { get; set; } = 0;

        public LoopStream(WaveStream sourceStream)
        {
            this.sourceStream = sourceStream;
        }

        public override WaveFormat WaveFormat => sourceStream.WaveFormat;

        public override long Length => sourceStream.Length;

        public override long Position
        {
            get => sourceStream.Position;
            set => sourceStream.Position = value;
        }

        // Expose the current playback time
        public override TimeSpan CurrentTime
        {
            get => sourceStream.CurrentTime;
            set => sourceStream.CurrentTime = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;

            while (totalBytesRead < count)
            {
                int bytesRead = sourceStream.Read(buffer, offset + totalBytesRead, count - totalBytesRead);

                if (bytesRead == 0)
                {
                    sourceStream.Position = 0;
                    LoopCount++;
                }

                totalBytesRead += bytesRead;

                // Prevent infinite loop if no data is read
                //if (bytesRead == 0)
                //{
                    //break;
                //}
            }

            return totalBytesRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                sourceStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }




}
