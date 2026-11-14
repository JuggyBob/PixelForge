using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixelForge.Domain;
using PixelForge.Domain.Interfaces;
using PixelForge.Domain.Models;

namespace PixelForge.Engines.Upscaling;

/// <summary>
/// ONNX Runtime implementation of RIFE frame interpolation. It deliberately
/// shares the same DirectML/CPU runtime and session-reuse policy as the image
/// upscaler; no Python, CUDA or external runtime is required.
/// </summary>
public sealed class RifeInterpolator : IFrameInterpolator, IDisposable
{
    private readonly ConcurrentDictionary<SessionKey, SessionHolder> _sessions = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly ILogger<RifeInterpolator> _logger;
    private readonly bool _cpuAvailable;
    private readonly bool _directMlAvailable;
    private readonly string? _runtimeError;

    public RifeInterpolator(ILogger<RifeInterpolator> logger)
    {
        _logger = logger;
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            _cpuAvailable = providers.Contains("CPUExecutionProvider", StringComparer.OrdinalIgnoreCase);
            _directMlAvailable = providers.Contains("DmlExecutionProvider", StringComparer.OrdinalIgnoreCase);
            if (!_cpuAvailable && !_directMlAvailable)
                _runtimeError = "ONNX Runtime reported no execution providers.";
        }
        catch (Exception ex)
        {
            _runtimeError = ex.Message;
            _logger.LogError(ex, "RIFE could not initialize ONNX Runtime");
        }
    }

    public async Task<FrameInterpolationResult> InterpolateFrameAsync(string modelPath, byte[] firstRgb24,
        byte[] secondRgb24, int width, int height, double timestep, ExecutionProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The RIFE model is not installed.", modelPath);
        if (firstRgb24.Length != width * height * 3 || secondRgb24.Length != width * height * 3)
            throw new ArgumentException("The frame buffers do not match the supplied dimensions.");

        var multiple = 64;
        var paddedWidth = RoundUp(width, multiple);
        var paddedHeight = RoundUp(height, multiple);
        var attempts = provider == ExecutionProviderKind.Cpu
            ? new[] { ExecutionProviderKind.Cpu }
            : _directMlAvailable
                ? new[] { ExecutionProviderKind.DirectML, ExecutionProviderKind.Cpu }
                : new[] { ExecutionProviderKind.Cpu };

        var errors = new List<string>();
        string? warning = null;
        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var holder = await GetSessionAsync(modelPath, attempt, cancellationToken);
            holder.BeginUse();
            try
            {
                await _inferenceGate.WaitAsync(cancellationToken);
                try
                {
                    var result = await Task.Run(() => Run(holder, firstRgb24, secondRgb24, width, height,
                        paddedWidth, paddedHeight, timestep), cancellationToken);
                    return new FrameInterpolationResult
                    {
                        Rgb24 = result,
                        Provider = attempt,
                        Warning = warning
                    };
                }
                finally
                {
                    _inferenceGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RIFE inference failed with {Provider}", attempt);
                errors.Add($"{ProviderName(attempt)}: {ex.Message}");
                RemoveSession(modelPath, attempt);
                if (attempt != ExecutionProviderKind.Cpu)
                    warning = $"{ProviderName(attempt)} failed; frame interpolation fell back to the CPU.";
            }
            finally
            {
                holder.EndUse();
            }
        }

        throw new InvalidOperationException("RIFE inference failed on every available execution provider. " +
                                            string.Join(" | ", errors));
    }

    public async Task<ModelValidationResult> ValidateModelAsync(string modelPath, ModelDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
            return ModelValidationResult.Fail("The model file does not exist.");
        if (!_cpuAvailable && !_directMlAvailable)
            return ModelValidationResult.Fail(_runtimeError ?? "ONNX Runtime is unavailable.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = CreateSession(modelPath, ExecutionProviderKind.Cpu, out var inputNames,
                out var inputDims, out var outputDims);
            if (inputNames.Count != 3)
                return ModelValidationResult.Fail("RIFE must expose two frame inputs and one timestep input.",
                    $"Inputs: {string.Join(", ", inputNames)}");
            if (inputDims[0].Length != 4 || inputDims[1].Length != 4 ||
                (inputDims[0][1] > 0 && inputDims[0][1] != 3) ||
                (inputDims[1][1] > 0 && inputDims[1][1] != 3))
                return ModelValidationResult.Fail("RIFE frame inputs must be 4-D NCHW RGB tensors.");
            if (inputDims[2].Length is not (1 or 4))
                return ModelValidationResult.Fail("RIFE timestep must be a scalar or 1×1×1×1 tensor.");
            if (outputDims.Length != 4 || (outputDims[1] > 0 && outputDims[1] != 3))
                return ModelValidationResult.Fail("RIFE must produce a 3-channel image tensor.");

            const int size = 64;
            var frame = new float[3 * size * size];
            Array.Fill(frame, 0.5f);
            var timestep = new DenseTensor<float>(new[] { 0.5f }, [1, 1, 1, 1]);
            using var results = session.Run(
            [
                NamedOnnxValue.CreateFromTensor(inputNames[0],
                    new DenseTensor<float>(frame, [1, 3, size, size])),
                NamedOnnxValue.CreateFromTensor(inputNames[1],
                    new DenseTensor<float>(frame, [1, 3, size, size])),
                NamedOnnxValue.CreateFromTensor(inputNames[2], timestep)
            ], [session.OutputMetadata.First().Key]);
            var dims = results.First().AsTensor<float>().Dimensions.ToArray();
            if (dims.Length != 4 || dims[2] != size || dims[3] != size)
                return ModelValidationResult.Fail("RIFE produced an unexpected output size.",
                    $"Output: {string.Join(" × ", dims)}");

            return ModelValidationResult.Ok(1,
                string.Join(", ", inputNames.Select((n, i) => $"{n}:{FormatShape(inputDims[i])}")),
                FormatShape(dims));
        }
        catch (Exception ex)
        {
            return ModelValidationResult.Fail("The RIFE ONNX model could not be loaded: " + ex.Message,
                ex.ToString());
        }
    }

    private byte[] Run(SessionHolder holder, byte[] first, byte[] second, int width, int height,
        int paddedWidth, int paddedHeight, double timestep)
    {
        var plane = paddedWidth * paddedHeight;
        var input0 = new float[3 * plane];
        var input1 = new float[3 * plane];
        for (var y = 0; y < paddedHeight; y++)
        {
            var sourceY = Math.Min(y, height - 1);
            for (var x = 0; x < paddedWidth; x++)
            {
                var sourceX = Math.Min(x, width - 1);
                var source = (sourceY * width + sourceX) * 3;
                var destination = y * paddedWidth + x;
                input0[destination] = first[source] / 255f;
                input0[plane + destination] = first[source + 1] / 255f;
                input0[2 * plane + destination] = first[source + 2] / 255f;
                input1[destination] = second[source] / 255f;
                input1[plane + destination] = second[source + 1] / 255f;
                input1[2 * plane + destination] = second[source + 2] / 255f;
            }
        }

        var timeTensor = holder.TimestepRank switch
        {
            1 => new DenseTensor<float>(new[] { (float)timestep }, [1]),
            _ => new DenseTensor<float>(new[] { (float)timestep }, [1, 1, 1, 1])
        };

        using var results = holder.Session.Run(
        [
            NamedOnnxValue.CreateFromTensor(holder.InputNames[0],
                new DenseTensor<float>(input0, [1, 3, paddedHeight, paddedWidth])),
            NamedOnnxValue.CreateFromTensor(holder.InputNames[1],
                new DenseTensor<float>(input1, [1, 3, paddedHeight, paddedWidth])),
            NamedOnnxValue.CreateFromTensor(holder.InputNames[2], timeTensor)
        ], [holder.OutputName]);
        var output = results.First().AsTensor<float>();
        var dims = output.Dimensions.ToArray();
        if (dims.Length != 4 || dims[2] < height || dims[3] < width)
            throw new InvalidOperationException("RIFE returned an unexpected output tensor.");

        var outPlane = dims[2] * dims[3];
        var dense = output as DenseTensor<float>;
        var span = dense is not null ? dense.Buffer.Span : Span<float>.Empty;
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = y * dims[3] + x;
                var destination = (y * width + x) * 3;
                rgb[destination] = span.Length > 0
                    ? ToByte(span[source])
                    : ToByte(output[0, 0, y, x]);
                rgb[destination + 1] = span.Length > 0
                    ? ToByte(span[outPlane + source])
                    : ToByte(output[0, 1, y, x]);
                rgb[destination + 2] = span.Length > 0
                    ? ToByte(span[2 * outPlane + source])
                    : ToByte(output[0, 2, y, x]);
            }
        }

        return rgb;
    }

    private async Task<SessionHolder> GetSessionAsync(string modelPath, ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        var key = new SessionKey(modelPath, File.GetLastWriteTimeUtc(modelPath).Ticks, provider);
        if (_sessions.TryGetValue(key, out var existing)) return existing;
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out existing)) return existing;
            foreach (var old in _sessions.Keys.Where(k => k.Provider == provider &&
                         string.Equals(k.Path, modelPath, StringComparison.OrdinalIgnoreCase) && k != key).ToList())
            {
                if (_sessions.TryRemove(old, out var removed)) removed.Dispose();
            }

            var holder = await Task.Run(() =>
            {
                var session = CreateSession(modelPath, provider, out var inputNames, out var inputDims, out _);
                var timestepRank = inputDims[2].Length;
                return new SessionHolder(key, session, inputNames, session.OutputMetadata.First().Key, timestepRank);
            }, cancellationToken);
            _sessions[key] = holder;
            return holder;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private InferenceSession CreateSession(string modelPath, ExecutionProviderKind provider,
        out IReadOnlyList<string> inputNames, out IReadOnlyList<int[]> inputDims, out int[] outputDims)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        try
        {
            if (provider == ExecutionProviderKind.DirectML)
            {
                options.AppendExecutionProvider_DML(0);
                options.EnableMemoryPattern = false;
            }
            else
            {
                options.AppendExecutionProvider_CPU(0);
                options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
                options.InterOpNumThreads = 1;
            }

            var session = new InferenceSession(modelPath, options);
            var inputs = session.InputMetadata.ToList();
            inputNames = inputs.Select(i => i.Key).ToList();
            inputDims = inputs.Select(i => i.Value.Dimensions.ToArray()).ToList();
            outputDims = session.OutputMetadata.First().Value.Dimensions.ToArray();
            return session;
        }
        finally
        {
            options.Dispose();
        }
    }

    private void RemoveSession(string modelPath, ExecutionProviderKind provider)
    {
        foreach (var key in _sessions.Keys.Where(k => k.Provider == provider &&
                     string.Equals(k.Path, modelPath, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_sessions.TryRemove(key, out var holder)) holder.Dispose();
        }
    }

    private static int RoundUp(int value, int multiple) =>
        Math.Max(multiple, (value + multiple - 1) / multiple * multiple);

    private static byte ToByte(float value) => (byte)Math.Clamp(value * 255f + 0.5f, 0f, 255f);
    private static string ProviderName(ExecutionProviderKind provider) =>
        provider == ExecutionProviderKind.DirectML ? "DirectML (GPU)" : "CPU";
    private static string FormatShape(IEnumerable<int> dims) =>
        string.Join(" × ", dims.Select(d => d <= 0 ? "?" : d.ToString()));

    public void Dispose()
    {
        foreach (var holder in _sessions.Values) holder.Dispose();
        _sessions.Clear();
        _sessionGate.Dispose();
        _inferenceGate.Dispose();
    }

    private readonly record struct SessionKey(string Path, long Ticks, ExecutionProviderKind Provider);

    private sealed class SessionHolder : IDisposable
    {
        private long _lastUsed = Environment.TickCount64;
        private int _inUse;

        public SessionHolder(SessionKey key, InferenceSession session, IReadOnlyList<string> inputNames,
            string outputName, int timestepRank)
        {
            Key = key;
            Session = session;
            InputNames = inputNames;
            OutputName = outputName;
            TimestepRank = timestepRank;
        }

        public SessionKey Key { get; }
        public InferenceSession Session { get; }
        public IReadOnlyList<string> InputNames { get; }
        public string OutputName { get; }
        public int TimestepRank { get; }

        public void BeginUse()
        {
            Interlocked.Exchange(ref _inUse, 1);
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
        }

        public void EndUse()
        {
            Interlocked.Exchange(ref _inUse, 0);
            Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
        }

        public bool IsIdleSince(long cutoff) =>
            Volatile.Read(ref _inUse) == 0 && Volatile.Read(ref _lastUsed) < cutoff;

        public void Dispose() => Session.Dispose();
    }
}
