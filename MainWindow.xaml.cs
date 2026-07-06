using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave.SampleProviders;

namespace Ome
{
    public partial class MainWindow : Window
    {
        /// <summary>All per-track state and UI, keyed by file path (replaces nine parallel dictionaries).</summary>
        private readonly Dictionary<string, Track> Tracks = new(StringComparer.OrdinalIgnoreCase);

        private readonly AudioEngine Engine = new();
        private readonly DispatcherTimer UiTimer;

        private string SoundFolderPath = string.Empty;
        private double GlobalVolume = 0.5;

        // Live folder watching: events mark the folder dirty; a debounced rescan reconciles.
        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _rescanTimer;
        private readonly HashSet<string> _changedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _pendingAttempts = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxReadyAttempts = 30; // ~30s of retries for files still being copied

        /// <summary>Whether ReplayGain tags are applied. Persisted in settings.json, toggled from the menu.</summary>
        public bool UseReplayGain { get; private set; }

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ome", "settings.json");

        // Background ReplayGain scanning, so gains display without the track ever playing.
        private readonly Queue<Track> _rgScanQueue = new();
        private bool _rgScanRunning;

        // Volume fluctuation ("breathing"): the slider value is the peak; while enabled
        // the level glides between random targets in [WobbleMinFactor..WobbleMaxFactor]
        // of it, each glide taking a random few seconds. One timer drives the audio
        // gain and the slider/number display from the same value.
        private const double WobbleMinFactor = 0.45;
        private const double WobbleMaxFactor = 1.0;
        private const double WobbleLegMinSeconds = 4.0;
        private const double WobbleLegMaxSeconds = 12.0;

        // Pan wandering: drifts within ±PanWanderSpan of the slider's set position,
        // clamped to the stereo field; spatial drift is lazier than level breathing.
        private const double PanWanderSpan = 0.6;
        private const double PanLegMinSeconds = 6.0;
        private const double PanLegMaxSeconds = 16.0;

        private readonly DispatcherTimer _wobbleTimer;
        private readonly Random _wobbleRandom = new();
        private bool _updatingWobbleUi; // suppresses the volume handlers during programmatic UI updates

        /// <summary>Shown when the track list is empty, naming the folder that was scanned.</summary>
        private TextBlock? _emptyStateLabel;

        private sealed class Track
        {
            public required string FilePath { get; init; }
            public required string DisplayName { get; init; }
            public required ToggleButton PlayButton { get; init; }
            public required Slider VolumeSlider { get; init; }
            public required TextBox VolumeBox { get; init; }
            public required Label PositionLabel { get; init; }
            public required Label LoopLabel { get; init; }
            public required Label GainLabel { get; init; }
            public required CheckBox FluctuateBox { get; init; }
            public required Slider PanSlider { get; init; }
            public required CheckBox PanWanderBox { get; init; }
            public required StackPanel Row { get; init; }

            public double Volume = 0.5;

            /// <summary>Bumped on every start/stop; lets an in-flight async load detect it was cancelled.</summary>
            public int RequestVersion;

            /// <summary>Decoded audio, kept across stop/start for instant replays. Null for streamed tracks.</summary>
            public CachedSound? Cache;

            /// <summary>Set when the file changed on disk while playing; the cache is refreshed on stop.</summary>
            public bool CacheStale;

            /// <summary>Linear gain from the file's ReplayGain tags (1.0 = none), read at first play.</summary>
            public double ReplayGainScale = 1.0;

            /// <summary>Effective ReplayGain in dB for display; null when the file has no gain tags.</summary>
            public double? ReplayGainDb;
            public bool ReplayGainKnown;

            /// <summary>Whether the volume slowly wanders while playing. The slider value is the peak.</summary>
            public bool Fluctuate;
            public double WobbleFactor = 1.0;   // current multiplier on Volume
            public double WobbleFrom = 1.0;     // glide start factor
            public double WobbleTo = 1.0;       // glide target factor
            public double WobbleDuration;       // seconds for this glide
            public double WobbleElapsed;        // seconds into this glide

            /// <summary>Pan baseline set by the slider (-1 left .. +1 right); wander roams around it.</summary>
            public double Pan;
            public bool PanWander;
            public double PanCurrent;           // applied pan while wandering
            public double PanFrom;
            public double PanTo;
            public double PanDuration;
            public double PanElapsed;

            /// <summary>Non-null exactly while the track is in the mixer.</summary>
            public ILoopingSource? Source;
            public StereoBalanceSampleProvider? Balance;
            public VolumeSampleProvider? Gain;

            public bool IsPlaying => Source != null;
        }

        public MainWindow()
        {
            var app = (App)Application.Current;
            this.ShowActivated = !app.NoFocus;

            InitializeComponent();

            // The original declared Window_Closing but never wired it up, so config was
            // never saved and audio never disposed on exit. Wire it here.
            this.Closing += Window_Closing;

            // One UI-thread timer updates every playing track's labels; replaces the
            // previous one-background-thread-per-track polling (and its data race on
            // the shared dictionaries).
            UiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            UiTimer.Tick += UiTimer_Tick;

            // Folder events are debounced into a single reconcile pass through this timer.
            _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _rescanTimer.Tick += RescanTimer_Tick;

            // Drives volume fluctuation (audio + visuals) at ~20fps; runs only while needed.
            _wobbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _wobbleTimer.Tick += WobbleTimer_Tick;

            Engine.MasterVolume = (float)GlobalVolume;
            Engine.InputEnded += Engine_InputEnded;

            LoadAppSettings(); // before HandleCommandLineArgs, so config-started tracks get correct gains

            SetSoundFolderPath();
            HandleCommandLineArgs(Environment.GetCommandLineArgs(), initialLoad: true);

            AdjustWindowWidth();
            UpdateWindowTitle();
        }

        /// <summary>Handles command-line arguments, both at startup and via the single-instance pipe.</summary>
        public void HandleCommandLineArgs(string[] args) => HandleCommandLineArgs(args, initialLoad: false);

        private void HandleCommandLineArgs(string[] args, bool initialLoad)
        {
            // Separate flags from positional arguments. (The original treated args
            // strictly positionally, so "Ome.exe -m" read "-m" as the config path.)
            var positionals = new List<string>();
            bool minimize = false, pause = false, resume = false;

            foreach (var arg in args.Skip(1))
            {
                switch (arg)
                {
                    case "--minimize" or "-m": minimize = true; break;
                    case "--pause" or "-p": pause = true; break;
                    case "--resume" or "-r": resume = true; break;
                    case "--no-focus" or "-nf": break; // handled by App
                    default: positionals.Add(arg); break;
                }
            }

            var configPath = positionals.Count > 0 ? positionals[0] : null;
            var folderPath = positionals.Count > 1 ? positionals[1] : null;

            // Resolve the folder before building the UI, so startup builds it exactly once
            // (the original built it in the constructor and then again for a folder argument).
            var folderChanged = folderPath != null
                && Directory.Exists(folderPath)
                && !string.Equals(Path.GetFullPath(folderPath), Path.GetFullPath(SoundFolderPath), StringComparison.OrdinalIgnoreCase);
            if (folderChanged) SoundFolderPath = folderPath!;
            if (initialLoad || folderChanged) LoadSoundButtons();

            if (configPath != null)
            {
                LoadConfiguration(configPath);
            }

            if (pause) PauseAllTracks();
            if (resume) ResumeAllTracks();
            if (minimize) this.WindowState = WindowState.Minimized;
        }

        /// <summary>Finds the folder to load sounds from: the app folder, else its "Audio" subfolder.</summary>
        private void SetSoundFolderPath()
        {
            var baseDirectory = AppContext.BaseDirectory;
            SoundFolderPath = baseDirectory;

            if (!EnumerateAudioFiles(baseDirectory).Any())
            {
                var audioFolder = Path.Combine(baseDirectory, "Audio");
                if (Directory.Exists(audioFolder))
                {
                    SoundFolderPath = audioFolder;
                }
            }
        }

        /// <summary>Single-pass, lazy directory scan (the original scanned the folder six times at startup).</summary>
        private static IEnumerable<string> EnumerateAudioFiles(string folder)
        {
            if (!Directory.Exists(folder)) return Enumerable.Empty<string>();
            return Directory.EnumerateFiles(folder).Where(f =>
                Path.GetExtension(f).ToLowerInvariant() is ".flac" or ".mp3" or ".wav");
        }

        /// <summary>Builds one row of UI per audio file, releasing all previous tracks first.</summary>
        private void LoadSoundButtons()
        {
            StopFolderWatcher();

            foreach (var track in Tracks.Values)
            {
                StopTrack(track);
                track.Cache?.Release(); // return cached audio to the memory budget
            }
            Tracks.Clear();
            ButtonsPanel.Children.Clear();
            _pendingAttempts.Clear();
            _changedPaths.Clear();
            _rgScanQueue.Clear(); // in-flight results for old tracks are dropped by the identity check

            foreach (var audioFile in EnumerateAudioFiles(SoundFolderPath)
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var track = CreateTrackRow(audioFile);
                Tracks[audioFile] = track;
                ButtonsPanel.Children.Add(track.Row);
                QueueReplayGainScan(track); // queued in display order, so labels fill top-to-bottom
            }

            StartFolderWatcher();
            UpdateEmptyState();
        }

        /// <summary>
        /// When no tracks exist, says so and names the scanned folder — an empty
        /// window is otherwise indistinguishable from a wrong folder, an empty one,
        /// or a launch from a location without any audio next to it.
        /// </summary>
        private void UpdateEmptyState()
        {
            if (_emptyStateLabel != null)
            {
                ButtonsPanel.Children.Remove(_emptyStateLabel);
                _emptyStateLabel = null;
            }

            if (Tracks.Count > 0) return;

            _emptyStateLabel = new TextBlock
            {
                Foreground = Brushes.Gray,
                Margin = new Thickness(10),
                TextWrapping = TextWrapping.Wrap,
                Text = "No audio files (.flac, .mp3, .wav) found in:\n" +
                       SoundFolderPath +
                       "\n\nDrop audio files into that folder and they'll appear here automatically, " +
                       "or pass a folder as the second command-line argument (after a config path)."
            };
            ButtonsPanel.Children.Add(_emptyStateLabel);
        }

        /// <summary>Builds the UI row for one audio file and its backing Track.</summary>
        private Track CreateTrackRow(string audioFile)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            var positionLabel = new Label
            {
                Content = "00:00:00",
                Foreground = Brushes.White,
                Width = 60,
                Margin = new Thickness(5),
                HorizontalContentAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            row.Children.Add(positionLabel);

            var loopLabel = new Label
            {
                Content = "000",
                Foreground = Brushes.White,
                Width = 30,
                Margin = new Thickness(5),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            row.Children.Add(loopLabel);

            var displayName = Path.GetFileNameWithoutExtension(audioFile);
            row.Children.Add(new Label { Content = displayName, Foreground = Brushes.White, Width = 150, Margin = new Thickness(5) });

            var gainLabel = new Label
            {
                Content = string.Empty, // filled in once ReplayGain tags are read at first play
                Foreground = Brushes.White,
                Width = 60,
                Margin = new Thickness(5),
                HorizontalContentAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            row.Children.Add(gainLabel);

            var playButton = new ToggleButton { Content = "Play", Width = 75, Margin = new Thickness(5) };
            playButton.Checked += PlayToggleButton_Checked;
            playButton.Unchecked += PlayToggleButton_Unchecked;
            row.Children.Add(playButton);

            var volumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 100, Margin = new Thickness(5) }; // halved to make room for pan
            volumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            row.Children.Add(volumeSlider);

            var volumeBox = new TextBox
            {
                Width = 50,
                Text = 0.5.ToString("0.000"),
                Margin = new Thickness(5),
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            volumeBox.TextChanged += VolumeTextBox_TextChanged;
            row.Children.Add(volumeBox);

            var fluctuateBox = new CheckBox
            {
                Margin = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Slowly fluctuate this track's volume while it plays (slider sets the peak)"
            };
            fluctuateBox.Checked += FluctuateCheckBox_Changed;
            fluctuateBox.Unchecked += FluctuateCheckBox_Changed;
            row.Children.Add(fluctuateBox);

            var panSlider = new Slider
            {
                Minimum = -1,
                Maximum = 1,
                Value = 0,
                Width = 100,
                Margin = new Thickness(5),
                TickPlacement = TickPlacement.BottomRight,
                Ticks = new DoubleCollection { 0 }, // a notch marks dead center
                ToolTip = "Pan (left / right)"
            };
            panSlider.ValueChanged += PanSlider_ValueChanged;
            row.Children.Add(panSlider);

            var panWanderBox = new CheckBox
            {
                Margin = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Slowly wander the pan around its set position while the track plays"
            };
            panWanderBox.Checked += PanWanderCheckBox_Changed;
            panWanderBox.Unchecked += PanWanderCheckBox_Changed;
            row.Children.Add(panWanderBox);

            object resetContent;
            try
            {
                resetContent = new Image
                {
                    Source = new BitmapImage(new Uri("pack://application:,,,/reset.png")),
                    Width = 12,
                    Height = 12
                };
            }
            catch
            {
                resetContent = "\u21BA"; // ↺ glyph fallback; a missing icon must never break row construction
            }

            var resetButton = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(5),
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Template = RoundButtonTemplate,
                Content = resetContent,
                ToolTip = "Reset this track: stop, uncheck everything, volume 0.5, pan centered"
            };
            resetButton.Click += TrackResetButton_Click;
            row.Children.Add(resetButton);

            var track = new Track
            {
                FilePath = audioFile,
                DisplayName = displayName,
                PlayButton = playButton,
                VolumeSlider = volumeSlider,
                VolumeBox = volumeBox,
                PositionLabel = positionLabel,
                LoopLabel = loopLabel,
                GainLabel = gainLabel,
                FluctuateBox = fluctuateBox,
                PanSlider = panSlider,
                PanWanderBox = panWanderBox,
                Row = row
            };

            // Controls carry their Track so handlers need no dictionary lookups.
            row.Tag = track;
            playButton.Tag = track;
            volumeSlider.Tag = track;
            volumeBox.Tag = track;
            fluctuateBox.Tag = track;
            panSlider.Tag = track;
            panWanderBox.Tag = track;
            resetButton.Tag = track;
            resetButton.Tag = track;

            return track;
        }

        /// <summary>Adds a newly discovered file, inserting its row at the alphabetical position.</summary>
        private void AddTrack(string audioFile)
        {
            var track = CreateTrackRow(audioFile);
            Tracks[audioFile] = track;

            var newName = Path.GetFileName(audioFile);
            var index = 0;
            foreach (var child in ButtonsPanel.Children)
            {
                if (child is StackPanel { Tag: Track existing } &&
                    StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(existing.FilePath), newName) < 0)
                {
                    index++;
                }
                else
                {
                    break;
                }
            }
            ButtonsPanel.Children.Insert(index, track.Row);
            QueueReplayGainScan(track);
            UpdateEmptyState();
        }

        /// <summary>Removes a track whose file disappeared, stopping it first if needed.</summary>
        private void RemoveTrack(Track track)
        {
            StopTrack(track); // also cancels any in-flight load
            track.Cache?.Release();
            Tracks.Remove(track.FilePath);
            ButtonsPanel.Children.Remove(track.Row);
            UpdateEmptyState();
        }

        // ---------------------------------------------------------------------
        // Live folder watching
        // ---------------------------------------------------------------------

        private void StartFolderWatcher()
        {
            if (!Directory.Exists(SoundFolderPath)) return;

            try
            {
                var watcher = new FileSystemWatcher(SoundFolderPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 32768 // insurance for bursts; an overflow still self-heals via Error -> rescan
                };
                watcher.Filters.Add("*.flac");
                watcher.Filters.Add("*.mp3");
                watcher.Filters.Add("*.wav");

                watcher.Created += (s, e) => ScheduleRescan(null);
                watcher.Deleted += (s, e) => ScheduleRescan(null);
                watcher.Renamed += (s, e) => ScheduleRescan(null);
                watcher.Changed += (s, e) => ScheduleRescan(e.FullPath);
                watcher.Error += (s, e) => ScheduleRescan(null);

                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not watch sound folder: {ex.Message}");
            }
        }

        private void StopFolderWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        /// <summary>
        /// Watcher events arrive on a threadpool thread and in bursts (one file copy
        /// raises several). Marshal to the UI thread and debounce into a single rescan.
        /// </summary>
        private void ScheduleRescan(string? changedPath)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (changedPath != null) _changedPaths.Add(changedPath);
                _rescanTimer.Stop();
                _rescanTimer.Interval = TimeSpan.FromMilliseconds(500);
                _rescanTimer.Start();
            });
        }

        private void RescanTimer_Tick(object? sender, EventArgs e)
        {
            _rescanTimer.Stop();
            ReconcileSoundFolder();

            if (_pendingAttempts.Count > 0)
            {
                // Some new files aren't readable yet (still being copied); poll again shortly.
                _rescanTimer.Interval = TimeSpan.FromSeconds(1);
                _rescanTimer.Start();
            }
        }

        /// <summary>
        /// Diffs the folder against the current track list. One idempotent pass covers
        /// creates, deletes, and renames in any order, and recovers from watcher overflows.
        /// Playing tracks that still exist are never disturbed.
        /// </summary>
        private void ReconcileSoundFolder()
        {
            if (!Directory.Exists(SoundFolderPath))
            {
                // Folder vanished (e.g. removable drive): clear the UI and stop watching.
                foreach (var track in Tracks.Values.ToList()) RemoveTrack(track);
                _pendingAttempts.Clear();
                _changedPaths.Clear();
                StopFolderWatcher();
                return;
            }

            var onDisk = new HashSet<string>(EnumerateAudioFiles(SoundFolderPath), StringComparer.OrdinalIgnoreCase);

            foreach (var track in Tracks.Values.Where(t => !onDisk.Contains(t.FilePath)).ToList())
            {
                RemoveTrack(track);
            }

            foreach (var stale in _pendingAttempts.Keys.Where(p => !onDisk.Contains(p)).ToList())
            {
                _pendingAttempts.Remove(stale);
            }

            foreach (var path in onDisk.Where(p => !Tracks.ContainsKey(p)).ToList())
            {
                if (IsFileReady(path))
                {
                    AddTrack(path);
                    _pendingAttempts.Remove(path);
                }
                else
                {
                    var attempts = _pendingAttempts.GetValueOrDefault(path) + 1;
                    if (attempts > MaxReadyAttempts)
                    {
                        Debug.WriteLine($"Giving up waiting for '{path}' to become readable.");
                        _pendingAttempts.Remove(path);
                    }
                    else
                    {
                        _pendingAttempts[path] = attempts;
                    }
                }
            }

            // Files overwritten in place: drop the stale decoded copy and stored
            // ReplayGain so the next play picks up the new audio and tags. If it's
            // playing, the immutable in-memory buffer keeps looping seamlessly and
            // everything is refreshed on stop instead.
            foreach (var changedPath in _changedPaths)
            {
                if (!Tracks.TryGetValue(changedPath, out var track)) continue;
                if (track.IsPlaying)
                {
                    track.CacheStale = true;
                }
                else
                {
                    track.Cache?.Release();
                    track.Cache = null;
                    track.ReplayGainKnown = false;
                    track.ReplayGainDb = null;
                    UpdateGainLabel(track);
                    QueueReplayGainScan(track); // re-read tags from the replaced file
                }
            }
            _changedPaths.Clear();
        }

        /// <summary>
        /// A freshly dropped file may still be held open by whatever is writing it.
        /// Demand a brief exclusive handle: while a copier or writer holds the file
        /// this fails, and the rescan timer retries until <see cref="MaxReadyAttempts"/>.
        /// </summary>
        private static bool IsFileReady(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return stream.Length > 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static Track? TrackOf(object sender) => (sender as FrameworkElement)?.Tag as Track;

        /// <summary>One shared circular template for the per-track reset buttons, built in
        /// code so no XAML files need to change. Hover feedback via a template trigger.</summary>
        private static readonly ControlTemplate RoundButtonTemplate = BuildRoundButtonTemplate();

        private static ControlTemplate BuildRoundButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12)); // half the 24px button = a circle
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.LightGray, "border"));
            template.Triggers.Add(hover);

            return template;
        }

        // ---------------------------------------------------------------------
        // Playback
        // ---------------------------------------------------------------------

        private async void PlayToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            if (TrackOf(sender) is { } track)
            {
                await StartTrackAsync(track);
            }
        }

        private void PlayToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (TrackOf(sender) is { } track)
            {
                StopTrack(track);
            }
        }

        /// <summary>
        /// Starts a track. Decoding/IO happens on a worker thread so the UI never
        /// blocks; cached tracks restart instantly on subsequent plays.
        /// </summary>
        private async Task StartTrackAsync(Track track)
        {
            if (track.IsPlaying) return;

            var version = ++track.RequestVersion;
            track.PlayButton.Content = "Stop";
            track.PlayButton.Background = Brushes.LightBlue;

            try
            {
                ILoopingSource source;
                CachedSound? loadedCache = null;
                var filePath = track.FilePath;

                if (track.Cache != null)
                {
                    source = new CachedLoopingSource(track.Cache);
                    if (!track.ReplayGainKnown)
                    {
                        (track.ReplayGainScale, track.ReplayGainDb) = await Task.Run(() => ReplayGain.Read(filePath));
                        track.ReplayGainKnown = true;
                    }
                }
                else if (track.ReplayGainKnown)
                {
                    (source, loadedCache) = await Task.Run(() => AudioEngine.LoadSource(filePath));
                }
                else
                {
                    var (loaded, rg) = await Task.Run(() =>
                        (AudioEngine.LoadSource(filePath), ReplayGain.Read(filePath)));
                    (source, loadedCache) = loaded;
                    (track.ReplayGainScale, track.ReplayGainDb) = rg;
                    track.ReplayGainKnown = true;
                }

                // File metadata is valid regardless of what happens to this play request.
                UpdateGainLabel(track);

                // Cancelled (stopped, reset, or reloaded) while decoding?
                if (version != track.RequestVersion || track.PlayButton.IsChecked != true)
                {
                    (source as IDisposable)?.Dispose();
                    loadedCache?.Release();
                    return;
                }

                if (loadedCache != null) track.Cache = loadedCache;

                var balance = new StereoBalanceSampleProvider(source);
                var gain = new VolumeSampleProvider(balance) { Volume = EffectiveVolume(track) };

                track.Source = source;
                track.Balance = balance;
                track.Gain = gain;

                // Fresh glides every play, easing out from the set levels rather than
                // resuming a stale leg from the previous session.
                if (track.Fluctuate) StartWobbleLeg(track, from: 1.0);
                if (track.PanWander) StartPanLeg(track, from: track.Pan);
                UpdateTrackPan(track);

                Engine.AddInput(gain);

                if (!UiTimer.IsEnabled) UiTimer.Start();
                UpdateWobbleTimer();
                UpdateWindowTitle();
            }
            catch (Exception ex)
            {
                if (version == track.RequestVersion)
                {
                    track.PlayButton.IsChecked = false;
                    track.PlayButton.Content = "Play";
                    track.PlayButton.Background = Brushes.White;
                    MessageBox.Show(this, $"Could not play '{track.DisplayName}':\n{ex.Message}",
                        "Ome", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void StopTrack(Track track)
        {
            track.RequestVersion++; // cancels any in-flight load

            if (track.Gain != null) Engine.RemoveInput(track.Gain);
            (track.Source as IDisposable)?.Dispose(); // streamed sources hold a file reader
            track.Source = null;
            track.Balance = null;
            track.Gain = null;

            if (track.CacheStale)
            {
                track.Cache?.Release();
                track.Cache = null;
                track.CacheStale = false;
                track.ReplayGainKnown = false;
                track.ReplayGainDb = null;
                UpdateGainLabel(track);
                QueueReplayGainScan(track); // re-read tags from the replaced file
            }

            track.PlayButton.Content = "Play";
            track.PlayButton.Background = Brushes.White;
            SetLabel(track.PositionLabel, "00:00:00");
            SetLabel(track.LoopLabel, "000");

            track.WobbleFactor = 1.0;
            track.WobbleElapsed = 0;
            if (track.Fluctuate) ShowWobbledVolume(track); // rest the display at the set level
            track.PanCurrent = track.Pan;
            if (track.PanWander) ShowWanderedPan(track);   // rest the display at the set position

            if (UiTimer.IsEnabled && !Tracks.Values.Any(t => t.IsPlaying)) UiTimer.Stop();
            UpdateWobbleTimer();
            UpdateWindowTitle();
        }

        /// <summary>
        /// A mixer input only stops producing samples if its file died mid-playback;
        /// reflect that in the UI. Raised on the audio thread, so marshal across.
        /// </summary>
        private void Engine_InputEnded(object? sender, SampleProviderEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var track = Tracks.Values.FirstOrDefault(t => ReferenceEquals(t.Gain, e.SampleProvider));
                if (track != null)
                {
                    track.PlayButton.IsChecked = false; // Unchecked handler performs the cleanup
                }
            });
        }

        /// <summary>Pauses all currently playing tracks (they hold position and stay in the mixer, silent).</summary>
        public void PauseAllTracks()
        {
            foreach (var track in Tracks.Values.Where(t => t.IsPlaying))
            {
                track.Source!.Paused = true;
                track.PlayButton.Content = "Resume";
            }

            PlayPauseToggleButton.IsChecked = true;
            UpdateWindowTitle();
        }

        /// <summary>Resumes all previously paused tracks.</summary>
        public void ResumeAllTracks()
        {
            foreach (var track in Tracks.Values.Where(t => t.IsPlaying))
            {
                track.Source!.Paused = false;
                track.PlayButton.Content = "Stop";
            }

            PlayPauseToggleButton.IsChecked = false;
            UpdateWindowTitle();
        }

        private void PlayPauseToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlayPauseToggleButton.IsChecked == true)
            {
                PauseAllTracks();
            }
            else
            {
                ResumeAllTracks();
            }
        }

        /// <summary>
        /// Returns one track to its default state: stopped (which zeroes the position
        /// and loop counters), fluctuation and pan wander off, volume 0.5, pan centered.
        /// Everything flows through the normal control handlers, and it's idempotent —
        /// values already at defaults fire nothing.
        /// </summary>
        private void ResetTrack(Track track)
        {
            track.PlayButton.IsChecked = false;   // fires Unchecked -> StopTrack
            track.FluctuateBox.IsChecked = false;
            track.PanWanderBox.IsChecked = false;
            track.VolumeSlider.Value = 0.5;       // fires ValueChanged -> syncs box + volume
            track.PanSlider.Value = 0;
        }

        private void TrackResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (TrackOf(sender) is { } track) ResetTrack(track);
        }

        /// <summary>Stops everything and restores defaults.</summary>
        public void ResetAllTracks()
        {
            foreach (var track in Tracks.Values)
            {
                ResetTrack(track);
            }

            PlayPauseToggleButton.IsChecked = false;
            UpdateWindowTitle();
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e) => ResetAllTracks();

        // ---------------------------------------------------------------------
        // Volume
        // ---------------------------------------------------------------------

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingWobbleUi) return; // wobble display update, not a user action
            if (TrackOf(sender) is not { } track) return;

            track.Volume = e.NewValue;
            UpdateTrackGain(track);

            var text = e.NewValue.ToString("0.000");
            if (track.VolumeBox.Text != text) track.VolumeBox.Text = text;
        }

        private void VolumeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingWobbleUi) return; // wobble display update, not a user action
            if (TrackOf(sender) is not { } track) return;

            if (double.TryParse(track.VolumeBox.Text, out var volume) && volume >= 0 && volume <= 1)
            {
                track.Volume = volume;
                UpdateTrackGain(track);

                if (Math.Abs(track.VolumeSlider.Value - volume) > 0.0005)
                {
                    track.VolumeSlider.Value = volume;
                }
            }
        }

        /// <summary>Global volume is applied once at the engine's master stage, not per track.</summary>
        private void GlobalVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            GlobalVolume = e.NewValue;
            Engine.MasterVolume = (float)e.NewValue;
        }

        /// <summary>The user's slider volume, scaled by ReplayGain (when enabled) and the wobble factor.</summary>
        private float EffectiveVolume(Track track) =>
            (float)(track.Volume * (UseReplayGain ? track.ReplayGainScale : 1.0) * track.WobbleFactor);

        private void UpdateTrackGain(Track track)
        {
            if (track.Gain != null) track.Gain.Volume = EffectiveVolume(track);
        }

        private void PanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingWobbleUi) return; // wander display update, not a user action
            if (TrackOf(sender) is not { } track) return;

            track.Pan = e.NewValue; // while wandering this recenters the roam; the drift catches up next glide
            UpdateTrackPan(track);
        }

        /// <summary>Applies the effective pan: the wander position while wandering, else the slider baseline.</summary>
        private static void UpdateTrackPan(Track track)
        {
            if (track.Balance != null)
            {
                track.Balance.Pan = (float)(track.PanWander ? track.PanCurrent : track.Pan);
            }
        }

        /// <summary>Called from the menu checkbox. Takes effect live on playing tracks.</summary>
        public void SetReplayGainEnabled(bool enabled)
        {
            if (UseReplayGain == enabled) return;
            UseReplayGain = enabled;
            SaveAppSettings();

            foreach (var track in Tracks.Values)
            {
                UpdateTrackGain(track);
            }
        }

        // ---------------------------------------------------------------------
        // Volume fluctuation
        // ---------------------------------------------------------------------

        private void FluctuateCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (TrackOf(sender) is not { } track) return;

            track.Fluctuate = track.FluctuateBox.IsChecked == true;
            if (track.Fluctuate)
            {
                StartWobbleLeg(track, from: 1.0); // ease out of the user's level into the wander
            }
            else
            {
                track.WobbleFactor = 1.0;
                UpdateTrackGain(track);
                ShowWobbledVolume(track); // snaps slider and number back to the set level
            }
            UpdateWobbleTimer();
        }

        /// <summary>Begins a new glide from the given factor to a random target over a random duration.</summary>
        private void StartWobbleLeg(Track track, double from)
        {
            track.WobbleFrom = from;
            track.WobbleTo = WobbleMinFactor + _wobbleRandom.NextDouble() * (WobbleMaxFactor - WobbleMinFactor);
            track.WobbleDuration = WobbleLegMinSeconds + _wobbleRandom.NextDouble() * (WobbleLegMaxSeconds - WobbleLegMinSeconds);
            track.WobbleElapsed = 0;
        }

        private void PanWanderCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (TrackOf(sender) is not { } track) return;

            track.PanWander = track.PanWanderBox.IsChecked == true;
            if (track.PanWander)
            {
                StartPanLeg(track, from: track.Pan); // drift away from the set position
            }
            else
            {
                track.PanCurrent = track.Pan;
                UpdateTrackPan(track);
                ShowWanderedPan(track); // snaps the slider back to the set position
            }
            UpdateWobbleTimer();
        }

        /// <summary>Begins a pan glide toward a random target within the roam span around the baseline.</summary>
        private void StartPanLeg(Track track, double from)
        {
            track.PanFrom = from;
            track.PanCurrent = from;
            var lo = Math.Max(-1.0, track.Pan - PanWanderSpan);
            var hi = Math.Min(1.0, track.Pan + PanWanderSpan);
            track.PanTo = lo + _wobbleRandom.NextDouble() * (hi - lo);
            track.PanDuration = PanLegMinSeconds + _wobbleRandom.NextDouble() * (PanLegMaxSeconds - PanLegMinSeconds);
            track.PanElapsed = 0;
        }

        private void WobbleTimer_Tick(object? sender, EventArgs e)
        {
            var interval = _wobbleTimer.Interval.TotalSeconds;

            foreach (var track in Tracks.Values)
            {
                // Paused tracks freeze mid-glide; silent audio with dancing controls would just confuse.
                if (track.Source is not { } source || source.Paused) continue;

                if (track.Fluctuate)
                {
                    track.WobbleElapsed += interval;
                    var progress = track.WobbleDuration <= 0 ? 1.0 : Math.Min(1.0, track.WobbleElapsed / track.WobbleDuration);
                    var eased = progress * progress * (3.0 - 2.0 * progress); // smoothstep: no corners at glide ends
                    track.WobbleFactor = track.WobbleFrom + (track.WobbleTo - track.WobbleFrom) * eased;

                    if (progress >= 1.0) StartWobbleLeg(track, from: track.WobbleTo);

                    UpdateTrackGain(track);   // what you hear...
                    ShowWobbledVolume(track); // ...and what you see, from the same value
                }

                if (track.PanWander)
                {
                    track.PanElapsed += interval;
                    var progress = track.PanDuration <= 0 ? 1.0 : Math.Min(1.0, track.PanElapsed / track.PanDuration);
                    var eased = progress * progress * (3.0 - 2.0 * progress);
                    track.PanCurrent = track.PanFrom + (track.PanTo - track.PanFrom) * eased;

                    if (progress >= 1.0) StartPanLeg(track, from: track.PanTo);

                    UpdateTrackPan(track);
                    ShowWanderedPan(track);
                }
            }
        }

        /// <summary>
        /// Reflects the current effective level (volume x wobble) into the slider and
        /// number box, suppressed while the user is dragging the slider or typing in
        /// the box so the display never fights their input.
        /// </summary>
        private void ShowWobbledVolume(Track track)
        {
            var displayed = Math.Clamp(track.Volume * track.WobbleFactor, 0, 1);

            _updatingWobbleUi = true;
            try
            {
                if (!track.VolumeSlider.IsMouseCaptureWithin)
                {
                    track.VolumeSlider.Value = displayed;
                }

                if (!track.VolumeBox.IsKeyboardFocused)
                {
                    var text = displayed.ToString("0.000");
                    if (track.VolumeBox.Text != text) track.VolumeBox.Text = text;
                }
            }
            finally
            {
                _updatingWobbleUi = false;
            }
        }

        /// <summary>Reflects the wandering pan into its slider, unless the user is dragging it.</summary>
        private void ShowWanderedPan(Track track)
        {
            var displayed = Math.Clamp(track.PanWander ? track.PanCurrent : track.Pan, -1, 1);

            _updatingWobbleUi = true;
            try
            {
                if (!track.PanSlider.IsMouseCaptureWithin)
                {
                    track.PanSlider.Value = displayed;
                }
            }
            finally
            {
                _updatingWobbleUi = false;
            }
        }

        /// <summary>Runs the wobble timer only while at least one playing track fluctuates or pan-wanders.</summary>
        private void UpdateWobbleTimer()
        {
            var needed = Tracks.Values.Any(t => (t.Fluctuate || t.PanWander) && t.IsPlaying);
            if (needed && !_wobbleTimer.IsEnabled) _wobbleTimer.Start();
            else if (!needed && _wobbleTimer.IsEnabled) _wobbleTimer.Stop();
        }

        // ---------------------------------------------------------------------
        // App settings (application-level preferences; unlike soundscape configs,
        // these persist automatically in %AppData%\Ome\settings.json)
        // ---------------------------------------------------------------------

        private void LoadAppSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                UseReplayGain = settings?.UseReplayGain ?? false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not load settings: {ex.Message}");
            }
        }

        private void SaveAppSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(new AppSettings { UseReplayGain = UseReplayGain }, JsonOptions));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not save settings: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // UI refresh
        // ---------------------------------------------------------------------

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var track in Tracks.Values)
            {
                if (track.Source is not { } source) continue;
                SetLabel(track.PositionLabel, source.CurrentTime.ToString(@"hh\:mm\:ss"));
                SetLabel(track.LoopLabel, source.LoopCount.ToString("D3"));
            }
        }

        /// <summary>Assigns only on change, so unchanged labels don't invalidate layout twice a second.</summary>
        private static void SetLabel(Label label, string text)
        {
            if (!Equals(label.Content, text)) label.Content = text;
        }

        /// <summary>
        /// Shows the detected ReplayGain next to the track: green for a boost, red
        /// for a cut, plain for exactly 0 dB, a grey dash for files that were checked
        /// and carry no tags, and blank while the tags haven't been read yet.
        /// </summary>
        private static void UpdateGainLabel(Track track)
        {
            if (!track.ReplayGainKnown)
            {
                track.GainLabel.Foreground = Brushes.White;
                SetLabel(track.GainLabel, string.Empty);
                return;
            }

            if (track.ReplayGainDb is not double db)
            {
                track.GainLabel.Foreground = Brushes.Gray;
                SetLabel(track.GainLabel, "—");
                return;
            }

            var rounded = Math.Round(db, 1); // color follows the displayed value, so "+0.0" is never red/green
            track.GainLabel.Foreground = rounded > 0 ? Brushes.LimeGreen
                                       : rounded < 0 ? Brushes.Tomato
                                       : Brushes.White;
            SetLabel(track.GainLabel, rounded.ToString("+0.0;-0.0;0.0") + " dB");
        }

        /// <summary>Queues a background read of the track's ReplayGain tags.</summary>
        private void QueueReplayGainScan(Track track)
        {
            if (track.ReplayGainKnown) return;
            _rgScanQueue.Enqueue(track);
            PumpReplayGainScans();
        }

        /// <summary>
        /// Drains the scan queue one file at a time on a worker thread, filling gain
        /// labels progressively. All queue state is touched only on the UI thread
        /// (awaits resume here), so no locking is needed. Tracks removed or rebuilt
        /// while their scan was in flight fail the identity check and are dropped.
        /// The lazy read in StartTrackAsync remains the fallback, so a play click
        /// never waits on this queue and is correctly gained from the first sample.
        /// </summary>
        private async void PumpReplayGainScans()
        {
            if (_rgScanRunning) return;
            _rgScanRunning = true;
            try
            {
                while (_rgScanQueue.Count > 0)
                {
                    var track = _rgScanQueue.Dequeue();
                    if (track.ReplayGainKnown) continue;

                    var filePath = track.FilePath;
                    if (!Tracks.TryGetValue(filePath, out var current) || !ReferenceEquals(current, track)) continue;

                    var rg = await Task.Run(() => ReplayGain.Read(filePath));

                    // Back on the UI thread: still the current track for that path?
                    if (!Tracks.TryGetValue(filePath, out current) || !ReferenceEquals(current, track)) continue;
                    if (track.ReplayGainKnown) continue; // a play beat us to it

                    (track.ReplayGainScale, track.ReplayGainDb) = rg;
                    track.ReplayGainKnown = true;
                    UpdateGainLabel(track);
                    UpdateTrackGain(track); // if it started playing mid-scan, apply the gain live
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReplayGain scan failed: {ex.Message}");
            }
            finally
            {
                _rgScanRunning = false;
            }
        }

        private void UpdateWindowTitle()
        {
            const string BaseTitle = "Ome - Ambient Soundscape Mixer";

            var playing = Tracks.Values.Where(t => t.IsPlaying).ToList();
            if (playing.Count == 0)
            {
                this.Title = $"{BaseTitle} - Stopped";
                return;
            }

            var anyPlaying = playing.Any(t => !t.Source!.Paused);
            var isPlayingTheMile = playing.Any(t => t.FilePath.Contains("The Mile", StringComparison.OrdinalIgnoreCase));
            var theMileSuffix = isPlayingTheMile ? " - The Mile" : "";

            this.Title = anyPlaying
                ? $"{BaseTitle} - Playing{theMileSuffix}"
                : $"{BaseTitle} - Paused{theMileSuffix}";
        }

        // ---------------------------------------------------------------------
        // Window plumbing
        // ---------------------------------------------------------------------

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (this.WindowState == WindowState.Normal && ((App)Application.Current).NoFocus)
            {
                this.ShowActivated = false;
            }
        }

        /// <summary>Adjusts the window width to fit the audio track controls.</summary>
        private void AdjustWindowWidth()
        {
            const double positionWidth = 60, loopWidth = 30, nameWidth = 150, gainWidth = 60;
            const double playWidth = 75, volumeSliderWidth = 100, volumeBoxWidth = 50;
            const double panSliderWidth = 100;
            const double checkBoxesWidth = 20 * 2;        // fluctuate + pan wander
            const double resetButtonWidth = 24;
            const double marginsAndChrome = 110 + 40;     // 10px per control + window chrome

            this.Width = positionWidth + loopWidth + nameWidth + gainWidth + playWidth
                       + volumeSliderWidth + volumeBoxWidth + panSliderWidth
                       + checkBoxesWidth + resetButtonWidth + marginsAndChrome;
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var configWindow = new ConfigWindow { Owner = this };
            configWindow.ShowDialog();
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Configs are manual snapshots: nothing is written at close. Saving
            // happens only through the menu's Save Config button.
            StopFolderWatcher();
            _rescanTimer.Stop();
            _wobbleTimer.Stop();
            UiTimer.Stop();
            Engine.Dispose(); // stops the device (and its pull on the sources) first

            foreach (var track in Tracks.Values)
            {
                (track.Source as IDisposable)?.Dispose();
                track.Source = null;
                track.Gain = null;
            }
        }

        // ---------------------------------------------------------------------
        // Configuration (same JSON schema as before; Newtonsoft replaced by System.Text.Json)
        // ---------------------------------------------------------------------

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>Saves volumes, play states, and window placement. Now covers every track,
        /// not just the ones whose sliders had incidentally been touched.</summary>
        public void SaveConfiguration(string filePath)
        {
            if (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                filePath += ".json";
            }

            var configData = new ConfigData
            {
                Tracks = Tracks.Values.Select(t => new TrackConfig
                {
                    FilePath = t.FilePath,
                    // The play button reflects intent from the moment a track is asked
                    // to play; Source-based IsPlaying only turns true once the worker
                    // thread finishes decoding, so a save during that gap would record
                    // loading tracks as stopped.
                    IsPlaying = t.PlayButton.IsChecked == true,
                    Volume = t.Volume,
                    Fluctuate = t.Fluctuate,
                    Pan = t.Pan,
                    PanWander = t.PanWander
                }).ToList(),
                Window = new WindowConfig
                {
                    Width = this.Width,
                    Height = this.Height,
                    Left = this.Left,
                    Top = this.Top,
                    GlobalVolume = this.GlobalVolume
                }
            };

            var json = JsonSerializer.Serialize(configData, JsonOptions);

            // Write-then-replace is atomic: an interrupted save (crash, shutdown)
            // leaves the previous config intact instead of a truncated file.
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }

        public void LoadConfiguration(string filePath)
        {
            if (!File.Exists(filePath)) return;

            ConfigData? configData;
            try
            {
                configData = JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(filePath), JsonOptions);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not read configuration:\n{ex.Message}",
                    "Ome", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (configData == null) return;

            // Back to defaults through the single reset path.
            foreach (var track in Tracks.Values)
            {
                ResetTrack(track);
            }
            PlayPauseToggleButton.IsChecked = false;

            if (configData.Window is { } window)
            {
                if (window.Width > 0) this.Width = window.Width;
                if (window.Height > 0) this.Height = window.Height;

                // Keep the window reachable even if the config came from another monitor setup.
                this.Left = Math.Clamp(window.Left,
                    SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100);
                this.Top = Math.Clamp(window.Top,
                    SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100);

                GlobalVolumeSlider.Value = Math.Clamp(window.GlobalVolume, 0, 1); // handler updates engine
            }

            foreach (var trackConfig in configData.Tracks ?? Enumerable.Empty<TrackConfig>())
            {
                if (trackConfig.FilePath == null) continue;
                if (!Tracks.TryGetValue(trackConfig.FilePath, out var track)) continue;

                track.VolumeSlider.Value = Math.Clamp(trackConfig.Volume, 0, 1);
                track.PanSlider.Value = Math.Clamp(trackConfig.Pan, -1, 1);
                track.FluctuateBox.IsChecked = trackConfig.Fluctuate; // before play, so it wanders from the first glide
                track.PanWanderBox.IsChecked = trackConfig.PanWander;
                if (trackConfig.IsPlaying)
                {
                    track.PlayButton.IsChecked = true; // Checked handler starts playback
                }
            }

            UpdateWindowTitle();
        }
    }

    /// <summary>Application-level preferences, persisted in %AppData%\Ome\settings.json.</summary>
    public class AppSettings
    {
        public bool UseReplayGain { get; set; }
    }

    public class TrackConfig
    {
        public string? FilePath { get; set; }
        public bool IsPlaying { get; set; }
        public double Volume { get; set; }
        public bool Fluctuate { get; set; }
        public double Pan { get; set; }
        public bool PanWander { get; set; }
    }

    public class WindowConfig
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double GlobalVolume { get; set; }
    }

    public class ConfigData
    {
        public List<TrackConfig>? Tracks { get; set; }
        public WindowConfig? Window { get; set; }
    }
}
