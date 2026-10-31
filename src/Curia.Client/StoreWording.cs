namespace Curia.Client;

/// <summary>
/// R11.72 (errata G52): the one definition of how <c>curia</c> and <c>curia-mcp</c> word a fault of the Forum's own store. The seam declares it: the three
/// problem types and the token's OAuth code are the identifiers the Forum issues, and the sentences are not yet worded (Task 4 gives them the words
/// <c>R11.72 words</c> publishes), so nothing here changes what a client prints.
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

    /// <summary>The sentence a refusal of <paramref name="type"/> carries after its title and type. Inert in the seam: empty.</summary>
    public static string Sentence(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return string.Empty;
    }

    /// <summary>The sentence about repeating a write that the write tools' descriptions and the writing verbs' helps carry. Inert in the seam: empty.</summary>
    public static string RepeatSentence => string.Empty;
}
