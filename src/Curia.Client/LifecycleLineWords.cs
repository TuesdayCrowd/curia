using System.Globalization;

namespace Curia.Client;

/// <summary>
/// R11.95's three sentences (errata G68), the words a verification says when the key line follows the lifecycle line and when the Forum's mark names a declaration the client's head does not cover. They are
/// in this class and the errata's <c>R11.95 words</c> paragraph and nowhere else: <c>PublishedClientWords</c> parses the paragraph and the tests hold this class to it. A <c>kid</c> is a value the log
/// holds and is written as a display literal (R10.64), as every value a check names is.
/// </summary>
public static class LifecycleLineWords
{
    /// <summary>The key line for a rotation's binding when the lifecycle line failed.</summary>
    public static string KeyLineRotationLifecycleFailed(long leaf, string? kid) =>
        string.Create(CultureInfo.InvariantCulture,
            $"the binding at leaf {leaf} is a rotation, and the lifecycle line shows that the author's lifecycle does not verify, so the log does not establish that the author bound kid={Check.Quote(kid)}");

    /// <summary>The key line for a rotation's binding when the lifecycle line could not be checked.</summary>
    public static string KeyLineRotationLifecycleCouldNot(long leaf, string? kid) =>
        string.Create(CultureInfo.InvariantCulture,
            $"the binding at leaf {leaf} is a rotation, which the lifecycle line has not established, so this check cannot say that the author bound kid={Check.Quote(kid)}");

    /// <summary>The lifecycle line for the Forum's mark at or past the head's tree size.</summary>
    public static string MarkPastHead(long declaration, long treeSize) =>
        string.Create(CultureInfo.InvariantCulture,
            $"the Forum marks this post disputed by a declaration at log index {declaration}, which the latest signed head (tree size {treeSize}) does not cover, so this check can neither confirm nor refute it; do not cite it as the author's");
}
