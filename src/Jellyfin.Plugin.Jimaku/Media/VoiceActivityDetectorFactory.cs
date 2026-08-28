using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Supplies a voice activity detector, preferring an optional Silero implementation when one has
/// been installed alongside the plugin.
/// </summary>
/// <remarks>
/// <para>
/// Silero is a trained neural detector and discriminates speech from music far better than any
/// energy heuristic, which matters for anime. It is optional rather than bundled because it needs
/// ONNX Runtime, whose native binaries total roughly 185 MB across the platforms a Jellyfin server
/// might run on. Requiring every user to carry that for a fallback path is a poor trade.
/// </para>
/// <para>
/// To enable it, drop <c>Jellyfin.Plugin.Jimaku.Silero.dll</c>, the ONNX Runtime assemblies for the
/// server's platform, and a <c>silero_vad.onnx</c> model into the plugin directory, then set the
/// model path in the plugin settings. Anything missing or unloadable falls back silently to the
/// built-in detector.
/// </para>
/// </remarks>
public sealed class VoiceActivityDetectorFactory(ILogger<VoiceActivityDetectorFactory> logger)
    : IVoiceActivityDetectorFactory
{
    private const string SileroAssemblyName = "Jellyfin.Plugin.Jimaku.Silero";

    private bool _sileroAttempted;
    private Type? _sileroType;

    /// <summary>
    /// Gets or sets an override for the Silero model path. Normally left empty, in which case the
    /// current plugin configuration is read on each call so a settings change takes effect without
    /// restarting the server.
    /// </summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <inheritdoc />
    public IVoiceActivityDetector Create()
    {
        var silero = TryCreateSilero();
        return silero ?? new BandEnergyVoiceActivityDetector();
    }

    private IVoiceActivityDetector? TryCreateSilero()
    {
        var modelPath = string.IsNullOrWhiteSpace(ModelPath)
            ? Plugin.Instance?.Configuration.SileroModelPath ?? string.Empty
            : ModelPath;

        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return null;
        }

        if (!_sileroAttempted)
        {
            _sileroAttempted = true;
            _sileroType = LoadSileroType();
        }

        if (_sileroType is null)
        {
            return null;
        }

        try
        {
            return (IVoiceActivityDetector?)Activator.CreateInstance(_sileroType, modelPath);
        }
        catch (Exception ex)
        {
            // A missing native ONNX Runtime library surfaces here, and it must never take the
            // plugin down: fall back and carry on.
            logger.LogWarning(ex, "The Silero detector could not be created; using the built-in detector.");
            _sileroType = null;
            return null;
        }
    }

    private Type? LoadSileroType()
    {
        try
        {
            var directory = Path.GetDirectoryName(typeof(VoiceActivityDetectorFactory).Assembly.Location);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var path = Path.Combine(directory, SileroAssemblyName + ".dll");
            if (!File.Exists(path))
            {
                logger.LogDebug("No optional Silero assembly at {Path}; using the built-in detector.", path);
                return null;
            }

            var assembly = Assembly.LoadFrom(path);
            var type = assembly.GetTypes().FirstOrDefault(t =>
                typeof(IVoiceActivityDetector).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            if (type is null)
            {
                logger.LogWarning("{Assembly} contains no voice activity detector.", SileroAssemblyName);
            }
            else
            {
                logger.LogInformation("Loaded the optional Silero voice activity detector.");
            }

            return type;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Loading the optional Silero assembly failed; using the built-in detector.");
            return null;
        }
    }
}
