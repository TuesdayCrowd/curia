using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>Which send of an act an answer is the answer to (errata G58, R11.79): the first, or one the agent asked for after it.</summary>
public enum ActSend
{
    /// <summary>The first send of a signed act.</summary>
    First,

    /// <summary>A send of the kept bytes the agent asked for (<c>curia resend</c>, <c>curia_resend</c>).</summary>
    Resend,
}

/// <summary>What an answer does to a kept act.</summary>
public enum ActFate
{
    /// <summary>The Forum answered definitely for this send: the file is removed.</summary>
    Remove,

    /// <summary>The outcome of the act is not known to this host: the file stays.</summary>
    Keep,
}

/// <summary>How an answer reached the client: a response, or the lack of one.</summary>
public enum ActTransport
{
    /// <summary>A response was read; <see cref="ActAnswer.Status"/> and <see cref="ActAnswer.ProblemType"/> say which.</summary>
    Answered,

    /// <summary>The request was sent and no response was read (R11.77).</summary>
    Unheard,

    /// <summary>The transport failed in a way that provably precedes the request being written.</summary>
    ProvablyNotSent,

    /// <summary>The caller's own cancellation.</summary>
    Cancelled,
}

/// <summary>
/// What a send was to the stop rule (errata G58, R11.80): <see cref="Unanswered"/> when no answer was read (the unheard refusal) or the Forum answered <c>5xx</c>, typed or not;
/// <see cref="Answered"/> for any other response, a <c>4xx</c> included, whatever it says of the first send; <see cref="Neither"/> for a send that did not reach the Forum or was stopped by the caller.
/// </summary>
public enum SendClass
{
    /// <summary>No answer was read, or the Forum answered <c>5xx</c>.</summary>
    Unanswered,

    /// <summary>A response below <c>500</c> was read.</summary>
    Answered,

    /// <summary>No answer and no failure of the Forum: the request provably did not leave, or the caller's own cancellation.</summary>
    Neither,
}

/// <summary>What <see cref="KeptActs.Settle"/> did: whether the act is removed or kept and, where kept, the number of consecutive sends ending with this one that the Forum did not answer.</summary>
public readonly record struct Settled(ActFate Fate, int Unanswered);

/// <summary>An answer, as <see cref="KeptActs.Decide"/> reads it: the status and problem type of a response, or how the transport ended. No text the Forum wrote is carried.</summary>
public readonly record struct ActAnswer(ActTransport Transport, int Status = 0, string? ProblemType = null, bool BodyReadable = true);

/// <summary>The header line of a kept act: what <c>acts</c> lists, read from line 1 alone (R11.79). Nothing the Forum wrote is in it.</summary>
public sealed record KeptActHeader(
    string Digest,
    string Kind,
    string Board,
    string Forum,
    string Author,
    DateTimeOffset KeptAt,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    string? LastAnswer,
    int Unanswered = 0);

/// <summary>What <see cref="KeptActs.Discard"/> did (plan ruling 40): removed the one file, found none, or could not remove it.</summary>
public enum DiscardStatus
{
    /// <summary>The file of exactly that digest was removed.</summary>
    Removed,

    /// <summary>No act of that digest is kept here (a digest not in the published form is never held): nothing was removed.</summary>
    NotHeld,

    /// <summary>A file is held and could not be removed: it is still kept. <see cref="DiscardOutcome.Reason"/> names the failure.</summary>
    Failed,
}

/// <summary>The outcome of a discard: the status and, for <see cref="DiscardStatus.Failed"/>, the name of the failure (a type name, never text a file or the Forum wrote).</summary>
public readonly record struct DiscardOutcome(DiscardStatus Status, string? Reason = null);

/// <summary>A kept act: its header and the exact signed bytes.</summary>
public sealed record KeptAct(KeptActHeader Header, ReadOnlyMemory<byte> Wire);

/// <summary>One line of the listing: a header, or the name of a file whose header could not be read.</summary>
public sealed record KeptActListing(KeptActHeader? Header, string? DamagedFile, string? Name = null);

/// <summary>
/// R11.79 (errata G58): the signed bytes of every post and vote this host sent, kept in the agent's own directory from before the first byte is sent until the Forum answers
/// definitely, and sent again exactly when asked. <b>The only file that names the on-disk form</b> (plan ruling 4): <c>acts/&lt;64 hex&gt;.act</c>, line 1 one JSON object (<c>v</c>,
/// <c>digest</c> as <c>sha256:&lt;hex&gt;</c>, <c>kind</c>, <c>board</c>, <c>forum</c> as the profile's origin, <c>author</c>, <c>kept_at</c>, <c>attempts</c>, <c>last_attempt_at</c>,
/// <c>last_answer</c>: a problem type or a status, never text from the Forum; <c>unanswered</c>: the run of sends the Forum did not answer, absent reads as 0), line 2 the wire in base64; mode 0600 in a directory of mode 0700, written through
/// <see cref="PrivateFiles"/>. The answer table (<see cref="Decide"/>) is the one definition of which answers leave an act unknown.
///
/// <para><b>The header's length.</b> A listing reads one block of <see cref="HeaderBlockBytes"/> bytes per file (plan ruling 28) and finds line 1 in it. The header's largest
/// parts are the author (at most 1,024 UTF-8 bytes, <c>AgentId</c>'s cap), the board (at most 64 scalar values, 256 bytes), the Forum's origin (a scheme, a host of at most 253
/// characters and a port) and a handful of fixed-form members (about 400 bytes with the two instants and the last answer, itself capped): about 2.2 KiB in the worst
/// case with no escape in the author, and with every character of the author escaped about 3.3 KiB. Both are under the block, and a header that is not (<see cref="Keep"/> checks)
/// is an act that is not kept and not sent, so a header that does not end within the block is a damaged file and never a long one.</para>
/// </summary>
public sealed class KeptActs
{
    /// <summary>R11.79: at most this many acts are kept for one agent; a 65th is not sent.</summary>
    public const int Bound = 64;

    /// <summary>R11.79: <c>acts</c> reads one block of this many bytes per file and finds line 1 in it (plan ruling 28).</summary>
    public const int HeaderBlockBytes = 4096;

    /// <summary>The act could not be kept on this host, so it was not sent.</summary>
    public const string ActNotKeptType = "curia/client/act-not-kept";

    /// <summary>No act of that digest is kept on this host.</summary>
    public const string NoKeptActType = "curia/client/no-kept-act";

    /// <summary>A kept file's bytes, header, author or Forum do not match what was asked; nothing was sent.</summary>
    public const string KeptActDamagedType = "curia/client/kept-act-damaged";

    /// <summary>The bound is reached; nothing was sent.</summary>
    public const string KeptActsFullType = "curia/client/kept-acts-full";

    private const string Extension = ".act";
    private const int Version = 1;
    private const int LastAnswerCap = 96;

    private readonly ProfileStore _store;
    private readonly string _slug;
    private readonly TimeProvider _clock;
    private readonly AgentProfile? _profile;

    /// <summary>
    /// The one door through which a kept file is opened for a <i>header</i> read: <see cref="List"/> reads a header through it and nothing else does, and the wire is read by a
    /// different function that does not use it, so a header read cannot reach line 2 by the counted path (R11.79, plan rulings 28 and 36). A probe that wraps the stream counts
    /// the bytes read per file. The default opens the file for reading.
    /// </summary>
    internal Func<string, Stream> OpenForRead { get; }

    public KeptActs(ProfileStore store, string slug, TimeProvider? clock = null, AgentProfile? profile = null)
        : this(store, slug, null, clock, profile)
    {
    }

    internal KeptActs(ProfileStore store, string slug, Func<string, Stream>? openForRead, TimeProvider? clock = null, AgentProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        _store = store;
        _slug = slug;
        _clock = clock ?? TimeProvider.System;
        _profile = profile;
        OpenForRead = openForRead ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1));
    }

    /// <summary>The directory the kept files live in: <c>acts</c> beside the profile.</summary>
    public string ActsDirectory => Path.Combine(_store.DirectoryFor(_slug), "acts");

    /// <summary>R11.79's table: which answers remove a kept act and which leave it kept, by send. A status the table does not name is kept.</summary>
    public static ActFate Decide(ActSend send, ActAnswer answer)
    {
        if (answer.Transport == ActTransport.Cancelled || answer.Transport == ActTransport.Unheard) return ActFate.Keep;
        if (answer.Transport == ActTransport.ProvablyNotSent) return send == ActSend.First ? ActFate.Remove : ActFate.Keep;
        if (!answer.BodyReadable) return ActFate.Keep;

        if (answer.Status == 201 || (answer.Status == 409 && answer.ProblemType == AlreadyRecordedType)) return ActFate.Remove;
        if (send == ActSend.Resend) return ActFate.Keep;

        return answer.Status is 400 or 401 or 403 or 404 or 409 or 422 || DefiniteServiceRefusal(answer)
            ? ActFate.Remove
            : ActFate.Keep;
    }

    private const string AlreadyRecordedType = "curia/ingest/already-recorded";

    /// <summary>The stop rule's class of an answer (R11.80): the one place a send is called unanswered or answered.</summary>
    public static SendClass ClassOf(ActAnswer answer) => answer.Transport switch
    {
        ActTransport.Unheard => SendClass.Unanswered,
        ActTransport.Answered => answer.Status >= 500 ? SendClass.Unanswered : SendClass.Answered,
        ActTransport.ProvablyNotSent or ActTransport.Cancelled => SendClass.Neither,
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer.Transport, "Not a transport an answer can have"),
    };

    private static bool DefiniteServiceRefusal(ActAnswer answer) =>
        answer.Status == 503
        && answer.ProblemType is "curia/log/unreadable" or "curia/budget/gate-unavailable" or "curia/append/delta-too-large";

    /// <summary>The profile the author and the Forum are compared with: the agent's own where the session passed it, otherwise the one stored for the slug.</summary>
    private Result<AgentProfile> Profile() => _profile is { } known ? Result<AgentProfile>.Ok(known) : _store.ReadProfile(_slug);

    /// <summary>
    /// Keeps <paramref name="submission"/> before its first byte is sent: creates the file (mode 0600, directory 0700) with the first attempt counted. Refuses, so that nothing is sent,
    /// when the file cannot be written (<see cref="ActNotKeptType"/>) or when this host already holds <see cref="Bound"/> acts (<see cref="KeptActsFullType"/>). A file already held for
    /// the same digest whose bytes are these is the same act and is counted as one more attempt; any other file of that name is not trusted and is not overwritten.
    /// </summary>
    public Result<KeptAct> Keep(SignedSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (!Profile().TryGetValue(out var profile, out var profileError)) return Result<KeptAct>.Fail(NotKept(profileError!.Title));
        if (!Describe(submission.Canonical.Span).TryGetValue(out var described, out var describeError)) return Result<KeptAct>.Fail(NotKept(describeError!.Title));

        var now = _clock.GetUtcNow();
        var header = new KeptActHeader(submission.PrefixedDigest, described!.Kind, described.Board, HeadStore.Origin(profile!.Forum), profile.AgentId, now, 1, now, null);
        var path = PathOf(submission.PrefixedDigest);
        try
        {
            if (File.Exists(path))
            {
                var held = ReadFile(path, submission.PrefixedDigest, profile);
                if (!held.TryGetValue(out var existing, out var heldError)) return Result<KeptAct>.Fail(NotKept("a file of that name is held and cannot be trusted: " + heldError!.Title));
                if (!existing!.Wire.Span.SequenceEqual(submission.Wire.Span)) return Result<KeptAct>.Fail(NotKept("a file of that name holds other bytes"));
                return Result<KeptAct>.Ok(Attempt(existing));
            }

            if (Count() >= Bound)
                return Result<KeptAct>.Fail(new Error(KeptActsFullType, "NOT SENT: this host already keeps the most acts it keeps", Bound.ToString(CultureInfo.InvariantCulture) + " acts are kept and none was answered definitely; `" + PrintedCommand.AsAgent("curia acts", _slug) + "` (in `curia-mcp`, `curia_acts`) lists them; `" + PrintedCommand.AsAgent("curia acts --discard <digest>", _slug) + "` (in `curia-mcp`, `curia_discard`) drops one. Nothing was sent."));

            var text = HeaderLine(header);
            if (Encoding.UTF8.GetByteCount(text) >= HeaderBlockBytes) return Result<KeptAct>.Fail(NotKept("the header is longer than the block a listing reads"));

            PrivateFiles.CreateDirectory(ActsDirectory);
            PrivateFiles.Write(path, text + "\n" + Convert.ToBase64String(submission.Wire.Span) + "\n");
            return Result<KeptAct>.Ok(new KeptAct(header, submission.Wire));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<KeptAct>.Fail(NotKept(ex.GetType().Name));
        }
    }

    private static Error NotKept(string why) =>
        new(ActNotKeptType, "NOT SENT: this host could not keep the act", $"{why}. Nothing was sent.");

    /// <summary>
    /// The kept act of <paramref name="digest"/>: its header, then its bytes, with the digest recomputed from the bytes by the canonicalization the Forum runs and compared with the
    /// digest asked, the file's name and the header, the envelope's author with the agent and the header's Forum with the profile's (R11.79; finding 11: a digest read from a label
    /// would be a check that derives from what it checks). Anything that does not hold is <see cref="KeptActDamagedType"/>; no file is <see cref="NoKeptActType"/>. Nothing is changed.
    /// </summary>
    public Result<KeptAct> Read(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (!IsPublishedForm(digest))
            return Result<KeptAct>.Fail(new Error(NoKeptActType, "NOT SENT: no act of that digest is kept on this host", "the digest is not in the form sha256:<64 lowercase hex>"));

        if (!Profile().TryGetValue(out var profile, out var profileError)) return Result<KeptAct>.Fail(profileError!);

        var path = PathOf(digest);
        if (File.Exists(path)) return ReadFile(path, digest, profile!);

        // A file under another name whose header says this digest is not this act's file: refused, not found (a renamed file is not trusted).
        foreach (var listed in List().Where(l => l.Header is not null && string.Equals(l.Header.Digest, digest, StringComparison.Ordinal)))
            return Result<KeptAct>.Fail(Damaged(listed.Name ?? string.Empty, "the file's name does not name the digest in its header"));

        return Result<KeptAct>.Fail(new Error(NoKeptActType, "NOT SENT: no act of that digest is kept on this host", "`" + PrintedCommand.AsAgent("curia acts", _slug) + "` (in `curia-mcp`, `curia_acts`) lists the acts this host keeps"));
    }

    private static Error Damaged(string file, string why) =>
        new(KeptActDamagedType, "NOT SENT: the kept act cannot be trusted", $"{DisplayLiteral.Of(file)}: {why}. Nothing was sent, and the file is untouched.");

    private static Result<KeptAct> ReadFile(string path, string digest, AgentProfile profile)
    {
        var name = Path.GetFileName(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<KeptAct>.Fail(Damaged(name, "it could not be read (" + ex.GetType().Name + ")"));
        }

        var newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0) return Result<KeptAct>.Fail(Damaged(name, "it has no header line"));
        if (!ParseHeader(bytes.AsSpan(0, newline)).TryGetValue(out var header, out var headerError)) return Result<KeptAct>.Fail(Damaged(name, headerError!.Title));

        if (!string.Equals(header!.Digest, digest, StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "the digest in its header is not the digest asked"));
        if (!string.Equals(name, DigestHex(digest) + Extension, StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "its name does not name the digest asked"));
        if (!string.Equals(header.Author, profile.AgentId, StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "its header names another author"));
        if (!string.Equals(header.Forum, HeadStore.Origin(profile.Forum), StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "its header names another Forum"));

        var line = Encoding.ASCII.GetString(bytes, newline + 1, bytes.Length - newline - 1).Trim();
        var wire = new byte[(line.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(line, wire, out var written)) return Result<KeptAct>.Fail(Damaged(name, "its bytes are not base64"));
        wire = wire[..written];

        if (!Recompute(wire, profile.AgentId).TryGetValue(out var recomputed, out var wireError)) return Result<KeptAct>.Fail(Damaged(name, wireError!.Title));
        if (!string.Equals(recomputed!.Digest, digest, StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "the digest recomputed from its bytes is not the digest asked"));

        // Errata G59 (item 123): the header's kind and board are what a listing prints and a resend is asked to send; they are compared with the envelope the bytes hold, and the field is named (never its value).
        if (!string.Equals(header.Kind, recomputed.Kind, StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "its header names another kind than its bytes hold"));
        if (!string.Equals(header.Board, recomputed.Board, StringComparison.Ordinal)) return Result<KeptAct>.Fail(Damaged(name, "its header names another board than its bytes hold"));

        return Result<KeptAct>.Ok(new KeptAct(header, wire));
    }

    /// <summary>What the bytes of a kept act hold: the digest by the canonicalization the Forum runs, and the envelope's kind and board.</summary>
    private sealed record Recomputed(string Digest, string Kind, string Board);

    /// <summary>The digest, kind and board of the envelope in <paramref name="wire"/>, once the envelope's author is <paramref name="author"/>.</summary>
    private static Result<Recomputed> Recompute(byte[] wire, string author)
    {
        if (!JsonReader.Parse(wire, ClientJson.Limits).TryGetValue(out var parsed, out _) || parsed is not JsonValue.Object submission)
            return Result<Recomputed>.Fail(new Error("curia/client/kept-act-damaged", "its bytes are not a submission"));

        if (ClientJson.Object(submission, "envelope") is not { } envelope || ClientJson.String(envelope, "author") is not { } envelopeAuthor)
            return Result<Recomputed>.Fail(new Error("curia/client/kept-act-damaged", "its bytes carry no envelope"));
        if (!string.Equals(envelopeAuthor, author, StringComparison.Ordinal))
            return Result<Recomputed>.Fail(new Error("curia/client/kept-act-damaged", "its envelope names another author"));
        if (ClientJson.String(envelope, "kind") is not { } kind || ClientJson.String(envelope, "board") is not { } board)
            return Result<Recomputed>.Fail(new Error("curia/client/kept-act-damaged", "its envelope carries no kind or no board"));

        return CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _)
            ? Result<Recomputed>.Ok(new Recomputed(Digests.Sha256(canonical).ToPrefixed(), kind, board))
            : Result<Recomputed>.Fail(new Error("curia/client/kept-act-damaged", "its envelope cannot be canonicalized"));
    }

    /// <summary>Whether this host holds a file for <paramref name="act"/> whose digest and bytes are the act's: a resend sends what is kept and nothing a caller made up.</summary>
    internal bool Holds(KeptAct act)
    {
        var path = PathOf(act.Header.Digest);
        try
        {
            if (!File.Exists(path)) return false;
            var lines = File.ReadAllLines(path);
            return lines.Length >= 2
                && ParseHeader(Encoding.UTF8.GetBytes(lines[0])).TryGetValue(out var header, out _)
                && string.Equals(header!.Digest, act.Header.Digest, StringComparison.Ordinal)
                && Convert.ToBase64String(act.Wire.Span) == lines[1].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>One more attempt: <c>attempts</c> and <c>last_attempt_at</c> are stamped before each send the agent asked for. Persisted best effort: the count is advice and a file that cannot be rewritten still resends.</summary>
    public KeptAct Attempt(KeptAct act)
    {
        ArgumentNullException.ThrowIfNull(act);
        var next = act with { Header = act.Header with { Attempts = act.Header.Attempts + 1, LastAttemptAt = _clock.GetUtcNow() } };
        Rewrite(next);
        return next;
    }

    /// <summary>
    /// The one function that decides, from an answer, whether the act is removed or kept (<see cref="Decide"/>), and does it: a kept act records the answer's type or status in its header
    /// (never text the Forum wrote), a removed one is deleted. A file that cannot be removed stays and resends to <c>already-recorded</c>.
    /// </summary>
    public Settled Settle(KeptAct act, ActSend send, ActAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(act);
        var fate = Decide(send, answer);
        if (fate == ActFate.Remove)
        {
            try { File.Delete(PathOf(act.Header.Digest)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the file stays: a false keep costs a resend that is answered already-recorded */ }
            return new Settled(fate, 0);
        }

        // R11.80: `unanswered` is the run of sends, ending with this one, that the Forum did not answer: one more for an unanswered send, none for an answered one, and unchanged for a send that
        // never reached the Forum. It is settled here, after the answer is known; a client killed mid-send leaves it unchanged, which only delays the stop sentence.
        var unanswered = ClassOf(answer) switch
        {
            SendClass.Unanswered => act.Header.Unanswered + 1,
            SendClass.Answered => 0,
            SendClass.Neither => act.Header.Unanswered,
            _ => throw new ArgumentOutOfRangeException(nameof(answer), answer.Transport, "Not a class of send"),
        };
        Rewrite(act with { Header = act.Header with { LastAnswer = AnswerWord(answer), Unanswered = unanswered } });
        return new Settled(fate, unanswered);
    }

    private static string AnswerWord(ActAnswer answer)
    {
        if (answer.Transport == ActTransport.Unheard) return "unheard";
        if (answer.Transport == ActTransport.Cancelled) return "cancelled";
        if (answer.Transport == ActTransport.ProvablyNotSent) return "not sent";

        return answer.ProblemType is { Length: > 0 } type && IsSafeWord(type) ? type : answer.Status.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSafeWord(string value) =>
        value.Length <= LastAnswerCap && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');

    private void Rewrite(KeptAct act)
    {
        try
        {
            var path = PathOf(act.Header.Digest);
            if (!File.Exists(path)) return;
            var text = HeaderLine(act.Header);
            if (Encoding.UTF8.GetByteCount(text) >= HeaderBlockBytes) return;
            PrivateFiles.Write(path, text + "\n" + Convert.ToBase64String(act.Wire.Span) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Advisory members only: the bytes and the digest are unchanged, so the act still resends.
        }
    }

    /// <summary>
    /// Every kept act's header, read from line 1 alone through <see cref="OpenForRead"/>: one block of <see cref="HeaderBlockBytes"/> bytes per file, nothing of line 2 decoded. A file whose
    /// first line does not end within the block, or is not a header, is listed as damaged by its name.
    /// </summary>
    public IReadOnlyList<KeptActListing> List()
    {
        if (!Directory.Exists(ActsDirectory)) return [];

        var listed = new List<KeptActListing>();
        foreach (var path in Directory.EnumerateFiles(ActsDirectory, "*" + Extension).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            try
            {
                var header = ReadHeaderBlock(path);
                listed.Add(header is null ? new KeptActListing(null, name) : new KeptActListing(header, null, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listed.Add(new KeptActListing(null, name));
            }
        }

        return listed;
    }

    private KeptActHeader? ReadHeaderBlock(string path)
    {
        var block = new byte[HeaderBlockBytes];
        int read;
        using (var stream = OpenForRead(path))
        {
            read = stream.Read(block, 0, block.Length);
        }

        var newline = Array.IndexOf(block, (byte)'\n', 0, read);
        return newline < 0 || !ParseHeader(block.AsSpan(0, newline)).TryGetValue(out var header, out _) ? null : header;
    }

    /// <summary>
    /// Removes the kept act of exactly <paramref name="digest"/> and no other (never by prefix, never more than one), and never throws (plan ruling 40, the review's M3b): the outcome says
    /// whether the file was removed, none is held, or the file is held and could not be removed. Whether the act was recorded is not said here and not known here.
    /// </summary>
    public DiscardOutcome Discard(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (!IsPublishedForm(digest)) return new DiscardOutcome(DiscardStatus.NotHeld);

        var path = PathOf(digest);
        try
        {
            if (!File.Exists(path)) return new DiscardOutcome(DiscardStatus.NotHeld);

            File.Delete(path);
            return new DiscardOutcome(DiscardStatus.Removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiscardOutcome(DiscardStatus.Failed, ex.GetType().Name);
        }
    }

    private int Count() => Directory.Exists(ActsDirectory) ? Directory.EnumerateFiles(ActsDirectory, "*" + Extension).Count() : 0;

    private string PathOf(string digest) => Path.Combine(ActsDirectory, DigestHex(digest) + Extension);

    private static string DigestHex(string digest) => digest["sha256:".Length..];

    /// <summary>The published form of a digest: <c>sha256:</c> and 64 lowercase hex digits.</summary>
    public static bool IsPublishedForm(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return digest.Length == "sha256:".Length + 64
            && digest.StartsWith("sha256:", StringComparison.Ordinal)
            && digest.AsSpan("sha256:".Length).IndexOfAnyExcept(LowerHex) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> LowerHex = System.Buffers.SearchValues.Create("0123456789abcdef");

    private sealed record Described(string Kind, string Board);

    private static Result<Described> Describe(ReadOnlySpan<byte> canonical)
    {
        if (!JsonReader.Parse(canonical.ToArray(), ClientJson.Limits).TryGetValue(out var parsed, out _) || parsed is not JsonValue.Object envelope
            || ClientJson.String(envelope, "kind") is not { } kind || ClientJson.String(envelope, "board") is not { } board)
            return Result<Described>.Fail(new Error("curia/client/act-not-kept", "the act's kind and board could not be read"));

        return Result<Described>.Ok(new Described(kind, board));
    }

    private static string HeaderLine(KeptActHeader header)
    {
        JsonValue Text(string? value) => value is null ? JsonValue.Null.Instance : new JsonValue.String(value);
        string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        return ClientJson.Render(
        [
            new("v", new JsonValue.Number(Version)),
            new("digest", Text(header.Digest)),
            new("kind", Text(header.Kind)),
            new("board", Text(header.Board)),
            new("forum", Text(header.Forum)),
            new("author", Text(header.Author)),
            new("kept_at", Text(Iso(header.KeptAt))),
            new("attempts", new JsonValue.Number(header.Attempts)),
            new("last_attempt_at", Text(header.LastAttemptAt is { } at ? Iso(at) : null)),
            new("last_answer", Text(header.LastAnswer)),
            new("unanswered", new JsonValue.Number(header.Unanswered)),
        ]);
    }

    private static Result<KeptActHeader> ParseHeader(ReadOnlySpan<byte> line)
    {
        Result<KeptActHeader> Bad(string why) => Result<KeptActHeader>.Fail(new Error(KeptActDamagedType, why));

        if (!JsonReader.Parse(line.ToArray(), ClientJson.Limits).TryGetValue(out var parsed, out _) || parsed is not JsonValue.Object o) return Bad("its header is not a JSON object");
        if (ClientJson.WholeNumber(o, "v") != Version) return Bad("its header is of a form this client does not read");

        if (ClientJson.String(o, "digest") is not { } digest || !IsPublishedForm(digest)) return Bad("its header names no digest");
        if (ClientJson.String(o, "kind") is not { } kind || ClientJson.String(o, "board") is not { } board) return Bad("its header names no kind or board");
        if (ClientJson.String(o, "forum") is not { } forum || ClientJson.String(o, "author") is not { } author) return Bad("its header names no Forum or author");
        if (!Instant(ClientJson.String(o, "kept_at")).TryGetValue(out var keptAt, out _)) return Bad("its header has no readable kept_at");
        if (ClientJson.WholeNumber(o, "attempts") is not (>= 0 and <= int.MaxValue and var attempts)) return Bad("its header has no readable attempts");

        DateTimeOffset? lastAttempt = null;
        if (ClientJson.String(o, "last_attempt_at") is { } last)
        {
            if (!Instant(last).TryGetValue(out var parsedLast, out _)) return Bad("its header has no readable last_attempt_at");
            lastAttempt = parsedLast;
        }

        // A header with no `unanswered` reads as 0 (no stored file outlives alpha); a member that is not a whole number in range is a header this client does not read.
        var unanswered = 0L;
        if (ClientJson.Member(o, "unanswered") is not null)
        {
            if (ClientJson.WholeNumber(o, "unanswered") is not (>= 0 and <= int.MaxValue and var counted)) return Bad("its header has no readable unanswered");
            unanswered = counted;
        }

        return Result<KeptActHeader>.Ok(new KeptActHeader(digest, kind, board, forum, author, keptAt, (int)attempts, lastAttempt, ClientJson.String(o, "last_answer"), (int)unanswered));
    }

    private static Result<DateTimeOffset> Instant(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? Result<DateTimeOffset>.Ok(at)
            : Result<DateTimeOffset>.Fail(new Error("curia/client/kept-act-damaged", "an instant is not readable"));
}
