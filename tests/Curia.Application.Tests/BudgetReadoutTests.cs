using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Collections.Immutable;
using Curia.Application.Budget;
using Curia.Application.Projections;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// Errata G49 (R7.28), SEB1 to SEB5 and SEB7: the standing's arithmetic over instants, pure. <b>These are consistency checks of one arithmetic and say so</b>:
/// they check <see cref="BudgetReadout"/> against hand-derived cases and against a count written out in the test, but the independent checks (the running
/// policy at a settable clock's tick, the events table by SQL) are SEB13 and SEB14 in <c>Curia.Api.Tests</c>. This is the one file that may name
/// <see cref="BudgetReadout"/> for an expectation (SEB50 scans the others). The window is R7.28's number, parsed from the errata.
///
/// <para><b>Red text on the seam tree</b> (recorded in the Task 0 report): <see cref="BudgetReadout.Entry"/>, <see cref="BudgetReadout.After"/> and
/// <see cref="BudgetReadout.NextSlotAt"/> are inert (an entry with nothing stated, null), so every case that expects a count or an instant is red by
/// assertion; SEB7 is a control and is green.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReadoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan Window => TimeSpan.FromSeconds(PublishedBudgetReadout.WindowSeconds());

    private static DateTimeOffset At(TimeSpan age) => Now - age;

    /// <summary>
    /// SEB1. R7.28: "An entry is counted while it is less than 86,400 seconds old, so the slot an entry held is free at the instant the entry is exactly
    /// 86,400 seconds old." Written by hand from that sentence: exactly the window is not counted, one tick younger is.
    /// </summary>
    [Fact]
    public void R7_28_SEB1_AnEntryExactlyTheWindowOldIsNotCountedAndOneTickYoungerIs()
    {
        var cases = new (string Name, DateTimeOffset[] Instants, int Used)[]
        {
            ("exactly the window old", [At(Window)], 0),
            ("one tick younger than the window", [At(Window - TimeSpan.FromTicks(1))], 1),
            ("one tick older than the window", [At(Window + TimeSpan.FromTicks(1))], 0),
            ("two at the boundary, one inside", [At(Window), At(Window), At(TimeSpan.FromHours(1))], 1),
            ("none", [], 0),
        };

        var problems = cases
            .Select(c => (c.Name, c.Used, Actual: BudgetReadout.Entry("posting", 3, c.Instants, Now).Used))
            .Where(r => r.Used != r.Actual)
            .Select(r => $"{r.Name}: Expected: used {r.Used} Actual: used {r.Actual}")
            .ToArray();

        Assert.True(problems.Length == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// SEB2. R7.28: <c>next_slot_at</c> "is the instant at which the k-th oldest counted entry reaches that age, where k is the larger of 1 and used less
    /// limit plus 1, and is null where nothing is counted". Hand-derived for a limit of 3, each row's arithmetic written out.
    /// </summary>
    [Fact]
    public void R7_28_SEB2_NextSlotAtIsTheKthOldestPlusTheWindow()
    {
        var (a, b, c, d, e) = (At(TimeSpan.FromHours(10)), At(TimeSpan.FromHours(8)), At(TimeSpan.FromHours(6)), At(TimeSpan.FromHours(4)), At(TimeSpan.FromHours(2)));

        var cases = new (string Name, DateTimeOffset[] Counted, DateTimeOffset? Expected)[]
        {
            ("no entries: null", [], null),
            ("two entries (k = 1): the older, a + window", [b, a], a + Window),
            ("three entries (used = limit, k = 1): the oldest, a + window", [c, a, b], a + Window),
            ("five entries (k = 5 - 3 + 1 = 3): the third oldest, c + window", [e, c, a, d, b], c + Window),
            ("four entries (k = 2): the second oldest, b + window", [d, b, a, c], b + Window),
            ("a tie at the oldest (a, a, b: k = 1): that tick, a + window", [b, a, a], a + Window),
            ("a tie across the k-th (a, a, a, b: used 4, k = 2): the second oldest, which is a, a + window", [a, b, a, a], a + Window),
        };

        var problems = cases
            .Select(x => (x.Name, x.Expected, Actual: BudgetReadout.NextSlotAt(x.Counted, 3)))
            .Where(r => r.Expected != r.Actual)
            .Select(r => $"{r.Name}: Expected: {r.Expected?.ToString("O") ?? "null"} Actual: {r.Actual?.ToString("O") ?? "null"}")
            .ToArray();

        Assert.True(problems.Length == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// SEB3. R7.28: "Where a budget has no fixed limit, limit, remaining and next_slot_at SHALL be null and used the count." The policy's sentinel for
    /// "negotiated" is <see cref="int.MaxValue"/>; a readout that stated it as a limit would state 2,147,483,647.
    /// </summary>
    [Fact]
    public void R7_28_SEB3_ANegotiatedLimitIsNullAndNeverTheSentinel()
    {
        var entry = BudgetReadout.Entry("posting", int.MaxValue, [At(TimeSpan.FromHours(3)), At(TimeSpan.FromHours(1))], Now);

        Assert.Equal<(int? Limit, int Used, int? Remaining, DateTimeOffset? Next)>((null, 2, null, null), (entry.Limit, entry.Used, entry.Remaining, entry.NextSlotAt));
    }

    /// <summary>SEB4. R7.28: "the remaining count (the larger of 0 and limit less used)". A property over limits, counts and instants, never negative.</summary>
    [Fact]
    public void R7_28_SEB4_RemainingIsTheLargerOfZeroAndLimitLessUsed() =>
        Gen.Select(Gen.Int[0, 40], Gen.Int[0, 3 * 86_400].List[0, 60])
            .Sample(
                (limit, ages) =>
                {
                    var instants = ages.Select(a => Now - TimeSpan.FromSeconds(a)).ToArray();
                    var inside = instants.Count(i => i > Now - Window);
                    var entry = BudgetReadout.Entry("flag", limit, instants, Now);
                    return entry.Remaining == Math.Max(0, limit - inside) && entry.Remaining >= 0;
                },
                iter: 200);

    /// <summary>
    /// SEB5. A receipt equals a readout: for any counted instants and any act, <see cref="BudgetReadout.After"/> equals <see cref="BudgetReadout.Entry"/> over
    /// the union, and its count is the one written out here (so two inert functions that agree on nothing are not a pass).
    /// </summary>
    [Fact]
    public void R7_29_SEB5_AReceiptEqualsAReadoutOverTheUnion() =>
        Gen.Select(Gen.Int[0, 12], Gen.Int[0, 2 * 86_400].List[0, 20], Gen.Int[0, 5])
            .Sample(
                (limit, ages, actAge) =>
                {
                    var counted = ages.Select(a => Now - TimeSpan.FromSeconds(a)).Where(i => i > Now - Window).ToArray();
                    var act = Now - TimeSpan.FromSeconds(actAge);
                    var after = BudgetReadout.After("posting", limit, counted, act, Now);
                    var readout = BudgetReadout.Entry("posting", limit, [.. counted, act], Now);
                    return after == readout && after.Used == counted.Length + 1;
                },
                iter: 200);

    /// <summary>
    /// SEB7 (green on the seam tree: a control; red under FSEB55). The standing is pure and stateless: no mutable static, no clock in any signature;
    /// <c>now</c> is a parameter.
    /// </summary>
    [Fact]
    public void R7_28_SEB7_TheStandingIsPureAndStateless()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var problems = new List<string>();

        foreach (var type in new[] { typeof(BudgetReadout), typeof(BudgetKinds), typeof(BudgetEntry), typeof(BudgetKind) })
        {
            foreach (var field in type.GetFields(All))
            {
                if (field.IsLiteral) continue;
                if (field.IsStatic && !field.IsInitOnly) problems.Add($"{type.Name}.{field.Name} is a static that is not readonly");
                if (field.IsStatic && Mutable(field.FieldType)) problems.Add($"{type.Name}.{field.Name} is a static of a mutable type {field.FieldType.Name}");
                if (field.FieldType == typeof(TimeProvider)) problems.Add($"{type.Name}.{field.Name} holds a TimeProvider");
            }

            foreach (var method in type.GetMethods(All))
            {
                var types = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
                if (types.Any(t => t == typeof(TimeProvider))) problems.Add($"{type.Name}.{method.Name} takes or returns a TimeProvider");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }


    private static AgentId Agent() => AgentId.Parse("https://agents.example/budget-unit").Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static ImmutableArray<RaisedFlag> TwoFlags() =>
        [
            new("f1", "p1", Agent().Value, FlagKind.Spam, ServerTimestamp.At(At(TimeSpan.FromHours(3)))),
            new("f2", "p1", Agent().Value, FlagKind.Incorrect, ServerTimestamp.At(At(TimeSpan.FromHours(1)))),
        ];

    /// <summary>
    /// SEB27, pure half (ruling 17, M5: no event in this build quarantines a credential, so the route half cannot be written). A quarantined credential reads:
    /// its word is <c>quarantined</c> and all three budgets are stated, in R9.50's order, whatever the credential (a read is within the tier's answer).
    /// </summary>
    [Fact]
    public void R9_50_SEB27_AQuarantinedCredentialReadsAllThreeBudgets()
    {
        var entries = BudgetReadout.Read([], [], Agent(), 3, 10, 10, Now);

        var word = BudgetKinds.CredentialWord(CredentialState.Quarantined);
        Assert.Equal(
            (PublishedBudgetReadout.CredentialWords()[1], string.Join(',', PublishedBudgetReadout.Names())),
            (word.TryGetValue(out var w, out _) ? w : "no readout", string.Join(',', entries.Select(e => e.Name))));
    }

    /// <summary>
    /// SEB28, pure T3 leg (ruling 17: <c>ManuallyGrantedT3</c> is set by no event). At T3 every limit is the policy's "Negotiated" sentinel and the readout
    /// states <c>null</c> limit, remaining and next slot with <c>used</c> the count (two flags counted), never 2,147,483,647.
    /// </summary>
    [Fact]
    public void R9_50_SEB28_AT3GrantReadsNullLimitsWithUsedStated()
    {
        var entries = BudgetReadout.Read(
            [], TwoFlags(), Agent(), TierPolicy.PostsPerDay(PrincipalTier.T3), TierPolicy.AcceptsPerDay(PrincipalTier.T3), TierPolicy.FlagsPerDay(PrincipalTier.T3), Now);

        Assert.Equal(
            "posting:null,0,null,null acceptance:null,0,null,null flag:null,2,null,null",
            string.Join(' ', entries.Select(e => $"{e.Name}:{(e.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")},{e.Used},{(e.Remaining?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")},{(e.NextSlotAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "null")}")));
    }

    private static bool Mutable(Type type) =>
        type.IsGenericType
            ? type.GetGenericTypeDefinition() is { } d && (d == typeof(List<>) || d == typeof(Dictionary<,>) || d == typeof(HashSet<>) || d.FullName!.StartsWith("System.Collections.Concurrent", StringComparison.Ordinal))
            : type.IsArray;
}
