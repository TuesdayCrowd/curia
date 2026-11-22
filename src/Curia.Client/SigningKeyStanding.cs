using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>A key this profile holds that the key set serves live with the held key's own public half: its kid and the log index the key set binds it at.</summary>
public sealed record HeldLiveKey(string Kid, long? LogIndex);

/// <summary>
/// Whether the key a profile signs with is one the Forum serves live, and what to say when it is not (errata G75, R11.101): decided from the profile's signing kid and the key set's <c>keys[]</c> alone, so the
/// line is printed whether or not any SKIPPED, MISSING, FOREIGN or NOW IN FORCE line is. <c>curia keys</c>, <c>curia_keys</c> and the footnote after a refused token read it here, from one definition. Every value the
/// Forum served or the profile holds is written as a display literal, and the command only where each value is a shell word (R10.65). A key set that could not be read has no standing: the caller does not call this.
/// </summary>
public static class SigningKeyStanding
{
    private const string Active = "active";

    /// <summary>R11.101 words, the description's added sentence: <c>curia_keys</c>'s description carries it, after R11.100's.</summary>
    public const string KeysDescriptionSentence = "A SIGNING KEY NOT LIVE line says the key this host signs with is not one the Forum serves live; curia use-key (never this tool) makes the host sign with a held key the Forum does serve, and this report writes nothing.";

    /// <summary>
    /// Null when the key set serves the profile's signing kid <c>active</c> (the key is live); otherwise what the key set says of it: null status inside the result means the key set does not list the kid, and
    /// otherwise the status it lists the kid with (the first entry for the kid in the key set's order).
    /// </summary>
    public static NotLive? Read(AgentProfile profile, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        var listed = Sorted(keySet).Where(k => string.Equals(k.Kid, profile.Kid, StringComparison.Ordinal)).ToList();
        return listed.Any(k => string.Equals(k.Status, Active, StringComparison.Ordinal))
            ? null
            : new NotLive(listed.Count == 0 ? null : listed[0].Status ?? "none");
    }

    /// <summary>The key set's keys in the order <c>curia keys</c> lists them: log index, then kid.</summary>
    public static IEnumerable<ForumJwk> Sorted(KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(keySet);
        return keySet.Keys.OrderBy(k => k.LogIndex ?? long.MaxValue).ThenBy(k => k.Kid, StringComparer.Ordinal);
    }

    /// <summary>
    /// The keys this profile holds that the key set serves <c>active</c> with the held key's <c>kty</c>, <c>crv</c>, <c>x</c> and <c>y</c> (a kid alone could be a stranger's), in the key set's order. A held key
    /// whose public half could not be read (<see cref="RetainedKey.Half"/> is null) is not named.
    /// </summary>
    public static ImmutableArray<HeldLiveKey> HeldLive(AgentProfile profile, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        return
        [
            .. Sorted(keySet)
                .Where(k => string.Equals(k.Status, Active, StringComparison.Ordinal)
                    && profile.Retained.Any(h => h.Half is { } half && string.Equals(h.Kid, k.Kid, StringComparison.Ordinal) && KeyActSettling.Holds(k, half)))
                .Select(k => new HeldLiveKey(k.Kid, k.LogIndex)),
        ];
    }

    /// <summary>
    /// The command that makes the profile sign with <paramref name="kid"/> (errata G75, R11.101 words): the agent and the key each go in only as a shell word that does not begin with a quotation mark (R10.65,
    /// R10.66), and a value that is not one leaves the command unprinted, with the line saying where the values are instead.
    /// </summary>
    public static OwnText UseKeyCommand(string slug, string kid)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(kid);

        return TryName(slug, out var agent) && TryName(kid, out var key)
            ? new OwnText(new FrameBuilder().Append($"curia use-key --agent {agent} --kid {key}").ToString())
            : new OwnText(new FrameBuilder().Append($"curia use-key and the agent and key named above ({new OwnText(PrintedCommand.Withheld)})").ToString());
    }

    /// <summary>The line R11.101 says, in its held form or its no-held form, written once into <paramref name="frame"/>.</summary>
    public static void AppendLine(FrameBuilder frame, AgentProfile profile, KeySetDocument keySet, NotLive notLive)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);
        ArgumentNullException.ThrowIfNull(notLive);

        var standing = new OwnText(notLive.Status is null ? "does not list" : "lists as " + DisplayLiteral.Of(notLive.Status));
        var heldKeys = HeldLive(profile, keySet);
        if (heldKeys.IsEmpty)
        {
            frame.Line($"SIGNING KEY NOT LIVE: this profile signs with key {profile.Kid}, which the Forum's key set {standing}, and the Forum serves no key this profile holds. There is nothing in this profile to sign with: do not enrol again and do not retry. Report to the Forum's operator that agent {profile.AgentId} cannot get a token.");
            return;
        }

        var list = new OwnText(string.Join(", ", heldKeys.Select(k => string.Create(CultureInfo.InvariantCulture, $"{DisplayLiteral.Of(k.Kid)} (bound at log index {k.LogIndex})"))));
        var command = UseKeyCommand(profile.Slug, heldKeys[0].Kid);
        frame.Line($"SIGNING KEY NOT LIVE: this profile signs with key {profile.Kid}, which the Forum's key set {standing}. The Forum refuses a token for a key it does not serve live, so every command that needs one fails with invalid_client until this profile signs with a key it does serve. The Forum does serve {heldKeys.Length} key(s) this profile also holds, with the same public key: {list}. This check signed and sent nothing. To sign with one of them: {command}. That command reads the key set, signs and sends nothing, makes no new key and no new act, and changes only this profile's own files.");
    }

    /// <summary>The rotation <c>NOW IN FORCE</c> line while the signing key is not live (errata G75, R11.96 (add.) (rev.)): no remedy that needs a token, and no key said to be the one the profile signs with.</summary>
    public static void AppendRotationNowInForce(FrameBuilder frame, long index, string newKid)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(newKid);

        frame.Line($"NOW IN FORCE: this profile's rotation at log index {index}, which the Forum answered it did not act on, is one it now acts on: key {newKid} is live and this profile holds it. A live key this profile does not sign with is one more key that can sign as this agent, and curia keys exits 6 until it is ended. This profile's signing key is not live, so no command that needs a token can run; see SIGNING KEY NOT LIVE below.");
    }

    private static bool TryName(string value, [NotNullWhen(true)] out ShellWord? word)
    {
        word = null;
        return !value.StartsWith('"') && ShellWord.TryOf(value, out word);
    }
}

/// <summary>What the key set says of a signing kid it does not serve <c>active</c>: <paramref name="Status"/> is null when it lists no such kid, and otherwise the status it lists (a display literal when written).</summary>
public sealed record NotLive(string? Status);
