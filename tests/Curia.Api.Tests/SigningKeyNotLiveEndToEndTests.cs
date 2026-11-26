using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CanonJson = Curia.Canon.Json;

namespace Curia.Api.Tests;

/// <summary>
/// The test-only seam of UDO6 (errata G75, register item 323; Amendment 1 ruling 6): while armed, fails verification of exactly one rotation message and keeps failing exactly those bytes; disarmed, it
/// delegates. The message is the first one it sees whose statement carries both <c>prev</c> and <c>authorized_by</c> (a rotation's), recorded at its first sight; any other message, a later rotation included, differs
/// in its bytes and is never failed. It also records the shape of the first messages it verified, so a report says what the first verified message actually was.
/// </summary>
internal sealed class RotationSeam
{
    private readonly object _gate = new();
    private byte[]? _recorded;

    internal volatile bool Armed;

    /// <summary>The members of the statement of each of the first messages verified while armed, with whether the selector took it.</summary>
    internal List<string> Seen { get; } = [];

    internal bool Failed { get; private set; }

    /// <summary>Whether this message is the one to fail: the recorded rotation's bytes, the first time one is seen.</summary>
    internal bool Fails(ReadOnlySpan<byte> input)
    {
        if (!Armed) return false;

        lock (_gate)
        {
            var members = MembersOf(input);
            var rotation = members is not null && members.Contains("prev", StringComparer.Ordinal) && members.Contains("authorized_by", StringComparer.Ordinal);
            if (_recorded is null && rotation) _recorded = input.ToArray();

            var fails = _recorded is not null && input.SequenceEqual(_recorded);
            if (fails) Failed = true;
            if (Seen.Count < 12) Seen.Add($"[{(members is null ? "not a statement" : string.Join(",", members))}] selected={(rotation && fails ? "yes" : "no")} failed={fails}");
            return fails;
        }
    }

    private static string[]? MembersOf(ReadOnlySpan<byte> input)
    {
        var dot = input.IndexOf((byte)'.');
        if (dot < 0) return null;

        try
        {
            using var statement = JsonDocument.Parse(input[(dot + 1)..].ToArray());
            return statement.RootElement.ValueKind == JsonValueKind.Object ? [.. statement.RootElement.EnumerateObject().Select(p => p.Name)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal IReadOnlyDictionary<string, IContentVerifier> Decorate(IReadOnlyDictionary<string, IContentVerifier> verifiers) =>
        verifiers.ToDictionary(v => v.Key, v => (IContentVerifier)new Decorated(v.Value, this), StringComparer.Ordinal);

    private sealed class Decorated(IContentVerifier inner, RotationSeam seam) : IContentVerifier
    {
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key) => !seam.Fails(input) && inner.Verify(input, sig, key);
    }
}

/// <summary>
/// The Forum on a real socket whose <see cref="LifecycleVerifier"/> (Program.cs's registration) is built over a decorated copy of the verifier dictionary (<see cref="RotationSeam"/>); the shared dictionary, which also
/// verifies tokens, DPoP proofs and heads, is left alone. Same store, same <c>events</c>: the fold is the only thing the seam changes between two requests.
/// </summary>
public sealed class SkippedRotationFixture : ForumFixture
{
    public SkippedRotationFixture() => UseReservedKestrelPort();

    internal RotationSeam Seam { get; } = new();

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        base.ConfigureStore(services);
        foreach (var registered in services.Where(d => d.ServiceType == typeof(LifecycleVerifier)).ToList()) services.Remove(registered);
        services.AddSingleton(sp => new LifecycleVerifier(Seam.Decorate(sp.GetRequiredService<IReadOnlyDictionary<string, IContentVerifier>>())));
    }
}

/// <summary>
/// UDO6 (errata G75, R11.101, register item 323): the lockout end to end, over the real client in a child process and the real route, through the seam above. A rotation (A) is answered <c>500 curia/keys/not-honoured</c>
/// (the seam fails it), the next (B) is <c>201</c> (it links from the last honoured row), the seam is disarmed and the Forum's fold then honours A and skips B as <c>broken-link</c>: the profile signs with a key the Forum
/// does not serve, and the token is refused (a cached token first, then <c>invalid_client</c>). Then <c>curia keys</c> prints the line listing the keys the Forum binds, <c>curia use-key</c> makes the profile sign with
/// one of them and appends nothing, and an ordinary <c>ask</c> is accepted. The oracle for what the Forum binds is the store's <c>agent.key-bound</c> events, and for "appends nothing" the log's length; never the profile.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class SigningKeyNotLiveEndToEndTests(SkippedRotationFixture forum) : IClassFixture<SkippedRotationFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-udo6-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ");

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args)
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);   // a child process signs its proofs with the real clock
        return await CuriaProcess.RunAsync(_home, Ct, args);
    }

    private async Task<List<AppendedEvent>> LogAsync()
    {
        var read = await forum.Services.GetRequiredService<IEventStore>().ReadForwardAsync(EventSequence.Zero, null, Ct);
        return [.. read.Match(v => v, e => throw new InvalidOperationException(e.Type))];
    }

    /// <summary>The kids the store's <c>agent.key-bound</c> events of the identity name, with each event's position in the whole log, in log order.</summary>
    private async Task<List<(string Kid, long Index)>> BoundAsync(string agentId)
    {
        var log = await LogAsync();
        var bound = new List<(string, long)>();
        for (var i = 0; i < log.Count; i++)
        {
            var e = log[i];
            if (e.Event.Type.Value != "agent.key-bound" || e.AggregateId.Value != agentId) continue;
            if (e.Event.Payload is CanonJson.JsonValue.Object o && o.Members.FirstOrDefault(m => m.Key == "kid").Value is CanonJson.JsonValue.String kid) bound.Add((kid.Value, i));
        }

        return bound;
    }

    private static string Kid(string kid) => CanonJson.DisplayLiteral.Of(kid);

    private static string Span(string label)
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R11.101 words.**");
        var marker = $"*{label}:*";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"the errata's R11.101 words do not hold {marker}");

        var start = at + marker.Length;
        var next = Regex.Match(paragraph[start..], @"\*[A-Z][^*`]*:\*", RegexOptions.CultureInvariant);
        var section = next.Success ? paragraph[start..(start + next.Index)] : paragraph[start..];
        var spans = Regex.Matches(section, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        return spans.Length == 1 ? spans[0] : throw new InvalidOperationException($"{marker}: {spans.Length} spans, expected 1");
    }

    private static string Once(string template, string placeholder, string value)
    {
        var at = template.IndexOf(placeholder, StringComparison.Ordinal);
        if (at < 0 || template.IndexOf(placeholder, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the published words do not hold {placeholder} exactly once");

        return template.Replace(placeholder, value, StringComparison.Ordinal);
    }

    private string SeamReport() => "\n--- the seam: the first messages it verified while armed ---\n" + string.Join("\n", forum.Seam.Seen);

    [Fact]
    public async Task UDO6_ALockedOutProfileIsToldSoAndSignsWithAKeyTheForumBinds()
    {
        var slug = "udo6-" + Guid.NewGuid().ToString("N")[..8];
        var agentId = $"urn:curia:agent:{slug}";

        // The scene, step by step. Each step's own words are asserted as it stands on the unmodified tree: a harness fault must not read as the probe's red.
        var enrolled = await CuriaAsync("enrol", "--agent", slug, "--forum", Address);
        Assert.True(enrolled.Exit == 0, "harness: enrol: " + enrolled.Stdout + enrolled.Stderr);

        forum.Seam.Armed = true;
        var first = await CuriaAsync("rotate", "--agent", slug, "--forum", Address);
        Assert.True(
            first.Exit == 6 && (first.Stdout + first.Stderr).Contains("curia/keys/not-honoured", StringComparison.Ordinal) && forum.Seam.Failed,
            $"harness: the first rotation (A) was not answered not-honoured by the seam (exit {first.Exit}, failed {forum.Seam.Failed}): {first.Stdout}{first.Stderr}{SeamReport()}");
        var second = await CuriaAsync("rotate", "--agent", slug, "--forum", Address);
        Assert.True(second.Exit == 0, $"harness: the second rotation (B) was not accepted (exit {second.Exit}): {second.Stdout}{second.Stderr}{SeamReport()}");
        forum.Seam.Armed = false;

        var bound = await BoundAsync(agentId);
        Assert.True(bound.Count == 3, $"harness: the log holds {bound.Count} agent.key-bound events for the identity, expected 3 (enrolment, A, B): {string.Join(", ", bound)}");
        var signing = bound[^1].Kid;
        var identity = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_home, "agents", slug, "identity.json"), Ct))!.AsObject();
        Assert.True((string?)identity["kid"] == signing, $"harness: the profile signs with {identity["kid"]}, expected B's key {signing}");

        // The token is refused: a cached token first (the budget read), then invalid_client. Both are the Forum's answers; the printed one is the last.
        var budget = await CuriaAsync("budget", "--agent", slug, "--forum", Address);
        var budgetText = budget.Stdout + "\n" + budget.Stderr;
        Assert.True(budget.Exit == 6 && budgetText.Contains("invalid_client", StringComparison.Ordinal), $"harness: budget did not meet invalid_client (exit {budget.Exit}): {budgetText}");

        // The probe. The keys the Forum binds are K0 (the enrolment's) and A's, at the log positions the store holds them; B's is not served.
        var served = bound[..2];
        var held = string.Join(", ", served.Select(b => $"{Kid(b.Kid)} (bound at log index {b.Index.ToString(CultureInfo.InvariantCulture)})"));
        var line = Span("Held keys the Forum binds");
        line = Once(line, "<held keys>", held);
        line = Once(line, "<does not list | lists as <status>>", "does not list");
        line = Once(line, "<n>", served.Count.ToString(CultureInfo.InvariantCulture));
        line = Once(line, "<the use-key command for the key bound last>", $"curia use-key --agent '{slug}' --kid '{served.MaxBy(b => b.Index)!.Kid}'");
        line = Once(line, "<kid>", Kid(signing));

        var afterRefusal = Fold(budgetText).Contains(Fold(line), StringComparison.Ordinal);
        var keys = await CuriaAsync("keys", "--agent", slug, "--forum", Address);
        var onKeys = Fold(keys.Stdout).Contains(Fold(line), StringComparison.Ordinal);
        var summary = $"the line after the budget refusal: {(afterRefusal ? "said" : "not said")}; curia keys exit {keys.Exit}: the line {(onKeys ? "said" : "not said")}";
        var expectedKeys = "the line after the budget refusal: said; curia keys exit 6: the line said";
        Assert.True(
            summary == expectedKeys,
            $"Expected: {expectedKeys}  Actual: {summary}\n--- budget ---\n{budgetText.Trim()}\n--- curia keys stdout ---\n{keys.Stdout.Trim()}\n--- the line, from the errata ---\n{line}{SeamReport()}");

        // The adoption: it appends nothing, and an ordinary question is accepted afterwards.
        var eventsBefore = (await LogAsync()).Count;
        var adopted = await CuriaAsync("use-key", "--agent", slug, "--kid", served[1].Kid, "--forum", Address);
        var eventsAfter = (await LogAsync()).Count;
        var asked = await CuriaAsync(FirstAsk.Args(slug, "udo6-" + Guid.NewGuid().ToString("N")[..8], "Which key signs now?", "A question asked after the profile adopted a key the Forum binds.", Address, makesBoard: true));
        var actual = $"use-key exit {adopted.Exit}; events {(eventsBefore == eventsAfter ? "unchanged" : $"{eventsBefore} -> {eventsAfter}")}; ask exit {asked.Exit}";
        var expected = "use-key exit 0; events unchanged; ask exit 0";
        Assert.True(expected == actual, $"Expected: {expected}  Actual: {actual}\n--- use-key ---\n{adopted.Stdout}{adopted.Stderr}\n--- ask ---\n{asked.Stdout}{asked.Stderr}");
    }
}
