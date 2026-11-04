using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>CWK34 (printed half; errata G59, R6.61, R11.83; plan ruling 30, FCWK21): the could-not-be-checked line a real verification prints, held to the published words with nothing added.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RetainedHeadRoleTests
{
    private static string Flip(string signature)
    {
        var middle = signature.Length / 2;
        return signature[..middle] + (signature[middle] == 'A' ? 'B' : 'A') + signature[(middle + 1)..];
    }

    /// <summary>The published words with their placeholders filled by <paramref name="values"/> (in order of appearance), whitespace folded, the line's own label removed.</summary>
    private static string Filled(string words, params string[] values)
    {
        var at = 0;
        var filled = Regex.Replace(words, @"<[a-z][a-z ]*>", _ => values[at++], RegexOptions.CultureInvariant);
        Assert.Equal(values.Length, at);
        const string Label = "consistency COULD NOT BE CHECKED: ";
        Assert.StartsWith(Label, filled, StringComparison.Ordinal);
        return Regex.Replace(filled[Label.Length..], @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }

    /// <summary>
    /// CWK34 (printed). A real verification over the stub log with a retained head that does not verify, once with a flipped signature under a published kid (the signature words) and once under a kid the
    /// log does not publish (the key words): the printed could-not-be-checked line equals the published words with only the tree size, kid, instant and directory filled in, so a sentence that names a cause
    /// the check cannot know, or any other word the entry does not publish, is red. Predicted red under FCWK21: any change of the sentence.
    /// </summary>
    [Fact]
    public async Task R11_83_CWK34_ThePrintedCouldNotBeCheckedLineIsThePublishedWordsAndNothingMore()
    {
        var home = Directory.CreateTempSubdirectory("curia-head-words-").FullName;
        try
        {
            var problems = new List<string>();
            var retainedAt = new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero);
            var instant = retainedAt.UtcDateTime.ToString(HeadStore.InstantFormat, CultureInfo.InvariantCulture);
            var cases = new (string Name, Func<SignedHeadDocument, SignedHeadDocument> Plant, string Words)[]
            {
                ("flipped signature", h => h with { Signature = Flip(h.Signature) }, PublishedAgentWords.SignatureWords),
                ("unpublished kid", h => h with { Kid = "unpublished-kid" }, PublishedAgentWords.KeyWords),
            };
            foreach (var (name, plant, words) in cases)
            {
                var heads = new HeadStore(Path.Combine(home, name.Replace(' ', '-')));
                var planted = plant(Head(4));
                heads.Write(StubLog.Forum, planted, retainedAt);
                var verifier = new PostVerifier(_log.Client(), heads);
                var read = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, Ct);
                Assert.True(read.TryGetValue(out var run, out var refusal), refusal?.Summary);
                var expected = Filled(words, planted.TreeSize.ToString(CultureInfo.InvariantCulture), Check.Quote(planted.Kid), instant, Check.Quote(heads.DirectoryFor(StubLog.Forum)));
                var printed = Regex.Replace(run!.Consistency.Detail, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
                if (run.Consistency.Outcome != CheckOutcome.CouldNotCheck) problems.Add($"{name}: expected COULD NOT BE CHECKED, was {run.Consistency.Outcome}");
                else if (printed != expected) problems.Add($"{name}: printed \"{printed}\" is not the published words \"{expected}\"");
            }

            Assert.True(problems.Count == 0, "Expected: the printed line is the published words with only its placeholders filled. Actual: " + string.Join("; ", problems));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
