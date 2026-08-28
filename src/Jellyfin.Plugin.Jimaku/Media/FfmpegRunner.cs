using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Runs the server's ffmpeg to decode an audio stream to raw mono PCM.
/// </summary>
/// <remarks>
/// The binary comes from <see cref="IMediaEncoder.EncoderPath"/>, which is the resolved and
/// validated path. The user-facing <c>EncodingOptions.EncoderAppPath</c> setting is empty on a
/// default install that uses a bundled or distro ffmpeg, so it is not usable here.
/// </remarks>
public sealed class FfmpegRunner(IMediaEncoder mediaEncoder, ILogger<FfmpegRunner> logger)
{
    /// <summary>Sample rate the audio is decoded to.</summary>
    public const int SampleRate = 16000;

    /// <summary>Gets a value indicating whether ffmpeg is available.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(mediaEncoder.EncoderPath);

    /// <summary>
    /// Decodes one audio stream and yields fixed-size frames of normalized samples.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="audioStreamIndex">The ffmpeg stream index to decode.</param>
    /// <param name="frameSamples">Samples per emitted frame.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Frames of samples in the range -1 to 1. The same array is reused for every frame to avoid
    /// allocating tens of thousands of buffers per episode, so consumers must read each frame
    /// before requesting the next and must not retain it.
    /// </returns>
    public async IAsyncEnumerable<float[]> DecodeFramesAsync(
        string mediaPath,
        int audioStreamIndex,
        int frameSamples,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSamples);

        if (!IsAvailable)
        {
            logger.LogWarning("ffmpeg is not available; cannot analyse audio.");
            yield break;
        }

        var startInfo = new ProcessStartInfo(mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList rather than a joined string: media paths routinely contain spaces, quotes
        // and brackets, and this avoids every quoting question.
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-v", "error",
                     "-i", mediaPath,
                     "-map", string.Create(CultureInfo.InvariantCulture, $"0:{audioStreamIndex}"),
                     "-vn", "-sn", "-dn",
                     "-ac", "1",
                     "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
                     "-f", "s16le",
                     "-",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain stderr concurrently; ffmpeg will block on a full pipe if nobody reads it.
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var bytesPerFrame = frameSamples * 2;
        var buffer = new byte[bytesPerFrame];
        var frame = new float[frameSamples];

        try
        {
            while (true)
            {
                var read = await ReadExactlyAsync(process.StandardOutput.BaseStream, buffer, cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                var samples = read / 2;
                for (var i = 0; i < samples; i++)
                {
                    frame[i] = (short)(buffer[i * 2] | (buffer[(i * 2) + 1] << 8)) / 32768f;
                }

                for (var i = samples; i < frameSamples; i++)
                {
                    frame[i] = 0;
                }

                yield return frame;

                if (read < bytesPerFrame)
                {
                    break;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogDebug("ffmpeg reported: {Error}", stderr.Trim());
            }
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
