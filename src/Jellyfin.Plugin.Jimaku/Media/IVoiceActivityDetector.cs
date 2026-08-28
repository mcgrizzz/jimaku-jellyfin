using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Detects speech in a stream of mono 16 kHz PCM samples.
/// </summary>
/// <remarks>
/// Implemented in-process by <see cref="BandEnergyVoiceActivityDetector"/>, and optionally by a
/// Silero ONNX detector supplied by the separate <c>Jellyfin.Plugin.Jimaku.Silero</c> assembly.
/// The optional path is kept behind this interface because ONNX Runtime carries roughly 185 MB of
/// native binaries across all platforms, which is far too much to bundle for a fallback.
/// </remarks>
public interface IVoiceActivityDetector : IDisposable
{
    /// <summary>Gets a short name for logging and display.</summary>
    string Name { get; }

    /// <summary>Gets the sample rate the detector expects.</summary>
    int SampleRate { get; }

    /// <summary>Gets the number of samples per analysis frame.</summary>
    int FrameSamples { get; }

    /// <summary>
    /// Scores one frame of audio.
    /// </summary>
    /// <param name="samples">
    /// Exactly <see cref="FrameSamples"/> samples, normalized to the range -1 to 1.
    /// </param>
    /// <returns>The probability that the frame contains speech.</returns>
    float ScoreFrame(ReadOnlySpan<float> samples);

    /// <summary>Resets any internal state between files.</summary>
    void Reset();
}

/// <summary>
/// Creates the best available voice activity detector.
/// </summary>
public interface IVoiceActivityDetectorFactory
{
    /// <summary>Creates a detector.</summary>
    /// <returns>A detector; never null, since there is always a built-in fallback.</returns>
    IVoiceActivityDetector Create();
}
