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

    private const string StoreUnavailableSentence = FaultOfTheForum +
        "If the request was a write it may or may not have been recorded: do not run the verb again to find out, because ask, answer and comment each sign a new act and the Forum can store it as a second post; " +
        "for a post, ask the Forum's recheck about the digest of the act sent, which is printed with this refusal, and send it again only if the Forum does not know it; " +
        "a repeat of a resolve or of a flag is safe, because the Forum answers that the acceptance already stands or refuses the flag as already raised";

    private const string TokenSentence = FaultOfTheForum + "Nothing was minted: a new request can be sent later";

    // Errata G53's published words, declared and not yet used (the seam): the build replaces the three sentences above with them.

    /// <summary>R10.93 wait, R11.73 words: the figure of the wait, in seconds, before an act a refusal printed may be sent again.</summary>
    public const int RecheckWaitSeconds = 60;

    /// <summary>R11.73 words: the phrase a replayed resolve prints, which means the first call was recorded.</summary>
    public const string ResolveReplayPhrase = "already the accepted answer; nothing was recorded";

    /// <summary>R11.73 words: the phrase the refusal of a repeated flag prints, which means the first call was recorded.</summary>
    public const string FlagRepeatPhrase = "has already raised a flag of this type against this post";

    /// <summary>R11.74 words: the <c>whoami</c> line for a cached token that has expired.</summary>
    public const string WhoamiExpiredLine = "cached, expired; a new one is requested at the next call";

    /// <summary>
    /// The sentence a refusal of <paramref name="type"/> carries after its title and type, or the empty string for a type that is not one of the three. The
    /// token's is chosen by the code <c>temporarily_unavailable</c>, which the token endpoint's answer carries as its type.
    /// </summary>
    public static string Sentence(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            LogUnreadableType => LogUnreadableSentence,
            StoreUnavailableType => StoreUnavailableSentence,
            TemporarilyUnavailableCode => TokenSentence,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The sentence about repeating a write that the write tools' descriptions and the writing verbs' helps carry: the <c>curia/store/unavailable</c> sentence,
    /// the one that says not to run the verb again to find out and where to ask instead (R11.72).
    /// </summary>
    public const string RepeatSentence = StoreUnavailableSentence;

    /// <summary>
    /// What the six write tools' descriptions and the ten writing verbs' helps say of the two <c>503</c> types, in the published words: the one that says a
    /// write may or may not have been recorded (<see cref="RepeatSentence"/>) and the one that says nothing was recorded.
    /// </summary>
    public const string WriteSentence =
        "A 503 " + StoreUnavailableType + " means " + StoreUnavailableSentence + ". A 503 " + LogUnreadableType + " means " + LogUnreadableSentence + ". " +
        "Neither is retried for you.";

    /// <summary>
    /// R11.72's act words (ruling 22): the line printed after a refusal of <see cref="StoreUnavailableType"/> to a signed post, with the digest of the act
    /// the client sent. The digest is the act's <c>sha256:</c> and 64 lowercase hex digits, the spelling <c>recheck</c> takes, computed here from the canonical
    /// bytes signed, never read from the Forum's refusal. The line holds no state and calls nothing (R11.64): it says where to ask.
    /// </summary>
    public static string ActLine(string prefixedDigest)
    {
        ArgumentNullException.ThrowIfNull(prefixedDigest);
        return "The act sent: digest " + prefixedDigest + " (computed here from the bytes signed); the Forum's recheck of it says whether the act was recorded.";
    }
}
