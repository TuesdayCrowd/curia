namespace Curia.Client;

/// <summary>
/// R11.68 (errata G50): the sentence <c>curia_verify</c>'s description and <c>curia help verify</c> share about verifications sent in a batch, in one place so that
/// neither words it alone. It is G48's non-property (j) made advice: the retained-head line a verification prints is true of its own call, and a host that wants
/// one verdict reads first, waits for the answer and pins <c>expectedDigest</c>.
/// </summary>
public static class VerifyWording
{
    /// <summary>The three clauses of R11.68, in its order.</summary>
    public const string BatchSentence =
        "Note that verifications sent together are each true of their own call and may differ in what they say of the retained head "
        + "by which call stored it first, a verification sent in the same batch as the read it is about is fetched afresh, and a host that wants one verdict "
        + "reads first, waits for the answer and pins expectedDigest.";
}
