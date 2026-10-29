using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>
/// R8.79 (errata G46), TRW45 to TRW48: an acceptance that loses its append is decided again from a fresh read, up to the published number of attempts. The
/// proof is deterministic: the store is <see cref="ConflictingEventStore"/>, which lets a rival append to the thread's stream (through the real use case, on
/// the real in-memory store) between the caller's read and its append, so the conflict is the store's own. The oracle is the stream itself, read from the
/// inner store: the <c>answer.accepted</c> events and who accepted them, never the use case's report of itself. The bound and the slug are read from the
/// errata (<see cref="PublishedAcceptBudget"/>), not from <see cref="AcceptAnswer"/> or <see cref="AcceptErrors"/>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptRaceTests
{
    private const string Root = "01JP0ST0000000000000000001";
    private const string AnswerA = "01JP0ST000000000000000000A";
    private const string AnswerB = "01JP0ST000000000000000000B";
    private const string AnswerC = "01JP0ST000000000000000000C";
    private const string Asker = "https://agents.example/asker";
    private const string Rival = "https://agents.example/rival";

    private static readonly DateTimeOffset Start = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static string RivalAnswer(int n) => "01JP0ST0000000000000000R" + n.ToString("D2", CultureInfo.InvariantCulture);

    private sealed class Scene
    {
        internal ManualTimeProvider Clock { get; } = new(Start);

        internal InMemoryEventStore Inner { get; }

        internal ConflictingEventStore Store { get; }

        internal AcceptAnswer Caller { get; }

        private readonly AcceptAnswer _rival;

        internal Scene()
        {
            Inner = new InMemoryEventStore(Clock, new PermissiveLifecycleAppendGate());
            Store = new ConflictingEventStore(Inner);
            Caller = new AcceptAnswer(Store, Clock);
            _rival = new AcceptAnswer(Inner, Clock);
            Store.Arm(Require(AggregateId.Create(Root)), static (_, _) => Task.FromResult(false));
        }

        /// <summary>The next rival writes <paramref name="answer"/>'s acceptance one minute after the clock stood, and the caller's append now loses.</summary>
        internal async Task<bool> RivalAcceptsAsync(string answer, CancellationToken ct)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            Require(await _rival.RecordAsync(Typed.Post(Root), Typed.Post(answer), Typed.Agent(Rival), ct).ConfigureAwait(false));
            return true;
        }

        internal Task<Result<Acceptance>> CallerAcceptsAsync(string answer, CancellationToken ct) =>
            Caller.RecordAsync(Typed.Post(Root), Typed.Post(answer), Typed.Agent(Asker), ct);

        /// <summary>The thread's <c>answer.accepted</c> events in order, as "accepted_by answer".</summary>
        internal async Task<string[]> EventsAsync(CancellationToken ct) =>
        [
            .. Require(await Inner.ReadByAggregateAsync(Require(AggregateId.Create(Root)), ct).ConfigureAwait(false))
                .Where(e => e.Event.Type.Value == AcceptanceProjector.AnswerAcceptedType)
                .Select(e =>
                {
                    var members = ((Curia.Canon.Json.JsonValue.Object)e.Event.Payload).Members;
                    string Of(string key) => ((Curia.Canon.Json.JsonValue.String)members.Single(m => m.Key == key).Value).Value;
                    return Of(AcceptanceProjector.AcceptedByField) + " " + Of(AcceptanceProjector.AnswerIdField);
                }),
        ];
    }

    /// <summary>
    /// TRW11 (a control, R8.68; green first and held through TRW15 by FTRW12). A replay appends nothing: accepting the standing answer twice leaves one
    /// event, and the second call answers the first call's instant.
    /// </summary>
    [Fact]
    public async Task R8_68_TRW11_AReplayAppendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new Scene();

        var first = Require(await scene.CallerAcceptsAsync(AnswerA, ct));
        scene.Clock.Advance(TimeSpan.FromMinutes(5));
        var replay = Require(await scene.CallerAcceptsAsync(AnswerA, ct));

        Assert.Equal(
            ($"{Asker} {AnswerA}", false, true, first.AcceptedAt),
            (string.Join(" | ", await scene.EventsAsync(ct)), first.AlreadyAccepted, replay.AlreadyAccepted, replay.AcceptedAt));
    }

    /// <summary>
    /// TRW45. The caller's append loses the first K attempts to a rival and the next one lands, at the version of the last read: Ok, and exactly one event of
    /// the caller's. K = 1 is green today (the second look); K = 2 and 7 are the defect.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task R8_79_TRW45_ALoserReDecidesUntilItCanAppend(int lost)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new Scene();
        scene.Store.Arm(Require(AggregateId.Create(Root)), (n, token) => n <= lost ? scene.RivalAcceptsAsync(RivalAnswer(n), token) : Task.FromResult(false));

        var result = await scene.CallerAcceptsAsync(AnswerA, ct);

        Assert.True(result.TryGetValue(out var acceptance, out var error), $"Expected: Ok. Actual: Fail {error?.Type}");
        Assert.Equal(
            (string.Join(" | ", [.. Enumerable.Range(1, lost).Select(n => $"{Rival} {RivalAnswer(n)}"), $"{Asker} {AnswerA}"]), false, RivalAnswer(lost)),
            (string.Join(" | ", await scene.EventsAsync(ct)), acceptance!.AlreadyAccepted, acceptance.ReplacedAnswer!));
    }

    /// <summary>
    /// TRW46. A store that wins every append: past the published bound the use case refuses with the published type (never the store's conflict), the detail is
    /// the thread and nothing the store said, exactly the bound's number of appends were tried, none of the caller's is stored, and a runaway loop is a red
    /// from the decorator and not a hang.
    /// </summary>
    [Fact]
    public async Task R8_79_TRW46_PastTheBoundItRefusesAsContendedAndRecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var bound = PublishedAcceptBudget.Attempts();
        var scene = new Scene();
        scene.Store.Arm(Require(AggregateId.Create(Root)), (n, token) => scene.RivalAcceptsAsync(RivalAnswer(n), token));

        var result = await scene.CallerAcceptsAsync(AnswerA, ct);

        Assert.False(result.TryGetValue(out _, out var error), "the acceptance was recorded although every append was lost");
        var events = await scene.EventsAsync(ct);
        Assert.Equal(
            (PublishedAcceptBudget.ContendedType(), $"thread={Root}", bound, 0),
            (error!.Type, error.Detail!, scene.Store.AppendsTried, events.Count(e => e.StartsWith(Asker, StringComparison.Ordinal))));
    }

    /// <summary>The bound the use case declares is the published one (TRW46's other side: a change to the constant alone is a red here).</summary>
    [Fact]
    public void R8_79_TRW46_TheDeclaredBoundIsThePublishedOne() =>
        Assert.Equal(AcceptAnswer.Attempts, PublishedAcceptBudget.Attempts());

    /// <summary>
    /// TRW47. A loser that meets its own answer standing records nothing. A single rival appending A before the first conflict is the control (the second
    /// pass finds it); two rivals, B and then A, put the finding on the third pass. Events 2, not 3, and the replay answers the first time.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task R8_79_TRW47_ALoserThatFindsItsOwnAnswerStandingRecordsNothing(int rivals)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new Scene();
        DateTimeOffset firstAcceptance = default;
        scene.Store.Arm(
            Require(AggregateId.Create(Root)),
            async (n, token) =>
            {
                if (n > rivals) return false;

                // The last rival accepts the caller's own answer; any before it accepts another.
                await scene.RivalAcceptsAsync(n == rivals ? AnswerA : AnswerB, token).ConfigureAwait(false);
                if (n == rivals) firstAcceptance = scene.Clock.GetUtcNow();
                return true;
            });

        var result = await scene.CallerAcceptsAsync(AnswerA, ct);

        Assert.True(result.TryGetValue(out var acceptance, out var error), $"Expected: AlreadyAccepted. Actual: Fail {error?.Type}");
        Assert.Equal(
            (true, firstAcceptance, rivals),
            (acceptance!.AlreadyAccepted, acceptance.AcceptedAt, (await scene.EventsAsync(ct)).Length));
    }

    /// <summary>
    /// TRW48. <c>replaced_answer</c> names what stood at the pass that appended (R8.71): rivals append B then C, the caller's A lands on the third pass, and the
    /// answer it replaced is C and not B.
    /// </summary>
    [Fact]
    public async Task R8_79_TRW48_TheReplacedAnswerIsTheOneThatStoodAtThePassThatAppended()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new Scene();
        scene.Store.Arm(
            Require(AggregateId.Create(Root)),
            (n, token) => n <= 2 ? scene.RivalAcceptsAsync(n == 1 ? AnswerB : AnswerC, token) : Task.FromResult(false));

        var result = await scene.CallerAcceptsAsync(AnswerA, ct);

        Assert.True(result.TryGetValue(out var acceptance, out var error), $"Expected: Ok. Actual: Fail {error?.Type}");
        Assert.Equal(
            (AnswerC, 3),
            (acceptance!.ReplacedAnswer!, (await scene.EventsAsync(ct)).Length));
    }
}
