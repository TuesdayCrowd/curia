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
        new("01J0", moderator, "mod-1", effect, FlagKind.Injection, rationale, Now, []);

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

    private static ModerationAction Adjudicating(
        ModerationEffect effect, ModeratorKind moderator, params string[] flags) =>
        new("01J0", moderator, "mod-1", effect, FlagKind.Injection, "reviewed", Now, [.. flags]);

    private static string[] Sorted(IEnumerable<string> flags) => [.. flags.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Table 11's "≥ 3 questions with no upheld flags" needs a definition of <i>upheld</i>. R10.61:
    /// a flag is upheld while the most recent reviewing record that names it quarantined or withheld
    /// the post. A flag no record names is not upheld — the safe direction, since R10.35 lets every
    /// T0 agent raise one.
    /// </summary>
    [Fact]
    public void R10_39_AnUnreviewedFlagIsNotUpheld() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([]));

    /// <summary>Acting on the content upholds the flags the record names; both acting effects count.</summary>
    [Theory]
    [InlineData(ModerationEffect.Quarantine)]
    [InlineData(ModerationEffect.Withhold)]
    public void R10_39_ActingOnContentUpholdsTheFlagsTheRecordNames(ModerationEffect effect) =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([Adjudicating(effect, ModeratorKind.Human, "f1")])));

    /// <summary>A dismissal is the denominator's other half: reviewed, and found not to warrant action.</summary>
    [Fact]
    public void R10_39_ADismissalDoesNotUpholdTheFlag() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Dismiss, ModeratorKind.Human, "f1")]));

    /// <summary>
    /// A restore reverses the upholding as well as the withholding, for every flag it names — an
    /// agent left demoted by an action a moderator explicitly reversed would be serving it anyway.
    /// </summary>
    [Fact]
    public void R10_39_ARestoreReleasesEveryFlagItNames() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"),
            Adjudicating(ModerationEffect.Restore, ModeratorKind.Human, "f1", "f2")]));

    /// <summary>
    /// R10.61: upholding is decided per flag. A restore that names f1 releases f1 and says nothing
    /// about f2, which the same category's withholding upheld. Keyed to the category, the restore
    /// would have released both.
    /// </summary>
    [Fact]
    public void R10_61_UpholdingIsDecidedPerFlag() =>
        Assert.Equal(["f2"], Sorted(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"),
            Adjudicating(ModerationEffect.Restore, ModeratorKind.Human, "f1")])));

    /// <summary>
    /// R10.61's reason. Keyed to a category, a flag raised after a withholding in its category was
    /// upheld the instant it was raised, by nobody. Keyed to the record that names it, it is not
    /// upheld until a record does.
    /// </summary>
    [Fact]
    public void R10_61_AWithholdingUpholdsOnlyTheFlagsItNames() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human)]));

    /// <summary>The most recent reviewing record governs: the history is the state.</summary>
    [Fact]
    public void UpholdingIsAFoldOverHistory() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Restore, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1")])));

    /// <summary>
    /// PR #59's Task B1, confirmed by execution in Step 2 and fixed here. R10.36: automated
    /// moderation quarantines <i>pending review</i>; pending review is not upheld.
    /// </summary>
    [Fact]
    public void An_automated_quarantine_is_not_an_upheld_flag() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Automated, "f1")]));

    [Fact]
    public void A_human_quarantine_is_an_upheld_flag() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Human, "f1")])));

    [Fact]
    public void A_delegated_agents_quarantine_is_an_upheld_flag() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Quarantine, ModeratorKind.DelegatedAgent, "f1")])));

    /// <summary>
    /// R10.61, and the spec's Decision 20: an automated record changes no flag's state in either
    /// direction. An automated dismissal releasing a flag a human upheld would be a system reviewing
    /// a human.
    /// </summary>
    [Fact]
    public void An_automated_dismissal_does_not_release_a_flag_a_human_upheld() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Dismiss, ModeratorKind.Automated, "f1")])));

    /// <summary>
    /// PR #59's Task B1, second half. The log is append-only, so a record R10.36 forbids can exist in
    /// it — appended by a defect or by a compromised writer. The fold must not honour it, or
    /// appending an event becomes a way to remove content.
    /// </summary>
    [Fact]
    public void An_automated_withholding_does_not_stop_a_post_being_served() =>
        Assert.True(ModerationPolicy.MayServe([Action(ModeratorKind.Automated, ModerationEffect.Withhold)]));

    /// <summary>R10.36 permits exactly this, and it is the reason automated moderation exists.</summary>
    [Fact]
    public void An_automated_quarantine_does_stop_a_post_being_served() =>
        Assert.False(ModerationPolicy.MayServe([Action(ModeratorKind.Automated, ModerationEffect.Quarantine)]));

    /// <summary>R10.39's denominator: the flags a reviewing record named, dismissed or not; never an automated one's.</summary>
    [Fact]
    public void R10_61_AdjudicatedFlagsCountOnlyReviewingRecords() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.AdjudicatedFlags([
            Adjudicating(ModerationEffect.Dismiss, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Automated, "f2")])));

    /// <summary>
    /// Structural equality, including what the record adjudicates. R11.9's rebuild drill compares a
    /// fold with its own rebuild, and a record compared by array reference would make it compare
    /// nothing.
    /// </summary>
    [Fact]
    public void A_moderation_action_is_equal_by_value_including_what_it_adjudicates()
    {
        Assert.Equal(
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"),
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"));

        Assert.NotEqual(
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f2"));
    }

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
