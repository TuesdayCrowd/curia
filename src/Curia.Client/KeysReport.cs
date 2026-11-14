using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>How the comparison of the key set with the profile came out.</summary>
public enum KeysVerdict
{
    /// <summary>A comparison was made and nothing disagreed: exit 0.</summary>
    Clean,

    /// <summary>A <c>MISSING</c>, <c>FOREIGN</c> or <c>SKIPPED</c> line was written: exit 6.</summary>
    Findings,

    /// <summary>No comparison could be made (the key set names no lifecycle, or no key names its binding): exit 8, with <see cref="KeysReading.CouldNotCompare"/>.</summary>
    CouldNotCompare,
}

/// <summary>
/// What <c>curia keys</c> and <c>curia_keys</c> say (errata G68, R11.96): <paramref name="Text"/> is the report as the CLI writes it to standard output, <paramref name="Verdict"/> is what its exit
/// code and the tool's closing line are decided from, and <paramref name="CouldNotCompare"/> is the line that says why no comparison was made (the CLI writes it to standard error, the tool appends it).
/// </summary>
public sealed record KeysReading(string Text, KeysVerdict Verdict, string? CouldNotCompare)
{
    /// <summary>The whole of it, as the tool returns it: the report and, when no comparison was made, the line that says so.</summary>
    public string Render() => CouldNotCompare is null ? Text : Text + CouldNotCompare + "\n";
}

/// <summary>
/// The report <c>curia keys</c> prints (spec section 4.9) and <c>curia_keys</c> returns, from one definition (errata G68, R11.96): one line per key, the lifecycle's extent, a <c>MISSING</c> line for each
/// act this profile recorded that the Forum's lifecycle does not hold, a <c>SKIPPED</c> line for each row of the identity's stream the Forum does not act on, a <c>FOREIGN</c> line for each entry it
/// holds that this profile did not make, and the kinds the Forum enables. Every value the Forum served is written as a display literal unless it is one of a closed set this client knows (R10.63).
///
/// <para><b>Reads the profile alone, not its keys:</b> the verb that tells an owner a thief has acted must run when the owner's own key no longer works.</para>
/// </summary>
public static class KeysReport
{
    /// <summary>Reads the identity's key set and reports it against <paramref name="profile"/>. A refusal is the key-set read's.</summary>
    public static async Task<ForumResult<KeysReading>> ReadAsync(ForumClient client, AgentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(profile);

        var read = await client.GetKeySetAsync(profile.AgentId, ct).ConfigureAwait(false);
        return read.TryGetValue(out var keySet, out var refusal)
            ? ForumResult<KeysReading>.Ok(Of(profile, keySet))
            : ForumResult<KeysReading>.Refused(refusal);
    }

    /// <summary>The report for a key set already read.</summary>
    public static KeysReading Of(AgentProfile profile, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        var frame = new FrameBuilder();
        frame.Line("these lines are the Forum's word: its key set as served; only a signed head, checked by curia verify, anchors a key's binding");
        foreach (var key in keySet.Keys.OrderBy(k => k.LogIndex ?? long.MaxValue).ThenBy(k => k.Kid, StringComparer.Ordinal))
        {
            // The status is the Forum's word: its own four are written as they are, and any other is quoted.
            if (key.Status is "active" or "retired" or "compromised" or "unbound")
                frame.Line($"key {key.Kid} {new OwnText(key.Status)}, bound at log index {key.LogIndex}{RetiredAt(key)}");
            else
                frame.Line($"key {key.Kid} {key.Status}, bound at log index {key.LogIndex}{RetiredAt(key)}");
        }

        long? last = keySet.Lifecycle is { IsEmpty: false } lifecycle ? lifecycle.Max() : null;
        frame.Line($"lifecycle published through log index {last}; {profile.Acts.Length} acts of this profile recorded");

        // A row the Forum skips is in no published lifecycle, so an act this profile recorded at its index would read MISSING; the SKIPPED line replaces it (R11.96).
        var skippedAt = (keySet.Skipped ?? []).Select(r => r.LogIndex).ToHashSet();
        var findings = LifecycleWatch.Compare(profile, keySet);
        foreach (var missing in findings.Where(f => !f.Foreign && !skippedAt.Contains(f.LogIndex)))
            frame.Line($"MISSING: this profile's {new OwnText(missing.Kind)} at log index {missing.LogIndex} is not in the lifecycle the Forum publishes");

        AppendSkipped(frame, profile, keySet, saySoWhenSilent: true);

        var foreign = findings.Where(f => f.Foreign).ToList();
        var text = new System.Text.StringBuilder(frame.ToString());
        if (foreign.Count > 0) text.Append(ForeignLines(foreign).ToString());

        text.Append((keySet.EnabledKinds is { } enabled
            ? new FrameBuilder().Line($"lifecycle kinds this Forum enables: {(enabled.IsEmpty ? new OwnText("none (every lifecycle kind is guarded until the owner lifts the guard, R4.38)") : Literals(enabled))}")
            : new FrameBuilder().Line("lifecycle kinds this Forum enables: its key set does not say")).ToString());

        var disagreed = findings.Any(f => f.Foreign || !skippedAt.Contains(f.LogIndex)) || (keySet.SkippedTotal ?? 0) > 0;
        if (disagreed) return new KeysReading(text.ToString(), KeysVerdict.Findings, null);

        // No finding is a clean comparison only if one could be made: a lifecycle to read and a key naming its binding.
        if (last is null) return new KeysReading(text.ToString(), KeysVerdict.CouldNotCompare, "could not compare: the Forum's key set names no lifecycle");
        if (!keySet.Keys.Any(k => k.LogIndex is not null))
            return new KeysReading(text.ToString(), KeysVerdict.CouldNotCompare, "could not compare: no key names its binding");

        return new KeysReading(text.ToString(), KeysVerdict.Clean, null);
    }

    /// <summary>
    /// Writes the <c>SKIPPED</c> lines for the rows a key set names, in log order, then how many more the total names than the list holds (R11.96). A row at an index the profile recorded an act at is
    /// the second form. A key set without the members writes the line that says the Forum does not say when <paramref name="saySoWhenSilent"/>, and nothing otherwise. Returns whether any line naming a
    /// row was written, which is what makes the caller's exit 6.
    /// </summary>
    public static bool AppendSkipped(FrameBuilder frame, AgentProfile profile, KeySetDocument keySet, bool saySoWhenSilent)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        if (keySet.Skipped is not { } rows)
        {
            if (saySoWhenSilent) frame.Line("the Forum's key set does not say which rows it skips");
            return false;
        }

        var made = profile.Acts.Select(a => a.LogIndex).ToHashSet();
        foreach (var row in rows.OrderBy(r => r.LogIndex))
        {
            var (type, predicate) = (new OwnText(KnownType(row.Type)), new OwnText(KnownPredicate(row.Predicate)));
            if (made.Contains(row.LogIndex))
                frame.Line($"SKIPPED: this profile's {type} at log index {row.LogIndex} is a row the Forum does not act on ({predicate}): what it did is not in force.");
            else
                frame.Line($"SKIPPED: the Forum does not act on a {type} at log index {row.LogIndex} ({predicate}): it ends, binds and marks no key. If this profile did not make it, report it to the Forum's operator.");
        }

        if (keySet.SkippedTotal is { } total && total > rows.Length)
            frame.Line($"and {total - rows.Length} more rows the Forum does not act on (the Forum names the first {ForumDocuments.MaxSkippedListed})");

        return !rows.IsEmpty || (keySet.SkippedTotal ?? 0) > 0;
    }

    /// <summary>The event types a skipped row can be (R6.64's streams): written as they are; any other is the Forum's own string and is written as a display literal.</summary>
    private static readonly ImmutableHashSet<string> KnownTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "agent.key-bound", "agent.key-retired", "agent.key-compromised");

    /// <summary>R6.64's twelve predicates: written as they are; any other is a display literal (R10.63).</summary>
    private static readonly ImmutableHashSet<string> KnownPredicates = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "statement-mismatch", "unknown-lifecycle-kind", "unknown-reason", "broken-link", "lifecycle-typ-mismatch", "rotation-unauthorized",
        "declaration-key-unpublished", "declaration-key-is-log-key", "declaration-key-not-first", "declaration-signature-invalid", "kid-not-bound-to-agent", "declaration-mismatch");

    private static string KnownType(string served) => KnownTypes.Contains(served) ? served : DisplayLiteral.Of(served);

    private static string KnownPredicate(string served) => KnownPredicates.Contains(served) ? served : DisplayLiteral.Of(served);

    /// <summary>
    /// The lines that report entries in an identity's lifecycle that its profile did not make: one each, and then where to go (spec section 4.9; the operator runbook, R12.11, takes the first index as the
    /// latest a compromise declaration may be made from).
    /// </summary>
    public static FrameBuilder ForeignLines(IReadOnlyList<LifecycleFinding> foreign)
    {
        var frame = new FrameBuilder();
        AppendForeign(frame, foreign);
        return frame;
    }

    /// <summary><see cref="ForeignLines"/>, written into a frame that holds other lines already.</summary>
    public static void AppendForeign(FrameBuilder frame, IReadOnlyList<LifecycleFinding> foreign)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(foreign);

        foreach (var entry in foreign.OrderBy(f => f.LogIndex))
            frame.Line($"FOREIGN: a {new OwnText(entry.Kind)} at log index {entry.LogIndex} that this profile did not make");

        var first = foreign.Min(f => f.LogIndex);
        frame.Line($"report it to the Forum's operator; a compromise declaration from no later than log index {first} ends the key it bound (R6.57)");
    }

    private static OwnText RetiredAt(ForumJwk key) =>
        key.RetiredLogIndex is { } retired
            ? new OwnText(", retired at " + retired.ToString(CultureInfo.InvariantCulture))
            : new OwnText(string.Empty);

    /// <summary>A served list, each element a display literal, joined by commas (R10.63, errata G17).</summary>
    private static OwnText Literals(IEnumerable<string> values) =>
        new(string.Join(", ", values.Select(DisplayLiteral.Of)));
}
