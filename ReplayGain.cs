using System;
using System.Diagnostics;

namespace Ome
{
    /// <summary>
    /// Reads ReplayGain tags from an audio file via TagLib#, which handles the
    /// real-world tag zoo for us: Vorbis comments in FLAC, ID3v2 TXXX frames and
    /// APEv2 tags in MP3, and ID3 chunks in WAV. TagLib exposes the four values
    /// directly (double.NaN when a tag is absent); the arithmetic lives in
    /// <see cref="ReplayGainMath"/> so it stays unit-testable without TagLib.
    /// Any read failure degrades to unity gain — a missing or corrupt tag must
    /// never stop a file from playing.
    /// </summary>
    public static class ReplayGain
    {
        /// <summary>Returns the linear gain to apply and the effective dB for display (null = untagged).</summary>
        public static (double Scale, double? EffectiveDb) Read(string filePath)
        {
            try
            {
                using var file = TagLib.File.Create(filePath);
                var tag = file.Tag;
                double trackGain = tag.ReplayGainTrackGain, trackPeak = tag.ReplayGainTrackPeak;
                double albumGain = tag.ReplayGainAlbumGain, albumPeak = tag.ReplayGainAlbumPeak;

                return (ReplayGainMath.ComputeScale(trackGain, trackPeak, albumGain, albumPeak),
                        ReplayGainMath.ComputeEffectiveDb(trackGain, trackPeak, albumGain, albumPeak));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReplayGain read failed for '{filePath}': {ex.Message}");
                return (1.0, null);
            }
        }
    }
}
