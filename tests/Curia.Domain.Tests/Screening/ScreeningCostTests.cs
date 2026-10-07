using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// R10.69: <i>"Screening is linear on every path. Every screening pattern runs on a linear-time
/// engine, and a test fails if any pattern does not. A timing test runs at R6.39's cap against a
/// budget the test owns."</i> (errata G18, register D32.)
///
/// <para>Two mechanisms, because the request fuzzer cannot be the gate: the worst input measured,
/// <c>"ai"</c> followed by spaces, is keyed to one detector's anchor word, and a variation set
/// derived from the request surface knows nothing of the detectors. The engine fact rests on .NET's
/// documented linear-time guarantee for <see cref="RegexOptions.NonBacktracking"/>, a different
/// artifact from the patterns it checks. The timing theory has a row against each rule once found
/// quadratic (spec §2.1).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ScreeningCostTests
{
    /// <summary>
    /// The budget this test owns, per screening call at R6.39's cap. Measured under the linear
    /// engine at well under a quarter of this; the 4x margin is the assumption A3 records.
    /// </summary>
    private const int BudgetMilliseconds = 2_000;

    /// <summary>R6.39's per-string cap, in UTF-8 bytes.</summary>
    private const int CapBytes = 262_144;

    /// <summary>8 in SecretScanner, 6 in InjectionDetector, 1 in DerivedViews.</summary>
    private const int ExpectedPatternCount = 15;

    [Fact]
    public void R10_69_EveryScreeningPatternRunsOnTheLinearEngine()
    {
        var methods = typeof(ContentScreener).Assembly.GetTypes()
            .Where(t => t.Namespace == "Curia.Domain.Screening")
            .SelectMany(t => t.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0)
            .ToArray();

        var names = methods.Select(m => $"{m.DeclaringType!.Name}.{m.Name}").OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(
            methods.Length == ExpectedPatternCount,
            $"Expected {ExpectedPatternCount} screening patterns, found {methods.Length}: {string.Join(", ", names)}");

        var offenders = methods
            .Where(m => !((Regex)m.Invoke(null, null)!).Options.HasFlag(RegexOptions.NonBacktracking))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{offenders.Length} of {methods.Length} screening patterns lack RegexOptions.NonBacktracking (R10.69): "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("r")]
    [InlineData("space")]
    [InlineData("tab")]
    [InlineData("ai-then-spaces")]
    [InlineData("html-comment-open")]
    [InlineData("http-scheme")]
    [InlineData("a-colon")]
    [InlineData("token-assignment")]
    [InlineData("ignore-all")]
    [InlineData("zero-width-space")]
    public async Task R10_69_ScreeningAtTheStringCapStaysWithinItsBudget(string row)
    {
        var text = Row(row);
        var utf8 = Encoding.UTF8.GetBytes(text);
        Assert.True(utf8.Length <= CapBytes, $"{row}: {utf8.Length} bytes exceeds R6.39's cap");

        Assert.True(
            CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([new("body", new JsonValue.String(text))]))
                .TryGetValue(out var envelope, out var error),
            error?.Type);
        var envelopeBytes = envelope.Span.ToArray();

        var envelopeMs = await Timed(row, "ScreenEnvelope", () => ContentScreener.ScreenEnvelope(envelopeBytes));
        var textMs = await Timed(row, "ScreenText", () => ContentScreener.ScreenText(utf8));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{row}: {utf8.Length} bytes, ScreenEnvelope {envelopeMs} ms, ScreenText {textMs} ms ({Machine()})"));
    }

    /// <summary>
    /// Runs one screening call on its own thread and fails at five budgets rather than waiting for
    /// a quadratic rule to finish: a timed-out call keeps running until the process ends, which is
    /// why the red run is filtered to this class alone.
    /// </summary>
    private static async Task<long> Timed(string row, string call, Func<Result<ScreeningResult>> screen)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(BudgetMilliseconds * 5);

        var work = Task.Run(
            () =>
            {
                var stopwatch = Stopwatch.StartNew();
                Assert.True(screen().TryGetValue(out _, out var error), error?.Type);
                return stopwatch.ElapsedMilliseconds;
            },
            CancellationToken.None);

        long elapsed;
        try
        {
            elapsed = await work.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{row}: {call} did not finish within {BudgetMilliseconds * 5} ms (budget {BudgetMilliseconds} ms; {Machine()})"));
            throw;
        }

        Assert.True(
            elapsed <= BudgetMilliseconds,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{row}: {call} took {elapsed} ms against a budget of {BudgetMilliseconds} ms ({Machine()})"));
        return elapsed;
    }

    private static string Machine() => string.Create(
        CultureInfo.InvariantCulture,
        $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} processors");

    /// <summary>Each row at R6.39's cap: the unit repeated to 262,144 UTF-8 bytes.</summary>
    private static string Row(string row) => row switch
    {
        "r" => Repeat("r"),
        "space" => Repeat(" "),
        "tab" => Repeat("\t"),
        "ai-then-spaces" => "ai" + new string(' ', CapBytes - 2),
        "html-comment-open" => Repeat("<!--"),
        "http-scheme" => Repeat("http://"),
        "a-colon" => Repeat("a:"),
        "token-assignment" => Repeat("token = "),
        "ignore-all" => Repeat("ignore all "),
        "zero-width-space" => new string((char)0x200B, CapBytes / 3),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row"),
    };

    /// <summary>An ASCII unit repeated, cut to exactly the cap: one char is one UTF-8 byte.</summary>
    private static string Repeat(string unit)
    {
        var builder = new StringBuilder(CapBytes + unit.Length);
        while (builder.Length < CapBytes)
            builder.Append(unit);
        return builder.ToString(0, CapBytes);
    }
}
