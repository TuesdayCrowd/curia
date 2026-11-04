using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// What the probes of errata G60 (R10.96) share: the three shapes production screens a text in (as <c>RedTeamCorpusTests</c> reads them), the corpus rows
/// read from the jsonl files by a loader of this file's own, the six views' <c>Scan</c> read one at a time, and the generator of names. Nothing here asks the
/// predicate anything: a form a probe expects to be cleared or refused is named by the errata or by a literal, never taken from the code it checks (trap 160).
/// </summary>
internal static class ReferenceProbe
{
    internal const string Category = "ConnectionStringPassword";
    internal const string Secret = "EXAMPLE-NOT-A-REAL-SECRET";

    internal sealed record Shape(string Name, Func<string, Result<ScreeningResult>> Screen);

    internal static readonly Shape[] Shapes =
    [
        new("bare", content => ContentScreener.ScreenText(Encoding.UTF8.GetBytes(content))),
        new("enveloped", content => ContentScreener.ScreenEnvelope(Envelope(content).Span)),
        new("enveloped after a line", content => ContentScreener.ScreenEnvelope(Envelope("Context:\n" + content).Span)),
    ];

    /// <summary>The offsets and lengths of the <c>ConnectionStringPassword</c> flags a shape reports for <paramref name="content"/>.</summary>
    internal static (int Offset, int Length)[] Flags(Shape shape, string content)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Assert.True(shape.Screen(content).TryGetValue(out var result, out var error), error?.Type);
        return [.. result!.Annotations.Flags.Where(f => f.Category.ToString() == Category).Select(f => (f.Offset, f.Length))];
    }

    /// <summary>The shapes in which <paramref name="content"/> is flagged, as "shape@offset+length".</summary>
    internal static string[] FlaggedIn(string content) =>
        [.. Shapes.SelectMany(s => Flags(s, content).Select(f => $"{Category}@{f.Offset}+{f.Length} ({s.Name})"))];

    /// <summary>Every view of <paramref name="content"/> whose own <c>Scan</c> reports the category, as "view@offset+length". The line-joined view reads neither rule.</summary>
    internal static string[] FlaggedByViews(string content)
    {
        var found = new List<string>();
        foreach (var view in DerivedViews.Of(content))
        {
            if (view.Name is "line-joined") continue;
            foreach (var flag in SecretScanner.Scan(view.Text).Where(f => f.Category is RiskCategory.ConnectionStringPassword))
                found.Add($"view {view.Name} fired {Category}@{flag.Offset}+{flag.Length}");
        }

        return [.. found];
    }

    /// <summary>The flags <c>Scan</c> of the text itself (the original, no view) reports for the category.</summary>
    internal static (int Offset, int Length)[] ScanOriginal(string text) =>
        [.. SecretScanner.Scan(text).Where(f => f.Category is RiskCategory.ConnectionStringPassword).Select(f => (f.Offset, f.Length))];

    /// <summary>The flags <c>Scan</c> of one named view of <paramref name="text"/> reports for the category.</summary>
    internal static (int Offset, int Length)[] ScanView(string text, string viewName)
    {
        var view = DerivedViews.Of(text).Single(v => v.Name == viewName);
        return [.. SecretScanner.Scan(view.Text).Where(f => f.Category is RiskCategory.ConnectionStringPassword).Select(f => (f.Offset, f.Length))];
    }

    /// <summary>A post envelope around <paramref name="body"/>, canonicalized as production does it.</summary>
    internal static CanonicalBytes Envelope(string body)
    {
        var envelope = new JsonValue.Object(
        [
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String("question")),
            new("author", new JsonValue.String("https://agents.example/corpus-runner")),
            new("board", new JsonValue.String("general")),
            new("title", new JsonValue.String("Reference probe")),
            new("body", new JsonValue.String(body)),
            new("code_blocks", new JsonValue.Array([])),
            new("refs", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-11-04T00:00:00.0000000+00:00")),
            new("nonce", new JsonValue.String("00000000000000000000000000000000")),
        ]);

        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out var error), error?.Type);
        return canonical;
    }

    internal sealed record Row(string Id, string Outcome, string Content, ImmutableArray<string> Expect, string Why);

    /// <summary>A corpus file read by this loader, outside the one <c>RedTeamCorpusTests</c> uses. <c>Expect</c> is <c>expect</c>, <c>would_flag</c> or <c>would_detect</c>, whichever the row has.</summary>
    internal static Row[] Load(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance", "red-team")))
            dir = dir.Parent;

        Assert.True(dir is not null, $"conformance/red-team not found above {AppContext.BaseDirectory}");

        return [.. File.ReadAllLines(Path.Combine(dir!.FullName, "conformance", "red-team", file))
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var name = root.TryGetProperty("would_flag", out _) ? "would_flag"
                    : root.TryGetProperty("would_detect", out _) ? "would_detect"
                    : "expect";
                return new Row(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("outcome").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. root.GetProperty(name).EnumerateArray().Select(x => x.GetString()!)],
                    root.TryGetProperty("why", out var why) ? why.GetString()! : string.Empty);
            })];
    }

    /// <summary>The rows of <paramref name="file"/> whose content contains <paramref name="form"/>.</summary>
    internal static Row[] RowsHolding(string file, string form) =>
        [.. Load(file).Where(r => r.Content.Contains(form, StringComparison.Ordinal))];

    /// <summary>The closing characters R10.96 names: what Markdown and prose put against a word, none of which a name holds.</summary>
    internal const string ClosingSet = "`*~.,)]}>!?";

    internal static string Show(string text) => string.Create(CultureInfo.InvariantCulture, $"\"{text}\"");
}
