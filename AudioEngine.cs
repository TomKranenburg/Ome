using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Ome
{
    /// <summary>
    /// Owns the single audio output device and the mixer that all tracks feed into.
    ///
    /// Architecture: one WasapiOut -> master VolumeSampleProvider -> MixingSampleProvider.
    /// Tracks are added/removed as mixer inputs. This replaces the previous design of
    /// one WasapiOut (device session + playback thread + buffer) per playing track.
    ///
    /// The device is created lazily on the first play and then runs continuously,
    /// emitting silence when no inputs are present (ReadFully = true). An idle
    /// shared-mode WASAPI stream costs effectively nothing and gives instant starts.
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        /// <summary>Canonical mixer format. All inputs are converted to this at load time.</summary>
        public const int SampleRate = 44100;
        public const int Channels = 2;
        private const int LatencyMs = 100;

        private readonly MixingSampleProvider _mixer;
        private readonly VolumeSampleProvider _master;
        private readonly object _sync = new();
        private IWavePlayer? _output;
        private bool _disposed;

        /// <summary>
        /// Raised (on the audio thread) when a mixer input stops producing samples,
        /// which for our always-full looping sources only happens if a file dies
        /// mid-playback (e.g. decoder failure). Marshal to the UI thread before touching UI.
        /// </summary>
        public event EventHandler<SampleProviderEventArgs>? InputEnded;

        public AudioEngine()
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
            {
                ReadFully = true // keep the device fed with silence when no tracks play
            };
            _mixer.MixerInputEnded += (s, e) => InputEnded?.Invoke(this, e);
            _master = new VolumeSampleProvider(_mixer) { Volume = 0.5f };
        }

        /// <summary>Global volume, applied once at the master stage (not per track).</summary>
        public float MasterVolume
        {
            get => _master.Volume;
            set => _master.Volume = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>Adds a track to the mix, starting the output device if needed.</summary>
        public void AddInput(ISampleProvider input)
        {
            EnsureStarted();
            _mixer.AddMixerInput(input);
        }

        /// <summary>Removes a track from the mix. Safe to call for inputs already removed.</summary>
        public void RemoveInput(ISampleProvider input) => _mixer.RemoveMixerInput(input);

        private void EnsureStarted()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AudioEngine));
                if (_output != null) return;

                var output = new WasapiOut(AudioClientShareMode.Shared, LatencyMs);
                output.Init(new SampleToWaveProvider(_master));
                output.Play();
                _output = output;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _output?.Dispose();
                _output = null;
            }
        }

        /// <summary>
        /// Opens an audio file and produces a looping source in the mixer format.
        /// Small files are decoded once into memory (gapless loops, no further disk IO
        /// or decode cost); large files fall back to streaming. Runs blocking IO/decode,
        /// so call it off the UI thread. The returned cache, if any, can be reused for
        /// instant restarts and must be Release()d when permanently discarded.
        /// </summary>
        public static (ILoopingSource Source, CachedSound? Cache) LoadSource(string filePath)
        {
            var reader = new AudioFileReader(filePath); // .wav/.mp3 native; .flac via MediaFoundationReader (Windows 10 1607+)
            var ownsReader = true;
            try
            {
                long estimatedBytes = 0;
                try
                {
                    estimatedBytes = (long)(reader.TotalTime.TotalSeconds * SampleRate * Channels * sizeof(float));
                }
                catch
                {
                    // Some MediaFoundation streams can't report a duration; treat as unknown -> stream.
                }

                if (estimatedBytes > 0 &&
                    estimatedBytes <= CachedSound.MaxBytesPerFile &&
                    CachedSound.TryReserve(estimatedBytes))
                {
                    CachedSound cache;
                    try
                    {
                        cache = new CachedSound(ConvertToMixerFormat(reader), estimatedBytes);
                    }
                    catch
                    {
                        CachedSound.ReleaseReservation(estimatedBytes);
                        throw;
                    }
                    return (new CachedLoopingSource(cache), cache);
                }

                var source = new StreamingLoopingSource(reader, ConvertToMixerFormat(reader));
                ownsReader = false; // the streaming source owns and disposes the reader
                return (source, null);
            }
            finally
            {
                if (ownsReader) reader.Dispose();
            }
        }

        /// <summary>
        /// Converts an arbitrary sample provider to the mixer format, inserting
        /// converters only when actually needed (no resampler for 44.1 kHz sources, etc).
        /// </summary>
        public static ISampleProvider ConvertToMixerFormat(ISampleProvider source)
        {
            if (source.WaveFormat.Channels == 1)
            {
                source = new MonoToStereoSampleProvider(source);
            }
            else if (source.WaveFormat.Channels > Channels)
            {
                // Rare (>2ch) case: default multiplexer mapping keeps the first two channels.
                source = new MultiplexingSampleProvider(new[] { source }, Channels);
            }

            if (source.WaveFormat.SampleRate != SampleRate)
            {
                source = new WdlResamplingSampleProvider(source, SampleRate);
            }

            return source;
        }
    }

    /// <summary>
    /// A looping mixer input. Implementations must always return the full requested
    /// count while healthy (silence when paused), because MixingSampleProvider
    /// auto-removes inputs that come up short — we use that as the failure path.
    /// </summary>
    public interface ILoopingSource : ISampleProvider
    {
        /// <summary>When true, emits silence without advancing. Set from the UI thread.</summary>
        bool Paused { get; set; }

        /// <summary>Completed passes through the file. Safe to read from the UI thread.</summary>
        int LoopCount { get; }

        /// <summary>Position within the current pass. Safe to read from the UI thread.</summary>
        TimeSpan CurrentTime { get; }
    }

    /// <summary>
    /// A file fully decoded to floats in the mixer format. Decoding happens exactly once;
    /// looping the buffer afterwards costs no disk IO and no decode CPU, and the loop
    /// seam is sample-accurate. A global memory budget keeps many/large files in check.
    /// </summary>
    public sealed class CachedSound
    {
        public const long MaxBytesPerFile = 32L * 1024 * 1024;      // ~95 s of 44.1 kHz stereo float
        private const long MaxTotalCacheBytes = 256L * 1024 * 1024; // soft budget across all tracks

        private static long _totalReserved;
        private int _released;

        public float[] Data { get; }
        public long SizeBytes { get; }

        internal CachedSound(ISampleProvider mixerFormatProvider, long reservedBytes)
        {
            var estimatedSamples = (int)Math.Min(int.MaxValue, reservedBytes / sizeof(float));
            var data = new float[Math.Max(estimatedSamples, 16384)];
            var count = 0;

            int read;
            while ((read = mixerFormatProvider.Read(data, count, data.Length - count)) > 0)
            {
                count += read;
                if (count == data.Length)
                {
                    Array.Resize(ref data, data.Length * 2); // duration estimates can undershoot slightly
                }
            }

            if (count == 0)
            {
                throw new InvalidOperationException("The file contained no decodable audio data.");
            }

            if (count != data.Length) Array.Resize(ref data, count);
            Data = data;
            SizeBytes = (long)Data.Length * sizeof(float);

            // Reconcile the reservation (made from the estimate) with the actual decoded size.
            Adjust(SizeBytes - reservedBytes);
        }

        /// <summary>Returns the memory to the cache budget. Idempotent.</summary>
        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Adjust(-SizeBytes);
        }

        internal static bool TryReserve(long bytes)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _totalReserved);
                if (current + bytes > MaxTotalCacheBytes) return false;
                if (Interlocked.CompareExchange(ref _totalReserved, current + bytes, current) == current) return true;
            }
        }

        internal static void ReleaseReservation(long bytes) => Adjust(-bytes);

        private static void Adjust(long delta) => Interlocked.Add(ref _totalReserved, delta);
    }

    /// <summary>Loops an in-memory buffer forever. Read() is lock-free and allocation-free.</summary>
    public sealed class CachedLoopingSource : ILoopingSource
    {
        private readonly float[] _data; // non-empty, guaranteed by CachedSound
        private int _position;          // interleaved sample index, written on the audio thread
        private int _loopCount;
        private volatile bool _paused;

        public CachedLoopingSource(CachedSound sound) => _data = sound.Data;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(AudioEngine.SampleRate, AudioEngine.Channels);

        public bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        public int LoopCount => Volatile.Read(ref _loopCount);

        public TimeSpan CurrentTime =>
            TimeSpan.FromTicks((long)Volatile.Read(ref _position) * TimeSpan.TicksPerSecond
                               / (AudioEngine.SampleRate * AudioEngine.Channels));

        public int Read(float[] buffer, int offset, int count)
        {
            if (_paused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            var written = 0;
            var position = _position;

            while (written < count)
            {
                var available = _data.Length - position;
                if (available == 0)
                {
                    position = 0;
                    Interlocked.Increment(ref _loopCount);
                    available = _data.Length;
                }

                var toCopy = Math.Min(available, count - written);
                Array.Copy(_data, position, buffer, offset + written, toCopy);
                position += toCopy;
                written += toCopy;
            }

            Volatile.Write(ref _position, position);
            return count;
        }
    }

    /// <summary>
    /// Loops a file from disk for sources too large to cache. Rewinds the underlying
    /// reader at end-of-stream. Unlike the original LoopStream, a source that stops
    /// yielding data cannot spin the audio thread: after bounded rewind attempts the
    /// short read is returned, and the mixer drops the input (raising InputEnded).
    /// </summary>
    public sealed class StreamingLoopingSource : ILoopingSource, IDisposable
    {
        private readonly AudioFileReader _reader;   // AudioFileReader locks internally: repositioning while playing is safe
        private readonly ISampleProvider _provider; // reader converted to the mixer format
        private int _loopCount;
        private volatile bool _paused;
        private bool _disposed;

        public StreamingLoopingSource(AudioFileReader reader, ISampleProvider mixerFormatProvider)
        {
            _reader = reader;
            _provider = mixerFormatProvider;
        }

        public WaveFormat WaveFormat => _provider.WaveFormat;

        public bool Paused
        {
            get => _paused;
            set => _paused = value;
        }

        public int LoopCount => Volatile.Read(ref _loopCount);

        public TimeSpan CurrentTime => _disposed ? TimeSpan.Zero : _reader.CurrentTime;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_paused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            var written = 0;
            var rewindsWithoutData = 0;

            while (written < count)
            {
                var read = _provider.Read(buffer, offset + written, count - written);
                if (read == 0)
                {
                    if (++rewindsWithoutData > 2) break; // dead source: return short, mixer removes us
                    try
                    {
                        _reader.Position = 0;
                    }
                    catch
                    {
                        break; // seek failed (e.g. device/decoder error): give up cleanly
                    }
                    Interlocked.Increment(ref _loopCount);
                    continue;
                }

                rewindsWithoutData = 0;
                written += read;
            }

            return written;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }
    }

    /// <summary>
    /// A stereo balance control with unity center: pan 0 is a bit-exact passthrough,
    /// and panning attenuates the far channel only (never boosts), so it cannot
    /// change the level of a centered mix or add clipping headroom problems.
    /// Pan is written from the UI thread and read on the audio thread.
    /// </summary>
    public sealed class StereoBalanceSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private volatile float _pan; // -1 = hard left .. +1 = hard right

        public StereoBalanceSampleProvider(ISampleProvider source)
        {
            if (source.WaveFormat.Channels != 2)
                throw new ArgumentException("Balance requires a stereo source.", nameof(source));
            _source = source;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public float Pan
        {
            get => _pan;
            set => _pan = Math.Clamp(value, -1f, 1f);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);

            var pan = _pan; // one read per buffer: consistent gains within it
            if (pan == 0f) return read;

            var left = Math.Min(1f, 1f - pan);
            var right = Math.Min(1f, 1f + pan);

            for (var i = 0; i + 1 < read; i += 2)
            {
                buffer[offset + i] *= left;
                buffer[offset + i + 1] *= right;
            }

            return read;
        }
    }

    /// <summary>
    /// Pure ReplayGain arithmetic, kept free of tag-reading dependencies so it can be
    /// unit tested. Values follow the ReplayGain convention: gains in dB relative to
    /// the reference level, peaks as linear amplitude relative to full scale, and
    /// double.NaN meaning "tag not present".
    /// </summary>
    public static class ReplayGainMath
    {
        /// <summary>
        /// Converts ReplayGain tags to a linear gain factor. Track values are
        /// preferred; album values are the fallback; no tags means unity gain.
        /// When the matching peak is known, the gain is limited so the boosted
        /// signal cannot exceed full scale (standard clipping prevention).
        /// </summary>
        public static double ComputeScale(double trackGainDb, double trackPeak, double albumGainDb, double albumPeak)
        {
            double gainDb, peak;
            if (!double.IsNaN(trackGainDb))
            {
                gainDb = trackGainDb;
                peak = trackPeak;
            }
            else if (!double.IsNaN(albumGainDb))
            {
                gainDb = albumGainDb;
                peak = albumPeak;
            }
            else
            {
                return 1.0;
            }

            // Sanity clamp against corrupt tags; real-world gains live well inside this.
            gainDb = Math.Clamp(gainDb, -24.0, 24.0);
            var scale = Math.Pow(10.0, gainDb / 20.0);

            if (!double.IsNaN(peak) && peak > 0)
            {
                scale = Math.Min(scale, 1.0 / peak);
            }

            return scale;
        }

        /// <summary>
        /// The gain that will actually be applied, in dB, for display purposes —
        /// i.e. the tag value after clamping and peak limiting. Null when the file
        /// carries no gain tags at all (distinct from a tag of exactly 0.0 dB).
        /// </summary>
        public static double? ComputeEffectiveDb(double trackGainDb, double trackPeak, double albumGainDb, double albumPeak)
        {
            if (double.IsNaN(trackGainDb) && double.IsNaN(albumGainDb)) return null;
            return 20.0 * Math.Log10(ComputeScale(trackGainDb, trackPeak, albumGainDb, albumPeak));
        }
    }
}
