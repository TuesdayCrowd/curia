using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Canon.Jws;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// WRD8 (errata G77, R11.105, Amendment 1 ruling 2, register item 330 (e)): what <c>curia retire --help</c> says, that a retirement is permanent for the key's <b>kid</b>, is a property of the lifecycle fold, and the oracle
/// is the fold over a log the test builds and not the help. Task 0's K5 measured it; this holds it. The probe asserts kids and nothing else: it does <b>not</b> assert what the fold does with a retired key's public
/// bytes under a new kid, in either direction (Amendment 1: asserting it honoured would ratify an unexamined behaviour as a property, asserting it refused would be red on the tree; the register lead and
/// non-property (d) hold the fact).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetirementPermanenceTests
{
    private static string Status(VerifiedLifecycle verified, params string[] kids) =>
        string.Join(" ", kids.Select(k => $"{k}={verified.Honoured.StatusNow(k)}"));

    /// <summary>
    /// WRD8: enrol K0, rotate to K1, retire K0; then append, with real signatures and behind the use cases, a rotation that names K0 again (authorized by K1) and a second retirement of K0, and then a retirement of K1
    /// by K1 itself. After each, K0 is <c>Retired</c>; the rotation naming K0 is skipped <c>statement-mismatch</c> and the second retirement <c>rotation-unauthorized</c>; the use cases refuse them (<c>kid-taken</c>,
    /// <c>not-live</c>). Green on the tree (the property exists). A control, which carries information only through FWRD8: a fold that removed a kid from its retired set on a repeated rotation would make it red.
    /// </summary>
    [Fact]
    public async Task WRD8_ARetiredKidStaysRetiredWhateverRowNamesItAgain_Control()
    {
        var (harness, k0) = await EnrolledAsync();
        var k1 = TestIdentity.New("alice-2");
        Require(await harness.RotateAsync(Alice, k0, k1));      // index 2
        Require(await harness.RetireAsync(Alice, k0));          // index 3

        async Task<VerifiedLifecycle> Fold() => VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(false)).For(Typed.Agent(Alice));

        var steps = new List<string>
        {
            "after the retirement: " + Status(await Fold().ConfigureAwait(true), "alice-1", "alice-2"),
        };

        // a rotation, authorized by K1, that names K0's kid again: the use case refuses, and a row appended anyway is skipped
        var viaUseCase = await harness.RotateAsync(Alice, k1, k0, 3);
        steps.Add("rotation naming alice-1 again, use case: " + viaUseCase.Match(_ => "accepted", e => e.Type));
        var rotationRow = await AppendRowAsync(harness, Alice, "agent.key-bound", KeyLifecycleTests.RotationPayload(Alice, k1.Material, k0.Material, 3));
        var afterRotation = await Fold().ConfigureAwait(true);
        steps.Add($"row {rotationRow}: skipped [{string.Join(", ", afterRotation.Skipped.Select(r => $"{r.LogIndex} {r.Predicate}"))}]; " + Status(afterRotation, "alice-1", "alice-2"));

        // a second retirement of K0
        var secondUseCase = await harness.RetireAsync(Alice, k0, null, 3);
        steps.Add("second retirement of alice-1, use case: " + secondUseCase.Match(_ => "accepted", e => e.Type));
        var retirementRow = await AppendRowAsync(harness, Alice, "agent.key-retired", KeyLifecycleTests.RetirementPayload(Alice, k0.Material, 3));
        var afterSecond = await Fold().ConfigureAwait(true);
        steps.Add($"row {retirementRow}: skipped [{string.Join(", ", afterSecond.Skipped.Select(r => $"{r.LogIndex} {r.Predicate}"))}]; " + Status(afterSecond, "alice-1", "alice-2"));

        // a further honoured row (K1 rotates to a third key) does not give K0 back
        var k2 = TestIdentity.New("alice-3");
        var viaK1 = await harness.RotateAsync(Alice, k1, k2, 3);
        steps.Add("rotation of K1 to alice-3, use case: " + viaK1.Match(ok => "accepted at " + ok.LogIndex, e => e.Type));
        var last = await Fold().ConfigureAwait(true);
        steps.Add("after a further honoured row: " + Status(last, "alice-1", "alice-2", "alice-3"));

        var expected = string.Join("\n", [
            "after the retirement: alice-1=Retired alice-2=Active",
            "rotation naming alice-1 again, use case: curia/keys/kid-taken",
            "row 4: skipped [4 statement-mismatch]; alice-1=Retired alice-2=Active",
            "second retirement of alice-1, use case: curia/keys/not-live",
            "row 5: skipped [4 statement-mismatch, 5 rotation-unauthorized]; alice-1=Retired alice-2=Active",
            "rotation of K1 to alice-3, use case: accepted at 6",
            "after a further honoured row: alice-1=Retired alice-2=Active alice-3=Active",
        ]);
        Assert.True(string.Equals(expected, string.Join("\n", steps), StringComparison.Ordinal), $"Expected:\n{expected}\nActual:\n{string.Join("\n", steps)}");
    }
}
