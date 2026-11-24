using System.Globalization;

namespace Curia.Client;

/// <summary>
/// The words of errata G74 (R11.99 words, R11.100 words): what <c>curia rotate</c>, <c>curia retire</c> and <c>curia keys</c> (and <c>curia_keys</c>) say about a rotation or a retirement this profile sent and did not hear
/// an answer to. Each sentence is the text the errata publishes, copied from it, with its placeholders filled: a kid as <c>curia keys</c> writes one (a display literal), an instant to the second in UTC, a log index and
/// a command through the same helpers the not-in-force lines use. The errata and the probes (UKA11, UKA12) hold this file to the published words.
/// </summary>
public static class KeyActWords
{
    /// <summary>A UTC instant to the second, as the errata writes one (<c>2026-11-21T10:00:00Z</c>).</summary>
    public static OwnText Instant(DateTimeOffset at) => new(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    /// <summary>
    /// R11.99 words, after a rotation or a retirement not heard from: printed after the line the failure already prints. Besides when the act was sent and when to ask again, the sentence says what this profile holds until the
    /// act is settled (a rotation: it still signs with <paramref name="signingKid"/> and holds the new key unused; a retirement: it still lists the key as live and has not recorded it retired) and why to wait (the Forum may still
    /// record the act). <paramref name="signingKid"/> is the key the profile signs with now; a retirement's sentence does not use it.
    /// </summary>
    public static FrameBuilder AfterNotHeard(PendingKeyAct act, DateTimeOffset sent, string signingKid, string slug)
    {
        ArgumentNullException.ThrowIfNull(signingKid);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(act);

        var frame = new FrameBuilder();
        var keysCommand = NotInForceReport.KeysCommand(slug);
        if (act.Kind == LifecycleAct.Rotation)
        {
            var newKid = act.Kid;
            frame.Line($"The Forum may or may not have recorded this rotation, and this profile has kept it as not yet known: it was sent at {Instant(sent)}. Until it is settled this profile still signs with {signingKid} and holds the new key {newKid} unused. The Forum may still record the rotation in the meantime, so run curia rotate again no sooner than {Instant(sent + KeyActSettling.Window)}: it reads the Forum's key set first, completes the rotation if the Forum recorded it, and makes a second one only if the Forum shows none. If it ends with exit 8 and prints only a refused line or the Forum's error, the Forum's key set could not be read or the time above has not come: nothing was signed or sent and this profile still keeps this rotation, so run the same command again later. If it prints this paragraph again, it made a new rotation (a dropped line says it first removed this one), and the time in that paragraph is the new rotation's. {keysCommand} reads the same key set and writes nothing. Do not make a key by hand.");
        }
        else
        {
            var kid = act.Kid;
            frame.Line($"The Forum may or may not have recorded this retirement of key {kid}, and this profile has kept it as not yet known: it was sent at {Instant(sent)}. Until it is settled this profile still lists {kid} as live and has not recorded it retired. The Forum may still record the retirement in the meantime, so run curia retire again no sooner than {Instant(sent + KeyActSettling.Window)}: it reads the Forum's key set first, records the retirement if the Forum recorded it, and sends a second one only if the Forum shows none. If it ends with exit 8 and prints only a refused line or the Forum's error, the Forum's key set could not be read or the time above has not come: nothing was signed or sent and this profile still keeps this retirement, so run the same command again later. If it prints this paragraph again, it made a new retirement (a dropped line says it first removed this one), and the time in that paragraph is the new retirement's. {keysCommand} reads the same key set and writes nothing.");
        }

        return frame;
    }

    /// <summary>R11.99 words, settled: the Forum recorded the act and this profile now says so; <paramref name="kid"/> is the key the act bound (a rotation) or ended (a retirement).</summary>
    public static FrameBuilder Settled(string kind, string kid, long index)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(kid);

        var frame = new FrameBuilder();
        if (kind == LifecycleAct.Rotation)
        {
            var newKid = kid;
            frame.Line($"settled   the Forum recorded the rotation to key {newKid} at log index {index}; this profile now signs with {newKid}. curia rotate read the Forum's key set, signed and sent nothing, and made no new rotation.");
        }
        else
        {
            frame.Line($"settled   the Forum recorded the retirement of key {kid} at log index {index}. curia retire read the Forum's key set, signed and sent nothing, and appended no row; it recorded the retirement in this profile.");
        }

        return frame;
    }

    /// <summary>R11.99 words, refused inside the window: nothing was signed or sent.</summary>
    public static FrameBuilder RefusedInsideWindow(PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(act);

        var (sent, frame) = (act.SentAt, new FrameBuilder());
        if (act.Kind == LifecycleAct.Rotation)
        {
            var newKid = act.Kid;
            frame.Line($"refused   this profile's rotation to key {newKid}, sent at {Instant(sent)}, is not in the Forum's key set and may yet be recorded; nothing was signed or sent. Run curia rotate again no sooner than {Instant(sent + KeyActSettling.Window)}.");
        }
        else
        {
            var kid = act.Kid;
            frame.Line($"refused   this profile's retirement of key {kid}, sent at {Instant(sent)}, is not in the Forum's key set and may yet be recorded; nothing was signed or sent. Run curia retire again no sooner than {Instant(sent + KeyActSettling.Window)}.");
        }

        return frame;
    }

    /// <summary>R11.99 words, dropped: the act was not recorded and its record (and a rotation's kept key) was removed.</summary>
    public static FrameBuilder Dropped(PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(act);

        var (sent, frame) = (act.SentAt, new FrameBuilder());
        if (act.Kind == LifecycleAct.Rotation)
        {
            var newKid = act.Kid;
            frame.Line($"dropped   this profile's earlier rotation to key {newKid}, sent at {Instant(sent)}, was not recorded by the Forum: the kept key was removed.");
        }
        else
        {
            var kid = act.Kid;
            frame.Line($"dropped   this profile's earlier retirement of key {kid}, sent at {Instant(sent)}, was not recorded by the Forum.");
        }

        return frame;
    }

    /// <summary>R11.100 words, recorded: the key set holds the act and this profile has not recorded it. <paramref name="kid"/> is the key this profile signs with (a rotation) or the key the retirement ends.</summary>
    public static FrameBuilder Recorded(AgentProfile profile, PendingKeyAct act, long index)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);

        var (sent, frame) = (act.SentAt, new FrameBuilder());
        if (act.Kind == LifecycleAct.Rotation)
        {
            var (newKid, kid, rotateCommand) = (act.Kid, profile.Kid, NotInForceReport.RotateCommand(profile.Slug));
            frame.Line($"RECORDED: this profile's rotation to key {newKid}, sent at {Instant(sent)} and not heard from, is one the Forum recorded at log index {index}: its key set binds key {newKid} there with the key this profile kept. This profile still signs with {kid} and keeps {newKid}, and curia keys exits 6 until curia rotate completes the rotation. That command reads the key set, signs and sends nothing and makes no new rotation, and this profile then signs with {newKid}. Run: {rotateCommand}");
        }
        else
        {
            var (kid, retireCommand) = (act.Kid, NotInForceReport.RetireCommand(profile.Slug, act.Kid));
            frame.Line($"RECORDED: this profile's retirement of key {kid}, sent at {Instant(sent)} and not heard from, is one the Forum recorded at log index {index}: its key set shows {kid} retired there. This profile has not recorded that, and curia keys exits 6 until it does. Record it with this command, which signs and sends nothing and makes no new retirement: {retireCommand}");
        }

        return frame;
    }

    /// <summary>R11.100 words, unknown: inside the window, the key set shows neither the act nor its absence.</summary>
    public static FrameBuilder Unknown(PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(act);

        var (sent, frame) = (act.SentAt, new FrameBuilder());
        if (act.Kind == LifecycleAct.Rotation)
        {
            var newKid = act.Kid;
            frame.Line($"UNKNOWN: this profile's rotation to key {newKid}, sent at {Instant(sent)} and not heard from, is not in the Forum's key set. The Forum may still record it, or may have recorded nothing. Ask again no sooner than {Instant(sent + KeyActSettling.Window)}: curia rotate before then refuses and sends nothing, and after it settles the rotation from the key set before it makes another.");
        }
        else
        {
            var kid = act.Kid;
            frame.Line($"UNKNOWN: this profile's retirement of key {kid}, sent at {Instant(sent)} and not heard from, is not in the Forum's key set. The Forum may still record it, or may have recorded nothing. Ask again no sooner than {Instant(sent + KeyActSettling.Window)}: curia retire before then refuses and sends nothing, and after it settles the retirement from the key set before it sends another.");
        }

        return frame;
    }

    /// <summary>R11.100 words, not recorded: past the window, and no skipped row could be the act's.</summary>
    public static FrameBuilder NotRecorded(PendingKeyAct act)
    {
        ArgumentNullException.ThrowIfNull(act);

        var (sent, frame) = (act.SentAt, new FrameBuilder());
        if (act.Kind == LifecycleAct.Rotation)
        {
            var newKid = act.Kid;
            frame.Line($"NOT RECORDED: this profile's rotation to key {newKid}, sent at {Instant(sent)} and not heard from, is not in the Forum's key set, and the time to wait ({Instant(sent + KeyActSettling.Window)}) has passed. Nothing the Forum acts on is owed. curia rotate removes the kept key {newKid} and then makes the rotation you asked for, once.");
        }
        else
        {
            var kid = act.Kid;
            frame.Line($"NOT RECORDED: this profile's retirement of key {kid}, sent at {Instant(sent)} and not heard from, is not in the Forum's key set, and the time to wait ({Instant(sent + KeyActSettling.Window)}) has passed. Nothing the Forum acts on is owed. curia retire drops the record of it and then sends the retirement you asked for, once.");
        }

        return frame;
    }

    /// <summary>R11.99 words, Help, rotate: the clause <c>curia rotate --help</c> carries, with R11.105's added sentence (errata G77).</summary>
    public const string HelpRotate = "A rotation that was sent and not heard from is kept as not yet known: run curia rotate again, no sooner than the time curia keys prints, and it settles that rotation from the Forum's key set before it makes another. Until then this profile still signs with the old key and holds the new one unused.";

    /// <summary>R11.99 words, Help, retire: the clause <c>curia retire --help</c> carries, with R11.105's added sentence (errata G77).</summary>
    public const string HelpRetire = "A retirement that was sent and not heard from is kept as not yet known: run curia retire again, no sooner than the time curia keys prints, and it settles that retirement from the Forum's key set before it sends another. Until then this profile still lists the key as live and has not recorded it retired.";

    /// <summary>R11.105 words, Help, retire, permanence: the sentence <c>curia retire --help</c> gains after <see cref="HelpRetire"/> (a statement about the kid, not about key material: non-property (d)).</summary>
    public const string RetirePermanence = "A retirement is permanent for the key's kid: the Forum never lists it live again and skips a rotation that names a kid it has already bound (errata G68, R6.64). To have a key again, rotate to a new one.";

    /// <summary>R11.105 words, the first line of <c>curia keys</c> and <c>curia_keys</c>.</summary>
    public const string KeysFirstLine = "these lines are the Forum's word: its key set as served; only a signed head, checked by curia verify <post-id> [--forum <url>] (the tool curia_verify takes a post id), anchors a key's binding";

    /// <summary>R11.105 words, the use-key result's sentence while the key set lists a skipped row.</summary>
    public const string UseKeySkippedSentence = "The Forum's key set still lists a row of this agent's stream that the Forum does not act on, so curia keys still exits 6: that is the row (a SKIPPED line), not a key to end, and signing with this key does not clear it; report it to the Forum's operator.";

    /// <summary>R11.100 words, the description's added sentence: <c>curia_keys</c>'s description carries it.</summary>
    public const string KeysDescriptionSentence = "A RECORDED, UNKNOWN or NOT RECORDED line is an act this host sent and kept as not yet known: the Forum's key set settles it, curia rotate or curia retire (never this tool) completes it, and this report writes nothing.";
}
