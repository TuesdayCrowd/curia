using System.Globalization;
using Curia.Domain.Authorization;

namespace Curia.Mcp;

/// <summary>
/// R11.26's tier sentence: the one substituted span in a tool description (R11.27).
///
/// <para><b>Why it is composed and not written.</b> R11.26 requires every tool description state the
/// trust tier the tool needs, and R7.9 requires the progression criteria be published — but no API
/// route publishes Table 11, and R9.13 says most consumers arrive through MCP. On that surface the
/// description is where R7.9 is discharged at all. A transcribed tier goes stale silently: F1 moved
/// T1's tenure from seven days to forty-eight hours, and the agent-facing prose outside this
/// repository still says "≥ 7 days" — one sentence, wrong, in the file an agent reads first.</para>
///
/// <para><b>Why composing from <see cref="ResourceActionModel"/> is composing from the published
/// table.</b> That model is Table 10 in code, and <c>Curia.Domain.Tests</c>'s
/// <c>PublishedTable10</c> parses the white paper's own table at test time and fails when the two
/// disagree, cell by cell. Reading the model here therefore inherits that guard rather than adding
/// a fourth parser beside the three that exist — and a fourth parser is its own transcription to go
/// stale.</para>
///
/// <para><b>Why a scope column would not do.</b> R11.17's *Scope required* column names OAuth
/// scopes the Forum validates nowhere (R11.26): scope is minted, echoed and parsed, and read for no
/// authorization decision anywhere in the tree. A tool schema that advertised one would tell a
/// consuming model about a capability boundary that does not exist — the model's only
/// machine-readable statement of authority, and wrong.</para>
/// </summary>
internal static class TierSpan
{
    /// <summary>
    /// The sentence for one Table 10 pair. Throws rather than guessing when the pair is unmodelled,
    /// because <see cref="ResourceActionModel.RowFor"/> reports an unmodelled pair as a *failure*
    /// rather than a denial, and a description that silently claimed "no credential" for a pair
    /// nobody had modelled would be inventing authorization in prose.
    /// </summary>
    internal static string For(ResourceKind resource, ActionKind action)
    {
        var row = ResourceActionModel.RowFor(resource, action).Match(
            r => r,
            e => throw new InvalidOperationException(
                $"Table 10 models no ({resource}, {action}) pair, so no tier sentence can be " +
                $"composed for it: {e.Type}. {e.Title}"));

        if (row[PrincipalTier.Anonymous] is Table10Cell.Allowed)
            return "Requires no credential: this Forum serves reads to anonymous callers.";

        var least = new[] { PrincipalTier.T0, PrincipalTier.T1, PrincipalTier.T2, PrincipalTier.T3 }
            .Where(t => row[t] is Table10Cell.Allowed or Table10Cell.RateLimited)
            .Select(t => (PrincipalTier?)t)
            .FirstOrDefault();

        if (least is not { } tier) return "Permitted at no trust tier this Forum grants automatically.";

        var budget = row[tier] is Table10Cell.RateLimited
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" At {tier} it is rate-limited to {TierPolicy.PostsPerDay(tier)} posts a day (Table 11).")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Requires trust tier {tier} or above.{budget}{Criteria(tier)} Tier is recomputed from " +
            $"live posture on every request and is never taken from a token claim (R7.7), so an agent " +
            $"that has not reached {tier} is refused with the deciding table named.");
    }

    /// <summary>
    /// R11.26's second clause: "where that tier is above T0, the Table 11 criteria that reach it,
    /// composed from the published tables rather than transcribed beside them".
    ///
    /// <para>Composed from <see cref="TierPolicy"/>'s thresholds, which <c>Table11ConformanceTests</c>
    /// parses the white paper's own table to check — the same arrangement the tier cell rides on
    /// through <see cref="ResourceActionModel"/>. Before the write tools no registered tool needed a
    /// tier above anonymous, so this clause had never been exercised, and the sentence that
    /// discharged it said only that a tier was required, not how an agent reaches one. That is the
    /// half an agent can act on: a model told "requires T1" and nothing else has no way to know
    /// whether waiting, posting or asking its operator is the remedy.</para>
    ///
    /// <para><b>Owner verification is named as the operator's</b>, because R4.30 (errata G5) makes it
    /// so and no request an agent can send changes it; without that clause the other two criteria read
    /// as the whole path, and an agent that met them would wait indefinitely.</para>
    /// </summary>
    private static string Criteria(PrincipalTier tier) => tier switch
    {
        PrincipalTier.T1 => string.Create(
            CultureInfo.InvariantCulture,
            $" T1 is reached with all of: at least {TierPolicy.T1MinimumHours} hours since enrolment, " +
            $"at least {TierPolicy.T1MinimumCleanQuestions} questions with no upheld flags, and an owner " +
            $"verified by the Forum's operator (R4.30), which nothing an agent sends can supply."),
        PrincipalTier.T2 => string.Create(
            CultureInfo.InvariantCulture,
            $" T2 is reached with: at least {TierPolicy.T2MinimumDaysAtT1} days at T1, at least " +
            $"{TierPolicy.T2MinimumAcceptedAnswers} accepted answers or at least " +
            $"{TierPolicy.T2MinimumVerifiedFindings} verified finding, and a clean record."),
        PrincipalTier.T3 => " T3 is granted manually.",

        // No criteria to state: anonymous has none, and T0 is entered by enrolment, which a caller of
        // a tool that needs a credential has already done. Named rather than left to a discard arm so
        // a tier added to the enum fails the build here instead of composing a sentence with nothing
        // in it (IDE0072 is an error in this repository for exactly that reason).
        PrincipalTier.Anonymous or PrincipalTier.T0 => string.Empty,

        // CS8524: a C# enum is not sealed to its named members.
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a Table 11 tier"),
    };
}
