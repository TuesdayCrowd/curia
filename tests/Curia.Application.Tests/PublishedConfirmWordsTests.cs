using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Ingest;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// AFC28 (errata G55): <see cref="PublishedConfirmWords"/> reads the entry's published words and figures, every span with its count asserted, and the figure the code
/// states for the confirm's bound is compared with the entry's, read separately. A missing paragraph fails by name.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublishedConfirmWordsTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>The confirm's bound as the code states it: a public static <c>ConfirmBound</c> of <see cref="AppendConfirmation"/>, or <see langword="null"/> where there is none.</summary>
    private static TimeSpan? CodeBound()
    {
        var flags = BindingFlags.Public | BindingFlags.Static;
        var value = typeof(AppendConfirmation).GetField("ConfirmBound", flags)?.GetValue(null) ?? typeof(AppendConfirmation).GetProperty("ConfirmBound", flags)?.GetValue(null);
        return value as TimeSpan?;
    }

    /// <summary>
    /// AFC28. The entry's R8.84, R11.76 and R11.77 words parse (the figure, the five outcomes, the two events with their ids and messages, the two sentences, the unheard
    /// refusal's type and title, the not-sent title), and the code's <c>ConfirmBound</c> is the figure the entry states.
    /// </summary>
    [Fact]
    public void R8_84_AFC28_TheEntrysWordsParseAndTheCodesBoundIsTheEntrys()
    {
        var parsed = new List<string>();
        var failed = false;
        try
        {
            parsed.Add($"bound {PublishedConfirmWords.ConfirmBoundSeconds} s");
            parsed.Add($"{PublishedConfirmWords.Outcomes().Length} outcomes");
            var (confirmed, notConfirmed) = (PublishedConfirmWords.ConfirmedEvent(), PublishedConfirmWords.NotConfirmedEvent());
            parsed.Add($"events {confirmed.Name} {confirmed.Id} and {notConfirmed.Name} {notConfirmed.Id}");
            parsed.Add(PublishedConfirmWords.StoreSentence.Length > 0 && PublishedConfirmWords.UnheardSentence.Length > 0 ? "2 sentences" : "a sentence is empty");
            var (type, title) = PublishedConfirmWords.UnheardRefusal();
            parsed.Add($"refusal {type} / {title}");
            parsed.Add($"not-sent title {PublishedConfirmWords.NotSentTitle}");
        }
        catch (InvalidOperationException missing)
        {
            parsed.Add(missing.Message);
            failed = true;
        }

        Said("every span of the entry's words parses, once each", failed || parsed.Count != 6 ? "a span did not parse: " + string.Join("; ", parsed) : "every span of the entry's words parses, once each");

        var code = CodeBound();
        Said(
            $"the code states ConfirmBound as {PublishedConfirmWords.ConfirmBoundSeconds} s",
            $"the code states ConfirmBound as {(code is { } c ? $"{c.TotalSeconds:0.###} s" : "(none)")}");
    }
}
