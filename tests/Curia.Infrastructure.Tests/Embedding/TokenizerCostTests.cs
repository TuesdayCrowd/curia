using System.Diagnostics.CodeAnalysis;
using Curia.Infrastructure.Embedder.Tokenization;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), Amendment 3 ruling 53 (SEM57), model-free: the tokenizer is streaming (Amendment 1 ruling 22), so the memory it allocates for a 256 KiB body is what it allocates for the head it reads
/// and not for the length of the text. SEM31 holds the characters the scan consumed; a pre-pass that cleans and normalises the whole text before the scan leaves that counter unchanged, so only the bytes
/// the call allocates can see it. Allocation is counted on the calling thread, which no other test shares, and no time is read (ruling 13 of the plan).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenizerCostTests
{
    /// <summary>The most the call may allocate over the head's own allocation: the mutants allocate megabytes, a streaming scan allocates the same bytes for both texts.</summary>
    private const long Slack = 16 * 1024;

    private static long Allocated(WordPieceTokenizer tokenizer, string text)
    {
        // Two calls first: the JIT, the static tables and the first use of the lookup are paid before the count.
        tokenizer.Tokenize(text, 510);
        tokenizer.Tokenize(text, 510);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = tokenizer.Tokenize(text, 510);
        var after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(result);
        return after - before;
    }

    /// <summary>
    /// SEM57. A text of about 256 KiB whose head is 510 content tokens, with a non-ASCII letter in every repetition (so that a pre-pass has normalisation to do): the bytes allocated to tokenize it are within
    /// 16 KiB of the bytes allocated to tokenize its own head. Red on FSEM43, both forms (measured on a copy of the tree): <c>tokenizing 262,119 characters allocated 8,237,536 bytes and its
    /// 2,551-character head 437,184: a difference of 7,800,352, expected at most 16,384</c>. The sizes depend on the host; the difference does not fall below a megabyte, and on the tree as it stands it is inside the bound. Green on the tree as it stands.
    /// </summary>
    [Fact]
    public void R9_63_SEM57_AnEnormousTextAllocatesWhatItsHeadAllocates()
    {
        var tokenizer = new WordPieceTokenizer(EmbedderRig.Vocabulary);
        var unit = "the quick caf" + Cp.Of(0x00E9) + " jumps over the lazy dog ";
        var text = string.Concat(Enumerable.Repeat(unit, 256 * 1024 / unit.Length));
        Assert.True(text.Length > 200_000, "NOT REACHED: the text is not about 256 KiB");

        var read = tokenizer.Tokenize(text, 510);
        Assert.True(read.StoppedAtCap && read.ContentIds.Count == 510, $"NOT REACHED: the scan did not stop at 510 tokens ({read.ContentIds.Count}, stopped at the cap: {read.StoppedAtCap})");
        Assert.True(read.CharactersConsumed < 10_000, $"the scan consumed {read.CharactersConsumed} characters of a text whose head is a few thousand");
        var head = text[..read.CharactersConsumed];

        var whole = Allocated(tokenizer, text);
        var headOnly = Allocated(tokenizer, head);
        Assert.True(
            whole - headOnly <= Slack,
            $"tokenizing {text.Length:N0} characters allocated {whole:N0} bytes and its {head.Length:N0}-character head {headOnly:N0}: a difference of {whole - headOnly:N0}, expected at most {Slack:N0}");
    }
}
