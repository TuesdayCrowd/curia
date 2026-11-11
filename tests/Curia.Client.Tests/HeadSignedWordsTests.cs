using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR H (errata G67, R11.94): <see cref="HeadSignedWords"/>, the words a verification past the latest head says about when that head was signed. Pure. Every expectation
/// here is written by hand, with the sentence of R11.94 words it follows quoted beside it: parsing a grammar out of prose would be a second implementation of the rule
/// inside the test. The head's timestamp is the string the head signed; the words print the client's own rendering of the instant and never the string.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HeadSignedWordsTests
{
    private static readonly DateTimeOffset Began = new(2026, 11, 11, 9, 31, 0, TimeSpan.Zero);

    // The published NOT STATED form, read from R11.94 words (WAF30: no test copies a published sentence).
    private static readonly string NotStated = PublishedClientWords.SignedNotStatedForm;

    private static string Row(string name, string expected, string actual) => expected == actual ? string.Empty : $"{name}: expected \"{expected}\", got \"{actual}\"";

    /// <summary>
    /// HSU27. "<c>signed at &lt;instant&gt;, &lt;k&gt; s before this check began</c> for a head signed a second or more before the check began ... less than a second
    /// before ... after this check began (this client's clock and the signer's differ) ... where the timestamp is absent or is not <c>yyyy-MM-ddTHH:mm:ss</c>, zero to
    /// seven fractional digits and <c>Z</c>: NOT STATED. The instant is UTC to the millisecond, truncated and not rounded." Red on the seam, where the clause is empty.
    /// </summary>
    [Fact]
    public void R11_94_HSU27_TheClauseStatesTheInstantAndTheAgeAndNeverTheSignedString()
    {
        var hostile = "\u2603Q\u0000\n" + new string('\u03A9', 4096);
        var rows = new (string Name, string? Timestamp, DateTimeOffset Began, string Expected)[]
        {
            ("317 s before", "2026-11-11T09:25:43.000000Z", Began, "signed at 2026-11-11T09:25:43.000Z, 317 s before this check began"),
            ("400 ms before", "2026-11-11T09:30:59.600000Z", Began, "signed at 2026-11-11T09:30:59.600Z, less than a second before this check began"),
            ("2 s after", "2026-11-11T09:31:02.000000Z", Began, "signed at 2026-11-11T09:31:02.000Z, after this check began (this client's clock and the signer's differ)"),
            ("no fraction (the stub's form)", "2026-11-11T09:30:00Z", Began, "signed at 2026-11-11T09:30:00.000Z, 60 s before this check began"),
            ("microseconds render to the millisecond", "2026-11-11T09:30:00.123456Z", Began, "signed at 2026-11-11T09:30:00.123Z, 59 s before this check began"),
            ("truncated and not rounded, the age from the full value", "2026-11-11T09:30:00.9999999Z", new DateTimeOffset(2026, 11, 11, 9, 30, 10, 999, TimeSpan.Zero).AddTicks(9999), "signed at 2026-11-11T09:30:00.999Z, 10 s before this check began"),
            ("yesterday", "yesterday", Began, NotStated),
            ("empty", "", Began, NotStated),
            ("absent", null, Began, NotStated),
            ("no Z", "2026-11-11 09:30", Began, NotStated),
            ("an offset", "2026-11-11T09:30:00+00:00", Began, NotStated),
            ("a negative zero offset", "2026-11-11T09:30:00-00:00", Began, NotStated),
            ("a lower-case z", "2026-11-11T09:30:00z", Began, NotStated),
            ("a space for the T", "2026-11-11 09:30:00Z", Began, NotStated),
            ("eight fractional digits", "2026-11-11T09:30:00.12345678Z", Began, NotStated),
            ("a leap second", "2026-11-11T23:59:60Z", Began, NotStated),
            ("a date without a time", "2026-11-11", Began, NotStated),
            ("leading white space", " 2026-11-11T09:30:00Z", Began, NotStated),
            ("trailing white space", "2026-11-11T09:30:00Z ", Began, NotStated),
            ("a hostile 4 KiB string with a NUL and a line feed", hostile, Began, NotStated),
        };

        var failures = new List<string>();
        foreach (var (name, timestamp, began, expected) in rows)
        {
            var actual = HeadSignedWords.Signed(timestamp, began);
            var problem = Row(name, expected, actual);
            if (problem.Length > 0) failures.Add(problem);
            if (!string.IsNullOrEmpty(timestamp) && timestamp.Length > 8 && expected == NotStated && actual.Contains(timestamp[..8], StringComparison.Ordinal))
                failures.Add($"{name}: the words echo the signed string");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// HSU27 (the inclusion line's sentence): "between its first sentence and its second, the sentence <c>That head was &lt;signed&gt;.</c>". Red on the seam, where it is empty.
    /// </summary>
    [Fact]
    public void R11_94_HSU27_TheInclusionLineSentenceIsThatHeadWasTheClause()
    {
        Assert.Equal(
            "That head was signed at 2026-11-11T09:25:43.000Z, 317 s before this check began.",
            HeadSignedWords.InclusionSentence("2026-11-11T09:25:43.000000Z", Began));
    }

    /// <summary>
    /// HSU30. "ends with the another-check sentence where another check also could not be made, and not otherwise": a leaf past the head and
    /// a check not made carries the instant and the suffix; a leaf past the head and none missing carries the instant and no suffix. Red on the seam, where the closing holds no instant.
    /// </summary>
    [Fact]
    public void R11_94_HSU30_TheClosingCarriesTheInstantAndTheSuffixOnlyWhereAnotherCheckWasNotMade()
    {
        // The published closing and suffix, from R11.94 words (WAF30: no test copies a published sentence), with <n> and <signed> filled in by hand and by arithmetic
        // (09:25:43 plus 317 s is the check's 09:31:00).
        const string Clause = "signed at 2026-11-11T09:25:43.000Z, 317 s before this check began";
        var head = PublishedClientWords.Fold(PublishedClientWords.PastHeadClosing.Replace("<n>", "6", StringComparison.Ordinal).Replace("<signed>", Clause, StringComparison.Ordinal));

        Assert.Equal(
            [head + " " + PublishedClientWords.AnotherCheckSuffix, head],
            [
                PublishedClientWords.Fold(HeadSignedWords.Closing(6, "2026-11-11T09:25:43.000000Z", Began, anotherCheckCouldNotBeMade: true)),
                PublishedClientWords.Fold(HeadSignedWords.Closing(6, "2026-11-11T09:25:43.000000Z", Began, anotherCheckCouldNotBeMade: false)),
            ]);
    }

    /// <summary>
    /// HSU29, the control, new row (the existing WAF probes are the rest of it): a closing over a log with no head, or a head that covers the post, holds no
    /// <c>signed at</c>. Green on the seam and on every commit.
    /// </summary>
    [Fact]
    public void R11_94_HSU29_AClosingWithNoPastHeadCauseSaysNothingOfWhenAHeadWasSigned()
    {
        var verification = new PostVerification(
            "post-1",
            Check.Verified("signature"),
            Check.CouldNotCheck("no signed head has been published"),
            Check.CouldNotCheck("nothing to compare"),
            Check.CouldNotCheck("needs the inclusion"),
            Check.CouldNotCheck("needs the inclusion"),
            "sha256:00",
            LogIndex: 3,
            AnchorTreeSize: null);

        var rendered = verification.Render();

        Assert.Contains("NOT ESTABLISHED", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("signed at", rendered, StringComparison.Ordinal);
    }
}
