using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Moderation;

/// <summary>
/// R10.84 (errata G43): a refusal of a flag or of a moderation record against a value that is no post id says that the value is not a
/// post id, echoes no part of it and prints no placeholder; a rationale that holds U+0000 or an unpaired surrogate is refused in
/// words that name the rationale. The types are unchanged.
///
/// <para><b>Oracles.</b> The ids are built at run time (a digest, the empty string, a 26-character string that is no ULID); the
/// rationale's two bad characters are built at run time. The empty rationale keeps its own refusal and the store keeps its own word for
/// an empty member, each a control that must stay.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagWordsTests
{
    private const string Reporter = "https://agents.example/reporter";
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Error Refusal<T>(Result<T> result)
    {
        Assert.False(result.TryGetValue(out _, out var error));
        return error!;
    }

    private static (RaiseFlag Flag, ApplyModeration Moderate) Writers()
    {
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var details = new InMemoryFlagDetailStore();
        return (new RaiseFlag(store, details, clock), new ApplyModeration(store, details, clock));
    }

    private static ActorId Operator => Require(ActorId.Create("operator:reviewer"));

    /// <summary>Values that are no post id: a digest, the empty string, a 26-character string no ULID parses. Built here and nowhere else.</summary>
    public static TheoryData<string> NotPostIds() => new()
    {
        "sha256:" + new string('d', 64),
        "",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
    };

    private static void AssertSaysNotAPostId(Error error, string value)
    {
        var all = error.Title + "\n" + error.Detail;
        Assert.DoesNotContain("(post_id", all, StringComparison.Ordinal);
        Assert.Contains("not a post id", all, StringComparison.Ordinal);
        if (value.Length > 0) Assert.DoesNotContain(value, all, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", all, StringComparison.Ordinal);
    }

    /// <summary>PJ35: the flag path.</summary>
    [Theory]
    [MemberData(nameof(NotPostIds))]
    public async Task R10_84_AFlagAgainstANonPostIdIsRefusedInWordsNotAPlaceholder(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (flag, _) = Writers();

        var error = Refusal(await flag.RecordAsync(PostId.Parse(value), Typed.Agent(Reporter), FlagKind.Spam, Rationale.Parse("reported"), TestContext.Current.CancellationToken));

        Assert.Equal("curia/flag/no-such-post", error.Type);
        AssertSaysNotAPostId(error, value);
    }

    /// <summary>PJ35: the moderation record's twin.</summary>
    [Theory]
    [MemberData(nameof(NotPostIds))]
    public async Task R10_84_AModerationRecordAgainstANonPostIdIsRefusedInWordsNotAPlaceholder(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var (_, moderate) = Writers();

        var error = Refusal(await moderate.RecordAsync(
            PostId.Parse(value), ModerationEffect.Withhold, FlagKind.Spam, Rationale.Parse("reviewed"), Operator, TestContext.Current.CancellationToken));

        Assert.Equal("curia/moderation/no-such-post", error.Type);
        AssertSaysNotAPostId(error, value);
    }

    /// <summary>PJ36: a rationale holding U+0000 or an unpaired surrogate is named as the rationale, under the type it has today.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0xD800)]
    public async Task R10_84_ARationaleWithNulOrALoneSurrogateIsNamedAsTheRationale(int bad)
    {
        var (flag, _) = Writers();
        var rationale = "looks " + (char)bad + " odd";

        var error = Refusal(await flag.RecordAsync(
            PostId.Parse("01JP0ST0000000000000000001"), Typed.Agent(Reporter), FlagKind.Spam, Rationale.Parse(rationale), TestContext.Current.CancellationToken));

        Assert.Equal("curia/flag/detail-unstorable", error.Type);
        Assert.Contains("rationale", error.Title + "\n" + error.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(((char)bad).ToString(), error.Title + error.Detail, StringComparison.Ordinal);
    }

    /// <summary>PJ36's moderation twin: the record's rationale is named too (its type is the rule's own and unchanged).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0xD800)]
    public async Task R10_84_AModerationRationaleWithNulOrALoneSurrogateIsNamedAsTheRationale(int bad)
    {
        var (_, moderate) = Writers();
        var rationale = "reviewed " + (char)bad + " odd";

        var error = Refusal(await moderate.RecordAsync(
            PostId.Parse("01JP0ST0000000000000000001"), ModerationEffect.Withhold, FlagKind.Spam, Rationale.Parse(rationale), Operator, TestContext.Current.CancellationToken));

        Assert.Contains("rationale", error.Title + "\n" + error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PJ36's controls: an empty rationale keeps RationaleRequired, and the store keeps its own title for an empty member. Green first.</summary>
    [Fact]
    public async Task R10_84_AnEmptyRationaleAndAnEmptyStoreMemberKeepTheirOwnWords()
    {
        var (flag, _) = Writers();

        var empty = Refusal(await flag.RecordAsync(
            PostId.Parse("01JP0ST0000000000000000001"), Typed.Agent(Reporter), FlagKind.Spam, Rationale.Parse(""), TestContext.Current.CancellationToken));
        Assert.Equal(ModerationErrors.RationaleRequired().Type, empty.Type);

        var member = Refusal(FlagDetailRules.Admit(new FlagDetail("e", "p", "r", "", "s")));
        Assert.Equal("curia/flag/detail-unstorable", member.Type);
        Assert.Equal(FlagDetailRules.Unstorable().Title, member.Title);
    }
}
