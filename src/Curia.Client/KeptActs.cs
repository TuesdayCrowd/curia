using System.Diagnostics.CodeAnalysis;
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
    string? LastAnswer);

/// <summary>A kept act: its header and the exact signed bytes.</summary>
public sealed record KeptAct(KeptActHeader Header, ReadOnlyMemory<byte> Wire);

/// <summary>One line of the listing: a header, or the name of a file whose header could not be read.</summary>
public sealed record KeptActListing(KeptActHeader? Header, string? DamagedFile);

/// <summary>
/// R11.79 (errata G58): the signed bytes of every post and vote this host sent, kept in the agent's own directory from before the first byte is sent until the Forum answers
/// definitely, and sent again exactly when asked. <b>The seam:</b> the types, the names and the answer table exist; every method that would touch the disk is a stub that keeps nothing,
/// so a probe of the keeping is red by assertion and not by a missing symbol. The answer table (<see cref="Decide"/>) is the one definition of which answers leave an act unknown.
/// </summary>
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Seam stubs: each method is an instance method over the agent's own directory in the stage that builds the kept act (R11.79).")]
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

    private readonly ProfileStore _store;
    private readonly string _slug;

    /// <summary>
    /// The one door through which a kept file is opened for reading: <see cref="List"/> reads a header through it and nothing else does, so a probe that wraps the stream counts
    /// the bytes read per file (R11.79, plan ruling 28). The default opens the file for reading.
    /// </summary>
    internal Func<string, Stream> OpenForRead { get; }

    public KeptActs(ProfileStore store, string slug)
        : this(store, slug, null)
    {
    }

    internal KeptActs(ProfileStore store, string slug, Func<string, Stream>? openForRead)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        _store = store;
        _slug = slug;
        OpenForRead = openForRead ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
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

    private static bool DefiniteServiceRefusal(ActAnswer answer) =>
        answer.Status == 503
        && answer.ProblemType is "curia/log/unreadable" or "curia/budget/gate-unavailable" or "curia/append/delta-too-large";

    /// <summary>Seam stub: keeps nothing. R11.79's write-ahead file is built in the stage after the seam.</summary>
    public Result<KeptAct> Keep(SignedSubmission submission, AgentProfile profile, string kind, string board, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(profile);
        var header = new KeptActHeader(submission.PrefixedDigest, kind, board, profile.Forum.ToString(), profile.AgentId, now, 0, null, null);
        return Result<KeptAct>.Ok(new KeptAct(header, submission.Wire));
    }

    /// <summary>Seam stub: no act is kept, so none is found.</summary>
    public Result<KeptAct> Read(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return Result<KeptAct>.Fail(new Error(NoKeptActType, "No act of that digest is kept on this host", digest));
    }

    /// <summary>Seam stub: counts nothing.</summary>
    public KeptAct Attempt(KeptAct act, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(act);
        return act;
    }

    /// <summary>Seam stub: the table's verdict is returned and nothing is removed, because nothing was kept.</summary>
    public ActFate Settle(KeptAct act, ActSend send, ActAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(act);
        return Decide(send, answer);
    }

    /// <summary>Seam stub: nothing is held.</summary>
    public IReadOnlyList<KeptActListing> List() => [];

    /// <summary>Seam stub: nothing is held, so nothing is discarded.</summary>
    public bool Discard(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return false;
    }
}
