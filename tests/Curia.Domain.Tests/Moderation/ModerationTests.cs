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
}
