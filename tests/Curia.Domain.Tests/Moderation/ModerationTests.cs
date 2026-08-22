using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests.Moderation;

/// <summary>R10.35–R10.39: flags, who may act on them, and the property that no action deletes.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ModerationTests
{
    private static readonly ServerTimestamp Now =
        ServerTimestamp.At(new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));

    private static ModerationAction Action(
        ModeratorKind moderator, ModerationEffect effect, string rationale = "reviewed") =>
        new("01J0", moderator, "mod-1", effect, FlagKind.Injection, rationale, Now);

    /// <summary>R10.35's seven types, exactly. An eighth would be a specification change.</summary>
    [Fact]
    public void R10_35_ThereAreExactlySevenFlagTypes()
    {
        Assert.Equal(7, Enum.GetValues<FlagKind>().Length);

        foreach (var expected in (FlagKind[])[
            FlagKind.Injection, FlagKind.CredentialLeak, FlagKind.Incorrect, FlagKind.Spam,
            FlagKind.Duplicate, FlagKind.LicenseViolation, FlagKind.MaliciousCode])
            Assert.Contains(expected, Enum.GetValues<FlagKind>());
    }

    /// <summary>
    /// <b>The constraint the whole system rests on, asserted as a property of the type.</b>
    ///
    /// <para>R10.26: "editing the content would invalidate the author's signature (§6.4), so there is
    /// no redaction primitive in this system." There is therefore no <c>Delete</c> and no
    /// <c>Redact</c> effect — and this fails if anyone adds one, which is the only moment at which
    /// the omission could stop being deliberate.</para>
    ///
    /// <para>Checked by name over the whole enum rather than by listing the four that exist: a test
    /// that enumerated the permitted members would pass unchanged when a fifth arrived.</para>
    /// </summary>
    [Fact]
    public void R10_26_NoModerationEffectDeletesOrRedacts()
    {
        var forbidden = Enum.GetNames<ModerationEffect>()
            .Where(name =>
                name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Redact", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Erase", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(forbidden);
    }

    /// <summary>
    /// R10.36's load-bearing cell: automated moderation may quarantine pending review and may not
    /// withhold permanently.
    ///
    /// <para>R10.9 says injection detectors have meaningful false-positive rates, so a detector able
    /// to permanently silence an author without review would make every false positive
    /// irreversible.</para>
    /// </summary>
    [Fact]
    public void R10_36_AutomatedModerationMayQuarantineButNotWithhold()
    {
        Assert.True(ModerationPolicy.Authorize(
            Action(ModeratorKind.Automated, ModerationEffect.Quarantine)).TryGetValue(out _, out _));

        Assert.False(ModerationPolicy.Authorize(
            Action(ModeratorKind.Automated, ModerationEffect.Withhold)).TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/not-permitted", error!.Type);

        // Nor may it restore: a restore is a review outcome, and an automated system that could
        // reverse its own quarantine would be reviewing itself.
        Assert.False(ModerationPolicy.Authorize(
            Action(ModeratorKind.Automated, ModerationEffect.Restore)).TryGetValue(out _, out _));
    }

    [Theory]
    [InlineData(ModeratorKind.Human)]
    [InlineData(ModeratorKind.DelegatedAgent)]
    public void R10_36_HumansAndDelegatedAgentsMayTakeAnyAction(ModeratorKind moderator)
    {
        foreach (var effect in Enum.GetValues<ModerationEffect>())
            Assert.True(
                ModerationPolicy.Authorize(Action(moderator, effect)).TryGetValue(out _, out var error),
                $"{moderator} could not {effect}: {error?.Type}");
    }

    /// <summary>R10.37: actor, category and rationale on every action, so an empty rationale is refused.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void R10_37_AnActionWithoutARationaleIsRefused(string rationale)
    {
        var result = ModerationPolicy.Authorize(
            Action(ModeratorKind.Human, ModerationEffect.Withhold, rationale));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-required", error!.Type);
    }

    /// <summary>Servability is a fold over history, so nothing can go stale.</summary>
    [Fact]
    public void Servability_is_a_fold_over_history()
    {
        Assert.True(ModerationPolicy.MayServe([]));

        Assert.False(ModerationPolicy.MayServe([
            Action(ModeratorKind.Automated, ModerationEffect.Quarantine)]));

        Assert.True(ModerationPolicy.MayServe([
            Action(ModeratorKind.Automated, ModerationEffect.Quarantine),
            Action(ModeratorKind.Human, ModerationEffect.Restore)]));

        Assert.False(ModerationPolicy.MayServe([
            Action(ModeratorKind.Automated, ModerationEffect.Quarantine),
            Action(ModeratorKind.Human, ModerationEffect.Restore),
            Action(ModeratorKind.Human, ModerationEffect.Withhold)]));
    }

    /// <summary>
    /// A dismissal changes nothing about servability — it is a decision not to act. Recorded anyway,
    /// because R10.39 publishes the upheld rate and a dismissal is the denominator's other half.
    /// </summary>
    [Fact]
    public void R10_39_ADismissalIsRecordedButChangesNothing()
    {
        Assert.True(ModerationPolicy.MayServe([Action(ModeratorKind.Human, ModerationEffect.Dismiss)]));

        Assert.False(ModerationPolicy.MayServe([
            Action(ModeratorKind.Human, ModerationEffect.Withhold),
            Action(ModeratorKind.Human, ModerationEffect.Dismiss)]));
    }

    /// <summary>
    /// Every (moderator, effect) pair has a decided answer. A pair nobody thought about would throw
    /// on the moderation path in production, so it is enumerated here instead.
    /// </summary>
    [Fact]
    public void Every_moderator_effect_pair_is_decided()
    {
        var decided = 0;

        foreach (var moderator in Enum.GetValues<ModeratorKind>())
        foreach (var effect in Enum.GetValues<ModerationEffect>())
        {
            ModerationPolicy.Authorize(Action(moderator, effect));
            decided++;
        }

        // 3 moderator kinds x 4 effects.
        Assert.Equal(12, decided);
    }

    /// <summary>
    /// A moderation action carries no content — the same structural property <c>RiskFlag</c> has, for
    /// a related reason: a moderation log that quoted the content it withheld would republish it,
    /// which for a credential leak is precisely the harm the withholding was for.
    /// </summary>
    [Fact]
    public void A_moderation_action_carries_no_content()
    {
        var properties = typeof(ModerationAction).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(
            properties,
            p => p.PropertyType == typeof(byte[]) || p.PropertyType == typeof(char[]));

        // The string members are an id, an actor, and a rationale a moderator wrote — never the post.
        Assert.Equal(
            new[]
            {
                nameof(ModerationAction.PostId),
                nameof(ModerationAction.ActorId),
                nameof(ModerationAction.Rationale),
            },
            properties.Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToArray());
    }

    private static ModerationAction On(
        FlagKind category, ModerationEffect effect, ModeratorKind moderator = ModeratorKind.Human) =>
        new("01J0", moderator, "mod-1", effect, category, "reviewed", Now);

    /// <summary>
    /// Table 11's "≥ 3 questions with no upheld flags" needs a definition of <i>upheld</i>, and the
    /// specification never gives one directly — R10.39 publishes an "upheld rate" and leaves the
    /// numerator to the implementation. It is defined here as the moderation outcome, not the flag:
    /// a flag is upheld when the most recent moderation action citing its category acted on the
    /// content. A flag nobody has reviewed is not upheld, which is the safe direction — the opposite
    /// reading would let any agent demote any other by raising a flag nobody adjudicates.
    /// </summary>
    [Fact]
    public void R10_39_AnUnreviewedFlagIsNotUpheld() =>
        Assert.False(ModerationPolicy.IsUpheld(FlagKind.Injection, []));

    /// <summary>Acting on the content upholds the flag; both acting effects count.</summary>
    [Theory]
    [InlineData(ModerationEffect.Quarantine)]
    [InlineData(ModerationEffect.Withhold)]
    public void R10_39_ActingOnContentUpholdsTheFlag(ModerationEffect effect) =>
        Assert.True(ModerationPolicy.IsUpheld(FlagKind.Injection, [On(FlagKind.Injection, effect)]));

    /// <summary>
    /// A dismissal is the denominator's other half: reviewed, and found not to warrant action.
    /// </summary>
    [Fact]
    public void R10_39_ADismissalDoesNotUpholdTheFlag() =>
        Assert.False(ModerationPolicy.IsUpheld(FlagKind.Injection, [On(FlagKind.Injection, ModerationEffect.Dismiss)]));

    /// <summary>
    /// A restore reverses the upholding as well as the withholding. R7.8 requires demotion on
    /// posture degradation; nothing in Table 11 says the degradation outlives the decision that
    /// caused it, and an agent left demoted by a reversed action would be serving a penalty a
    /// moderator explicitly lifted.
    /// </summary>
    [Fact]
    public void R10_39_ARestoreReversesTheUpholding() =>
        Assert.False(ModerationPolicy.IsUpheld(FlagKind.Injection, [
            On(FlagKind.Injection, ModerationEffect.Withhold),
            On(FlagKind.Injection, ModerationEffect.Restore)]));

    /// <summary>
    /// Categories are decided independently. A post withheld for <c>Spam</c> says nothing about
    /// whether its <c>Injection</c> flag was upheld — R10.37 records a category on every action
    /// precisely so the two can be told apart, and collapsing them would make one moderator's
    /// decision silently answer a question they never considered.
    /// </summary>
    [Fact]
    public void R10_37_UpholdingIsDecidedPerCategory()
    {
        var history = (ModerationAction[])[On(FlagKind.Spam, ModerationEffect.Withhold)];

        Assert.True(ModerationPolicy.IsUpheld(FlagKind.Spam, history));
        Assert.False(ModerationPolicy.IsUpheld(FlagKind.Injection, history));
    }

    /// <summary>
    /// The most recent decision in a category governs, for the reason <see cref="ModerationPolicy.MayServe"/>
    /// folds rather than stores: the history is the state, so a reversal needs nothing invalidated.
    /// </summary>
    [Fact]
    public void UpholdingIsAFoldOverHistory() =>
        Assert.True(ModerationPolicy.IsUpheld(FlagKind.Injection, [
            On(FlagKind.Injection, ModerationEffect.Quarantine),
            On(FlagKind.Injection, ModerationEffect.Restore),
            On(FlagKind.Injection, ModerationEffect.Withhold)]));

    /// <summary>
    /// R10.35 names the seven types in the spelling they travel in: <c>injection</c>,
    /// <c>credential_leak</c>, <c>incorrect</c>, <c>spam</c>, <c>duplicate</c>,
    /// <c>license_violation</c>, <c>malicious_code</c>. Asserted against the published strings
    /// rather than against <c>ToString()</c>, because the C# member names differ from them and a
    /// wire format derived from an identifier changes whenever someone renames the identifier.
    /// </summary>
    [Theory]
    [InlineData(FlagKind.Injection, "injection")]
    [InlineData(FlagKind.CredentialLeak, "credential_leak")]
    [InlineData(FlagKind.Incorrect, "incorrect")]
    [InlineData(FlagKind.Spam, "spam")]
    [InlineData(FlagKind.Duplicate, "duplicate")]
    [InlineData(FlagKind.LicenseViolation, "license_violation")]
    [InlineData(FlagKind.MaliciousCode, "malicious_code")]
    public void R10_35_FlagKindsTravelInTheSpellingTheSpecificationPublishes(FlagKind kind, string wire)
    {
        Assert.Equal(wire, FlagKinds.Wire(kind));
        Assert.True(FlagKinds.Parse(wire).TryGetValue(out var parsed, out _));
        Assert.Equal(kind, parsed);
    }

    /// <summary>
    /// An unrecognised spelling is a failure, never a default. A parser that fell back to a member
    /// would let a typo silently become a real flag category, and R10.39's per-category statistics
    /// would then be counting something nobody raised.
    /// </summary>
    [Fact]
    public void R10_35_AnUnknownFlagSpellingDoesNotParse()
    {
        Assert.False(FlagKinds.Parse("not_a_flag").TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/unknown-kind", error!.Type);
    }

    /// <summary>Every member round-trips, so an eighth type added without a spelling fails here.</summary>
    [Fact]
    public void EveryFlagKindHasAWireSpelling()
    {
        foreach (var kind in Enum.GetValues<FlagKind>())
        {
            Assert.True(FlagKinds.Parse(FlagKinds.Wire(kind)).TryGetValue(out var parsed, out _));
            Assert.Equal(kind, parsed);
        }
    }
}
