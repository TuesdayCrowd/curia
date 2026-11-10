using System.Diagnostics;
using System.Globalization;
using System.Text;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Embedding.Tests;

/// <summary>
/// The model directory of the suite, and the one place that fails when it is absent (errata G65, R14.13): this assembly runs the real model and never skips. The failure names the
/// variable and the command that fetches the files.
/// </summary>
internal static class ModelDirectory
{
    internal const string Variable = "CURIA_EMBEDDING_MODEL_DIR";

    /// <summary>The directory <paramref name="value"/> names, or a failure that says what to run.</summary>
    internal static string Resolve(string? value)
    {
        var how = $"Run `python3 tools/model/fetch-model.py --out <dir>` and set {Variable} to <dir>. Curia.Embedding.Tests fails without the model; it never skips (R14.13).";
        if (string.IsNullOrWhiteSpace(value))
            throw new Xunit.Sdk.XunitException($"{Variable} is not set. {how}");
        if (!Directory.Exists(value))
            throw new Xunit.Sdk.XunitException($"{Variable} names a directory that does not exist. {how}");
        foreach (var file in new[] { "model.onnx", "vocab.txt" })
            if (!File.Exists(Path.Combine(value, file)) || new FileInfo(Path.Combine(value, file)).Length == 0)
                throw new Xunit.Sdk.XunitException($"{Variable} names a directory with no {file}. {how}");
        return value;
    }

    internal static string FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(Variable));

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}

/// <summary>The real embedder over the real files, opened and checked once for every test of the assembly.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "Constructor-injected into the public test classes of this assembly, which xUnit requires to be public; a public class cannot expose an internal type through its constructor (CS0051).")]
public sealed class RealModelFixture : IDisposable
{
    public RealModelFixture()
    {
        Directory = ModelDirectory.FromEnvironment();
        Embedder = OnnxTextEmbedder.Open(Directory);
        Embedder.Check();
    }

    internal string Directory { get; }

    internal OnnxTextEmbedder Embedder { get; }

    public void Dispose() => Embedder.Dispose();
}

/// <summary>Helpers the real-model probes share: a child process with its two streams kept apart, the cosine of two vectors, and the vector or the error type of a result.</summary>
internal static class Probe
{
    internal static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    internal static async Task<(int Exit, string Out, string Err)> RunAsync(string file, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + file);
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }

    internal static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        Assert.Equal(a.Count, b.Count);
        var dot = 0.0;
        for (var i = 0; i < a.Count; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    internal static double MaxDifference(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        Assert.Equal(a.Count, b.Count);
        var worst = 0.0;
        for (var i = 0; i < a.Count; i++) worst = Math.Max(worst, Math.Abs(a[i] - (double)b[i]));
        return worst;
    }

    /// <summary>The vector of a result, or a failure naming the error type: a probe that expected a vector says what it got.</summary>
    internal static IReadOnlyList<float> VectorOf(Curia.Domain.Primitives.Result<Curia.Application.Ports.Embedding> result, string what) =>
        result.Match(e => (IReadOnlyList<float>)e.Vector, error => throw new Xunit.Sdk.XunitException($"{what}: expected a vector, got {error.Type}"));

    internal static string Describe(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    internal static string Joined(IEnumerable<int> ids) => string.Join(' ', ids);

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    internal static EmbeddingRole Passage => EmbeddingRole.Passage;
}
