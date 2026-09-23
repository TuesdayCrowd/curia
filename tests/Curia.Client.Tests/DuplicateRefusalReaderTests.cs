using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.19 and R8.61 at the client's reader: the duplicate refusal is read whole or not at all, and
/// what it cannot read it counts.
///
/// <para><b>Why the reader is strict.</b> It used to default every absent number to zero and every
/// absent string to empty, so a refusal that had lost its thresholds rendered as "refused at 0 bp" —
/// a measurement nobody made, presented as the Forum's. And it skipped an answer it could not read
/// without a word, so an agent shown two answers when the Forum sent three concluded the thread had
/// two. Both are an absence reading as a satisfied answer.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DuplicateRefusalReaderTests : IDisposable
{
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Every member the reader requires, as a path into the refusal document.</summary>
    public static TheoryData<string> RequiredMembers() =>
    [
        "canonical",
        "canonical.post_id",
        "canonical.digest",
        "canonical.board",
        "answers",
        "similarity",
        "similarity.cosine_bp",
        "similarity.lexical_overlap_bp",
        "similarity.refuse_cosine_bp",
        "similarity.refuse_lexical_overlap_bp",
        "similarity.annotate_cosine_bp",
        "similarity.model",
        "override",
    ];

    /// <summary>
    /// Non-vacuity for the theory below: the intact document reads, so a null there is caused by
    /// the member removed rather than by a document that never read at all.
    /// </summary>
    [Fact]
    public void R8_61_TheIntactRefusalReads() =>
        Assert.NotNull(Read(Parse(_log.DuplicateRefusalJson())));

    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public void R8_61_ARefusalMissingAnyRequiredMemberIsNotReadAsZero(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var document = JsonNode.Parse(_log.DuplicateRefusalJson())!.AsObject();
        var removed = Remove(document, path);

        Assert.True(removed, $"the fixture carries no {path}, so this row would pass without testing anything");
        Assert.Null(Read(Parse(document.ToJsonString())));
    }

    /// <summary>
    /// A basis-point member outside 0..10000, or not a number at all, is not a measurement and is
    /// refused rather than clamped. A <i>fraction</i> never reaches this reader: the client parses
    /// every response under the ADMIT profile, which refuses a non-integer as
    /// <c>curia/admit/non-integer-number</c> first (R6.33) -- established by putting one here and
    /// watching the parse fail, not by reading the parser.
    /// </summary>
    [Theory]
    [InlineData("10001")]
    [InlineData("-1")]
    [InlineData("\"9200\"")]
    public void R6_33_ABasisPointMemberThatIsNotAnIntegerInRangeIsRefused(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var json = _log.DuplicateRefusalJson().Replace("\"cosine_bp\":9200", "\"cosine_bp\":" + value, StringComparison.Ordinal);
        Assert.NotEqual(_log.DuplicateRefusalJson(), json);

        Assert.Null(Read(Parse(json)));
    }

    /// <summary>
    /// An answer the client cannot read is counted, and the ones it can are kept. Dropping it
    /// silently would under-report the thread; refusing the whole document would withhold the
    /// canonical thread's id over one bad answer.
    /// </summary>
    [Fact]
    public void R8_19_AnUnreadableAnswerIsCountedRatherThanDropped()
    {
        var document = JsonNode.Parse(_log.DuplicateRefusalJson())!.AsObject();
        document["answers"]!.AsArray().Add(JsonNode.Parse("""{"not":"a post"}"""));

        var read = Read(Parse(document.ToJsonString()));

        Assert.NotNull(read);
        Assert.Single(read.Answers);
        Assert.Equal(1, read.UnreadableAnswers);
    }

    /// <summary>Through <see cref="Refusal.AsDuplicate"/>, the path every caller of the client reads a 409 by.</summary>
    private static DuplicateRefusalDocument? Read(Curia.Canon.Json.JsonValue document) =>
        new Refusal(
            RefusalKind.Conflict,
            409,
            new Curia.Domain.Primitives.Error("curia/posts/duplicate-question", "duplicate"),
            document).AsDuplicate;

    private static Curia.Canon.Json.JsonValue Parse(string json) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default).TryGetValue(out var value, out var error)
            ? value!
            : throw new InvalidOperationException(error!.Type);

    private static bool Remove(JsonObject root, string path)
    {
        var steps = path.Split('.');
        var parent = root;

        foreach (var step in steps[..^1])
        {
            if (parent[step] is not JsonObject next) return false;
            parent = next;
        }

        return parent.Remove(steps[^1]);
    }
}
