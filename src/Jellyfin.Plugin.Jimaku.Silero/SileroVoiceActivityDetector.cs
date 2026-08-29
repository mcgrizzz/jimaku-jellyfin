using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Media;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Jellyfin.Plugin.Jimaku.Silero;

/// <summary>
/// Voice activity detection using the Silero ONNX model.
/// </summary>
/// <remarks>
/// <para>
/// A trained neural detector, and markedly better than any energy heuristic at telling speech from
/// music. That matters here because anime is scored almost continuously, so the built-in detector
/// has a hard time and frequently ends in a decline.
/// </para>
/// <para>
/// The model is stateful and recurrent: frames must be fed in order, and the state carried between
/// them. At 16 kHz it accepts only 512-sample windows, which is a hard constraint of the model
/// rather than a choice, so its output lands on a 32 ms grid and is resampled onto the aligner's
/// 10 ms grid by the caller.
/// </para>
/// </remarks>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    private const int Rate = 16000;
    private const int Window = 512;
    private const int StateSize = 128;

    private readonly InferenceSession _session;
    private readonly string _audioInput;
    private readonly string? _stateInput;
    private readonly string? _hiddenInput;
    private readonly string? _cellInput;
    private readonly string? _sampleRateInput;

    private float[] _state = new float[2 * StateSize];
    private float[] _hidden = new float[2 * 64];
    private float[] _cell = new float[2 * 64];

    /// <summary>Initializes a new instance of the <see cref="SileroVoiceActivityDetector"/> class.</summary>
    /// <param name="modelPath">Path to <c>silero_vad.onnx</c>.</param>
    public SileroVoiceActivityDetector(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
        };

        _session = new InferenceSession(modelPath, options);

        var inputs = _session.InputMetadata.Keys.ToList();

        _audioInput = Find(inputs, "input") ?? inputs.FirstOrDefault()
            ?? throw new InvalidOperationException("The Silero model exposes no inputs.");

        // v5 carries a single combined state; v4 keeps separate LSTM hidden and cell tensors.
        _stateInput = Find(inputs, "state");
        _hiddenInput = Find(inputs, "h");
        _cellInput = Find(inputs, "c");
        _sampleRateInput = Find(inputs, "sr");
    }

    /// <inheritdoc />
    public string Name => "silero";

    /// <inheritdoc />
    public bool IsNeural => true;

    /// <inheritdoc />
    public int SampleRate => Rate;

    /// <inheritdoc />
    public int FrameSamples => Window;

    /// <inheritdoc />
    public float ScoreFrame(ReadOnlySpan<float> samples)
    {
        var audio = new float[Window];
        samples[..Math.Min(samples.Length, Window)].CopyTo(audio);

        var inputs = new List<NamedOnnxValue>(4)
        {
            NamedOnnxValue.CreateFromTensor(_audioInput, new DenseTensor<float>(audio, [1, Window])),
        };

        if (_sampleRateInput is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                _sampleRateInput,
                new DenseTensor<long>(new long[] { Rate }, [1])));
        }

        if (_stateInput is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                _stateInput,
                new DenseTensor<float>(_state, [2, 1, StateSize])));
        }
        else
        {
            if (_hiddenInput is not null)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(_hiddenInput, new DenseTensor<float>(_hidden, [2, 1, 64])));
            }

            if (_cellInput is not null)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(_cellInput, new DenseTensor<float>(_cell, [2, 1, 64])));
            }
        }

        using var results = _session.Run(inputs);

        var probability = 0f;
        foreach (var result in results)
        {
            var tensor = result.AsTensor<float>();

            // The model is recurrent: state must be carried forward or every frame is scored as if
            // it were the first, and the output becomes noise.
            if (tensor.Length == _state.Length && _stateInput is not null)
            {
                _state = tensor.ToArray();
            }
            else if (tensor.Length == _hidden.Length && result.Name.Contains('h', StringComparison.OrdinalIgnoreCase))
            {
                _hidden = tensor.ToArray();
            }
            else if (tensor.Length == _cell.Length && result.Name.Contains('c', StringComparison.OrdinalIgnoreCase))
            {
                _cell = tensor.ToArray();
            }
            else if (tensor.Length == 1)
            {
                probability = tensor.GetValue(0);
            }
        }

        return probability;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _state = new float[2 * StateSize];
        _hidden = new float[2 * 64];
        _cell = new float[2 * 64];
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();

    private static string? Find(List<string> names, string exact) =>
        names.FirstOrDefault(n => string.Equals(n, exact, StringComparison.OrdinalIgnoreCase));
}
