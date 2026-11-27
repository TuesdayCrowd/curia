namespace Curia.Client;

/// <summary>
/// Errata G84 (R11.111): the words a client says for the Forum's <c>413</c> of the type it issues for a request larger than a route reads (R6.68 (rev.), and a key act's own 32,768 bytes), in one place so that
/// <c>curia</c>, <c>curia-mcp</c> and the kept-act table cannot disagree about it. The problem type is the Forum's; the sentence is published verbatim in R11.111 and read from the errata by the probes.
/// It names no figure and no requirement, because a key act's route answers the same type at its own bound, and a figure typed here would be a second copy of a number the Forum owns.
/// </summary>
public static class RequestBoundWording
{
    /// <summary>The problem type of a request larger than its route reads: what the classifier, the kept-act table and the words all decide on, never the status alone.</summary>
    public const string TooLargeType = "curia/request/too-large";

    /// <summary>R11.111's sentence. It says nothing was recorded (once: R11.81 (c)'s count), that sending the same again is refused the same way, what to do, and what the refusal is not.</summary>
    public const string Sentence =
        "The Forum reads no more than a fixed size of request on this route, and this one is larger. Nothing was recorded, and sending it again will be refused the same way: send a shorter one. " +
        "This is neither a fault of the Forum, a tier denial nor a spent budget.";
}
