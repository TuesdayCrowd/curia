using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R11.93 (errata G65): what the vector model a Forum served can and cannot do, from the model id the Forum served and from one definition. The banner above a result set
/// takes it, so a reader is told the limits of the model that ranked the page and not the limits of a model the client's author had in mind.
///
/// <para>A client knows only the ids it was written with. An id it does not know prints the id, as a display literal (R10.64: the id is a value the Forum served, and a function that
/// returns the client's own words quotes its own holes), and says its limits are not stated; it never borrows a known model's sentence, because a word that claims what a model cannot
/// give is worse than no word (trap 185: a positive word with no negative neighbour). A Forum that served no id, or an empty one, is told apart from one that served an id.</para>
/// </summary>
public static class ModelWording
{
    private const string HashedId = "hashed-ngram@1";
    private const string BgeId = "bge-small-en-v1.5@1";

    /// <summary>The sentence for <paramref name="modelId"/> (null or empty when the Forum served none). For an id it does not know: the id as a display literal and that its limits are not stated.</summary>
    public static string Sentence(string? modelId) => modelId switch
    {
        HashedId =>
            "The vector channel's model is hashed-ngram@1: it matches shared words and spellings, and it does not find paraphrase, so a post that says the same thing in other words is found by its terms or not at all.",
        BgeId =>
            "The vector channel's model is bge-small-en-v1.5@1: a learned model that reads at most 512 tokens of its input (two are the model's own markers, and a query carries a fixed instruction as well), so a long post is found by its opening and not by its end; it can place a paraphrase near the post it paraphrases, and it can miss one.",
        null or "" =>
            "The vector channel's model was not stated by the Forum: limits NOT STATED.",
        _ =>
            $"The vector channel's model is {DisplayLiteral.Of(modelId)}: limits NOT STATED (this client has no word on what that model can and cannot find).",
    };
}
