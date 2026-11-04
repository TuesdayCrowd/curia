namespace Curia.Client;

/// <summary>
/// R11.72 (errata G52): the one definition of how <c>curia</c> and <c>curia-mcp</c> word a fault of the Forum's own store. The seam declares it: the three
/// problem types and the token's OAuth code are the identifiers the Forum issues, and the sentences are the words <c>R11.72 words</c> publishes. Both
/// clients print them through <c>Refusal.Summary</c>, so neither can word a type alone.
/// </summary>
public static class StoreWording
{
    /// <summary>A read of the event log failed; nothing was recorded.</summary>
    public const string LogUnreadableType = "curia/log/unreadable";

    /// <summary>Any other store failed, the append included; whether a write was recorded is not known.</summary>
    public const string StoreUnavailableType = "curia/store/unavailable";

    /// <summary>An exception nothing names; a problem document and not a body-less answer.</summary>
    public const string ServerFaultType = "curia/server/fault";

    /// <summary>RFC 6749's code the token endpoint answers a fault of a store with.</summary>
    public const string TemporarilyUnavailableCode = "temporarily_unavailable";

    private const string FaultOfTheForum = "a fault of the Forum, not of the request. ";

    private const string LogUnreadableSentence = FaultOfTheForum + "Nothing was recorded: the request can be sent again later";

    // Errata G58's published words (R11.80 clause words, act words, kind words, unheard words; R11.81). The sixty-second rule of G53 and G55 is withdrawn.

    /// <summary>R11.73 words: the phrase a replayed resolve prints, which means the first call was recorded.</summary>
    public const string ResolveReplayPhrase = "already the accepted answer; nothing was recorded";

    /// <summary>R11.73 words: the phrase the refusal of a repeated flag prints, which means the first call was recorded.</summary>
    public const string FlagRepeatPhrase = "has already raised a flag of this type against this post";

    /// <summary>R11.80 clause words (post): the sentence for <see cref="StoreUnavailableType"/> when the refusal's act is a signed post or vote, or a resend of one.</summary>
    private const string PostClause = FaultOfTheForum + "The Forum has not confirmed that the act was recorded and has not refused it";

    /// <summary>R11.80 clause words (resolve): names no digest, and carries the reading pair of a repeated resolve.</summary>
    private const string ResolveClause = FaultOfTheForum +
        "Whether this resolve was recorded is not known. Running it again is safe, and the answer says what happened the first time: \"" + ResolveReplayPhrase +
        "\" means the first call was recorded, and any other success means the first call was not recorded and this one has recorded it. " +
        "This reading holds only if no other call changed the thread's accepted answer between the two calls";

    /// <summary>R11.80 clause words (flag): names no digest, and carries the reading pair of a repeated flag.</summary>
    private const string FlagClause = FaultOfTheForum +
        "Whether this flag was recorded is not known. Raising it again is safe: a refusal \"" + FlagRepeatPhrase +
        "\" means the first call was recorded, and an accepted flag means the first call was not recorded and this one has recorded it";

    /// <summary>R11.80 clause words (other): a request that is none of the above; read what it would have changed before sending it again.</summary>
    private const string OtherClause = FaultOfTheForum + "If the request was a write it may or may not have been recorded: read what the request would have changed before sending it again";

    /// <summary>The clause for <paramref name="act"/> (R11.80): chosen by the act the refusal refused and by nothing else.</summary>
    public static string Clause(ActContext act) => act switch
    {
        ActContext.Post or ActContext.Resend => PostClause,
        ActContext.Resolve => ResolveClause,
        ActContext.Flag => FlagClause,
        ActContext.Other => OtherClause,
        _ => throw new ArgumentOutOfRangeException(nameof(act), act, "Not an act a refusal can refuse"),
    };

    /// <summary>
    /// R11.74 words: the sentence for the token's refusal. No call is a token request, so it says what is true of every call that prints it: the Forum could not
    /// issue this session's token and the call was refused before anything was sent to be recorded. It does not say that nothing was read, because a verb that reads
    /// a parent first has read it.
    /// </summary>
    private const string TokenSentence = FaultOfTheForum + "The Forum could not issue this session's token, so the call was refused before anything was sent to be recorded: the call can be sent again later";

    /// <summary>R11.74 words: the <c>whoami</c> line for a cached token that has expired.</summary>
    public const string WhoamiExpiredLine = "cached, expired; a new one is requested at the next call";

    /// <summary>
    /// The sentence a refusal of <paramref name="type"/> carries after its title and type, or the empty string for a type that is not one of the three: for
    /// <see cref="StoreUnavailableType"/> the clause of the act refused (<paramref name="act"/>, R11.80). The token's is chosen by the code <c>temporarily_unavailable</c>,
    /// which the token endpoint's answer carries as its type.
    /// </summary>
    public static string Sentence(string type, ActContext act = ActContext.Other)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            LogUnreadableType => LogUnreadableSentence,
            StoreUnavailableType => Clause(act),
            TemporarilyUnavailableCode => TokenSentence,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// R11.80 act words: the line printed after a refusal of <see cref="StoreUnavailableType"/> to a signed post or vote, with the digest of the act the client signed (computed here
    /// from the canonical bytes, never read from the Forum's refusal), and for a <c>revision</c> and an <c>endorse</c> the kind words. It holds no state, calls nothing (R11.64) and
    /// carries no wait: it says that the act is kept on this host, names the resend as the one action and the recheck as an optional question.
    /// </summary>
    public static string ActLine(string prefixedDigest, string? kind = null, string? agent = null)
    {
        ArgumentNullException.ThrowIfNull(prefixedDigest);
        return "The act sent: digest " + prefixedDigest + " (computed here from the bytes signed), kept on this host. Send exactly this act again with " + PrintedCommand.Span("curia resend " + prefixedDigest, agent) + " (in `curia-mcp`, `curia_resend`): " +
            "the Forum records one act once, so if the first call was recorded the resend is answered \"already recorded\" and names the post, and if it was not the resend records it. " +
            "Do not run the verb again: that signs a new act, and the Forum can store a new act as a second post. " +
            "To know first, `curia recheck " + prefixedDigest + "` (`curia_recheck`) says whether the act was recorded as of the moment it is asked; it cannot say the act will not be recorded after it, so \"unknown\" is not a reason to wait." +
            (kind is "revision" or "vote" ? KindWords : string.Empty);
    }

    /// <summary>R11.80 kind words: appended to the act line for a <c>revision</c> and for an <c>endorse</c> only.</summary>
    public const string KindWords = " A repeat of this verb would be refused rather than stored twice (the Forum serves one revision of a post and keeps one vote per target), but the resend is exact for every kind.";

    /// <summary>
    /// R11.80 unheard words: the sentence printed after the client's own refusal for an unheard signed post, directly above <see cref="ActLine"/>. It says the request was sent and no
    /// answer was read, and that a Forum still working on it can record the act after this client gave up. No wait is in it.
    /// </summary>
    public const string UnheardSentence = "The Forum was not heard from: the request was sent and no answer was read, so the act sent may or may not have been recorded, and a Forum still working on it can record it after this client gave up.";

    /// <summary>
    /// The passage the helps of the verbs that sign a post or a vote and the descriptions of the tools that do carry for the act line (R11.72 (rev.), R11.80): what a <c>503</c> is, what
    /// follows it and the one action, and what is printed when the request is sent and no answer is read. It carries no wait in seconds.
    /// </summary>
    public static readonly string PostPassage =
        "A 503 " + StoreUnavailableType + " means " + PostClause + ". It is followed by the act line, which names the digest of the act computed here, says the act is kept on this host and names the one action: " +
        "the resend, resend <digest> (in curia-mcp, curia_resend), sends exactly the act kept, and the Forum records one act once. Do not run the verb again: that signs a new act, and the Forum can store it as a second post. " +
        "If the request is sent and no answer is read (a timeout, or a connection that ends after the request was sent), the refusal is titled \"No answer was read from the Forum\" and is followed by this sentence and then the act line: " + UnheardSentence + " " +
        ResendWording.StopPassage;

    /// <summary>The passage of a resolve's help and description (R11.80): its own clause, which names no digest.</summary>
    public static readonly string ResolvePassage = "A 503 " + StoreUnavailableType + " means " + ResolveClause + ".";

    /// <summary>The passage of a flag's help and description (R11.80): its own clause, which names no digest.</summary>
    public static readonly string FlagPassage = "A 503 " + StoreUnavailableType + " means " + FlagClause + ".";

    /// <summary>
    /// What the help of a verb and the description of a tool carries for a refusal of its own act (R11.72 (rev.), R11.80): the passage of that act, then the sentence for the other 503.
    /// A signed post or vote's names the act line and the resend; a resolve's and a flag's name no digest.
    /// </summary>
    public static string PassageOf(ActContext act) => act switch
    {
        ActContext.Post or ActContext.Resend => PostPassage + " " + LogUnreadablePassage,
        ActContext.Resolve => ResolvePassage + " " + LogUnreadablePassage,
        ActContext.Flag => FlagPassage + " " + LogUnreadablePassage,
        ActContext.Other => "A 503 " + StoreUnavailableType + " means " + OtherClause + ". " + LogUnreadablePassage,
        _ => throw new ArgumentOutOfRangeException(nameof(act), act, "Not an act a refusal can refuse"),
    };

    /// <summary>The sentence every write's help and description carries for the other 503 and for retries (R11.72, R11.64).</summary>
    public static readonly string LogUnreadablePassage = "A 503 " + LogUnreadableType + " means " + LogUnreadableSentence + ". Neither is retried for you.";

    /// <summary>
    /// What follows a refusal of a signed post, given the digest of the act the client signed (null where no signed post's digest is held: a read, a resolve, a flag):
    /// the act line for <see cref="StoreUnavailableType"/> and for any refusal that leaves the act kept (R11.80, plan ruling 43); the unheard sentence and then the act line for an unheard refusal (R11.77); nothing otherwise. The one
    /// definition both printers call, so neither compares a problem type itself and neither can add a line the other does not. It calls nothing and waits for nothing (R11.64).
    /// </summary>
    public static IReadOnlyList<string> AfterRefusal(Refusal refusal, string? actDigest)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        if (actDigest is null) return [];

        // A resend's refusal carries its own words (R11.81 (a), composed by Refusal.Summary: the unheard sentence included) and no act line, which is a first send's. What follows is
        // the stop sentence or the definite words (R11.80, plan rulings 13 and 46 to 49): advice that is never enforced.
        var lines = new List<string>();
        if (refusal.Act != ActContext.Resend)
        {
            // Plan ruling 43 (R11.80): the act line follows every refusal of a signed post that leaves the act kept (the session sets KeptAttempts only then), whatever the status and whether or not the Forum
            // named a problem type: the file is kept, the slot is held, and the agent is told the digest. `curia/store/unavailable` is the same condition and stays named.
            if (refusal.Unheard) lines.AddRange([UnheardSentence, ActLine(actDigest, refusal.ActKind, refusal.Agent)]);
            else if (string.Equals(refusal.Error.Type, StoreUnavailableType, StringComparison.Ordinal) || refusal.KeptAttempts >= 1) lines.Add(ActLine(actDigest, refusal.ActKind, refusal.Agent));
        }

        // R11.80 (plan rulings 46 to 49): the stop sentence counts only sends the Forum did not answer, so it follows a refusal that is itself unanswered, from the second in a row and nowhere else; a
        // resend the Forum answered with a refusal (a 4xx) is followed by the definite words in its place, once. A first send's refusal gets neither definite words nor, unless it is the second in a
        // row, the stop sentence.
        if (refusal.KeptSend == SendClass.Unanswered && refusal.KeptUnanswered >= ResendWording.StopFromUnanswered) lines.Add(ResendWording.Stop(refusal.KeptUnanswered, refusal.KeptAttempts, actDigest, refusal.Agent));
        else if (refusal.Act == ActContext.Resend && refusal.KeptSend == SendClass.Answered && refusal.Status is >= 400 and <= 499) lines.Add(ResendWording.DefiniteWords);
        return lines;
    }
}
