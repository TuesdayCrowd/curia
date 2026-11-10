using Microsoft.ML.OnnxRuntime;

namespace Curia.Infrastructure.Embedder;

/// <summary>Builds an ONNX Runtime session from a verified model file: CPU, the thread shape of <see cref="OnnxEmbedderOptions"/>, runs in sequence (errata G65, Ruling 7).</summary>
internal sealed class OnnxRuntimeLoader : IModelLoader
{
    public IModelRunner Load(string modelPath, OnnxEmbedderOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(options);

        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = options.IntraOpNumThreads,
            InterOpNumThreads = options.InterOpNumThreads,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = options.EnableCpuMemArena,
        };
#pragma warning disable CA2000 // the runner owns the session and disposes it
        return new OnnxRuntimeRunner(new InferenceSession(modelPath, sessionOptions));
#pragma warning restore CA2000
    }
}

/// <summary>
/// One session (errata G65, Ruling 7). A run is stopped with <see cref="RunOptions.Terminate"/> when its caller's token is cancelled (Task 0 M6: a 510-token run stopped 2 to 4 ms
/// after the request and the session ran the next call in 149 ms), and what ONNX Runtime throws for a run it was told to stop is reported as the caller's own cancellation.
/// </summary>
internal sealed class OnnxRuntimeRunner(InferenceSession session) : IModelRunner
{
    private static readonly string[] OutputNames = ["last_hidden_state"];

    public async Task<float[]> RunAsync(ModelRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        try
        {
            return await Task.Run(() => Run(runOptions, input), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private float[] Run(RunOptions runOptions, ModelRunInput input)
    {
        var shape = new long[] { 1, input.InputIds.Length };
        using var ids = OrtValue.CreateTensorValueFromMemory(input.InputIds, shape);
        using var mask = OrtValue.CreateTensorValueFromMemory(input.AttentionMask, shape);
        using var types = OrtValue.CreateTensorValueFromMemory(input.TokenTypeIds, shape);
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = ids, ["attention_mask"] = mask, ["token_type_ids"] = types };

        using var outputs = session.Run(runOptions, inputs, OutputNames);
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    public void Dispose() => session.Dispose();
}
