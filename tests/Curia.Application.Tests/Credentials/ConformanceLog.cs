using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// One conformance case's log, read into the events the verifier takes: <c>entries/&lt;i&gt;.json</c> is the entry at log index <i>i</i> (R6.47). The entries are read from the
/// vector files themselves, a different artifact from the code the probes are about; <c>expected.json</c> is written by each case's construction and never by running a verifier.
/// </summary>
internal sealed record ConformanceLog(string Name, string Directory, string Agent, IReadOnlyList<AppendedEvent> Log)
{
    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        return dir is null
            ? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "conformance");
    }

    internal static string[] Names(string family) =>
        [.. System.IO.Directory.EnumerateDirectories(Path.Combine(Root(), family)).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];

    /// <summary>A case's name, directory and agent, without its entries (a reader vector's entries have gaps, where the log keys sit).</summary>
    internal static ConformanceLog Open(string family, string name)
    {
        var directory = Path.Combine(Root(), family, name);
        using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "meta.json")));
        return new ConformanceLog(name, directory, meta.RootElement.GetProperty("agent").GetString()!, []);
    }

    internal static ConformanceLog Load(string family, string name)
    {
        var directory = Path.Combine(Root(), family, name);
        using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "meta.json")));
        var agent = meta.RootElement.GetProperty("agent").GetString()!;
        var files = System.IO.Directory.EnumerateFiles(Path.Combine(directory, "entries"), "*.json")
            .Select(f => (Index: long.Parse(Path.GetFileNameWithoutExtension(f), CultureInfo.InvariantCulture), File: f))
            .OrderBy(x => x.Index)
            .ToArray();

        var log = new List<AppendedEvent>();
        foreach (var (index, file) in files)
        {
            if (index != log.Count) throw new InvalidOperationException($"{family}/{name}: entries are not contiguous at {index}");
            log.Add(Event(index, File.ReadAllBytes(file)));
        }

        return new ConformanceLog(name, directory, agent, log);
    }

    /// <summary>An entry file as the event the log held at <paramref name="index"/>.</summary>
    internal static AppendedEvent Event(long index, byte[] entryFile)
    {
        using var document = JsonDocument.Parse(entryFile);
        var entry = document.RootElement.GetProperty("entry");
        var payload = JsonReader.Parse(Encoding.UTF8.GetBytes(entry.GetProperty("payload").GetRawText()), AdmitLimits.Default);
        if (!payload.TryGetValue(out var value, out var error)) throw new InvalidOperationException($"entry {index}: payload: {error!.Type}");

        var at = DateTimeOffset.Parse(entry.GetProperty("server_ts").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind);
        return LogEvents.At(
            index,
            entry.GetProperty("aggregate_id").GetString()!,
            at,
            entry.GetProperty("event_id").GetString()!,
            entry.GetProperty("event_type").GetString()!,
            entry.GetProperty("actor_id").GetString()!,
            value!);
    }
}
