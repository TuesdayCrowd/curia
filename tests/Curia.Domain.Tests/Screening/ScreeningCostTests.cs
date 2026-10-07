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
    /// The budget this test owns, per screening call at R6.39's cap, set in Release on an Apple M3
    /// Max (Arm64, 16 processors), where the slowest row measured 173 ms (zero-width-space,
    /// ScreenEnvelope); the failure message names both that machine and the one running. The 4x
    /// margin A3 records is judged on CI's separate screening-cost step, which runs this class alone
    /// after one untimed warm-up per process (<see cref="WarmUp"/>); A3 Step 5's 500 ms stop guards
    /// that margin and never moves this budget.
    /// </summary>
    private const int BudgetMilliseconds = 2_000;

    /// <summary>R10.69: the budget "is stated with the machine it was measured on".</summary>
    private const string BudgetMachine = "set in Release on an Apple M3 Max, Arm64, 16 processors";

    /// <summary>R6.39's per-string cap, in UTF-8 bytes.</summary>
    private const int CapBytes = 262_144;

    /// <summary>8 in SecretScanner, 6 in InjectionDetector, 1 in DerivedViews.</summary>
    private const int ExpectedPatternCount = 15;

    /// <summary>
    /// The scaling fact's ceiling on min(large) / min(small) for an eightfold longer input: linear
    /// work gives about 8 (measured 7.1 to 8.5 on the M3 Max), quadratic about 64. It is derived
    /// from R10.69's form, linear, not from a measured duration, so it holds on any machine running
    /// this class alone: load from other processes between samples is not linear work. The fact is
    /// judged in CI's isolated screening-cost step.
    /// </summary>
    private const double MaxScalingRatio = 24;

    /// <summary>The scaling fact's smaller input: an eighth of R6.39's cap.</summary>
    private const int SmallBytes = CapBytes / 8;

    /// <summary>Serialises the timing and scaling files' appends: theory rows may run in parallel.</summary>
    private static readonly SemaphoreSlim TimingsLock = new(1, 1);

    /// <summary>
    /// One untimed call to each screening entry point on a one-character input, awaited before any
    /// row is timed. It absorbs only what a process pays once: building the fifteen non-backtracking
    /// automata (<c>[GeneratedRegex]</c> emits no code for that engine) and JIT-compiling the path,
    /// about 100 ms locally, which CI otherwise charged to whichever row ran first. The automaton
    /// states a row's own input needs are still built inside its timed call, because the caller
    /// chooses that cost. A1's precedent: the fuzzer excludes each row's first send. Never a
    /// full-size row: a regressed rule would then run outside <see cref="Guarded"/>'s timeout.
    /// </summary>
    private static readonly Lazy<Task> WarmUp = new(() => Task.Run(
        () =>
        {
            Assert.True(ContentScreener.ScreenText("x"u8).TryGetValue(out _, out var textError), textError?.Type);
            var envelope = Canonical("x");
            Assert.True(ContentScreener.ScreenEnvelope(envelope).TryGetValue(out _, out var envelopeError), envelopeError?.Type);
        },
        CancellationToken.None));

    /// <summary>
    /// The engine fact, by reflection over the screening namespace: it invokes each pattern, which
    /// the IL-wide rule cannot. That rule, which sees a field, a <c>new Regex</c> or a static call
    /// anywhere under <c>src/</c>, lives in <c>Curia.Architecture.Tests/RegexEngineTests.cs</c>.
    /// </summary>
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
        await WarmUp.Value.ConfigureAwait(true);

        var text = Row(row, CapBytes);
        var utf8 = Encoding.UTF8.GetBytes(text);
        Assert.True(utf8.Length <= CapBytes, $"{row}: {utf8.Length} bytes exceeds R6.39's cap");
        var envelopeBytes = Canonical(text);

        var envelopeMs = await Timed(row, "ScreenEnvelope", () => ContentScreener.ScreenEnvelope(envelopeBytes));
        var textMs = await Timed(row, "ScreenText", () => ContentScreener.ScreenText(utf8));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{row}: {utf8.Length} bytes, ScreenEnvelope {envelopeMs} ms, ScreenText {textMs} ms ({Machine()})"));

        // A3 Step 5: CI's logger prints no passing row's output, so CI names a file to read instead.
        if (Environment.GetEnvironmentVariable("CURIA_SCREEN_TIMINGS") is { Length: > 0 } timings)
        {
            await Record(
                timings,
                TimingLine(row, "ScreenEnvelope", utf8.Length, envelopeMs) + TimingLine(row, "ScreenText", utf8.Length, textMs));
        }
    }

    /// <summary>
    /// R10.69's own claim, linear, checked on any machine running this class alone (load from other
    /// processes between samples is not linear work; the fact is judged in CI's isolated
    /// screening-cost step): each row's cost at R6.39's cap is at most
    /// <see cref="MaxScalingRatio"/> times its cost at an eighth of it. Small and large inputs
    /// alternate, three of each, and the fastest of each size is compared, so a pause lands on one
    /// sample rather than on the ratio. Every call runs under <see cref="Guarded"/>'s timeout. It
    /// writes the scaling file <c>CURIA_SCREEN_SCALING</c> names, one line per call, and never the
    /// timing file: the stop reads the budget theory's timings only.
    /// </summary>
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
    public async Task R10_69_ScreeningCostScalesLinearlyToTheStringCap(string row)
    {
        await WarmUp.Value.ConfigureAwait(true);

        var smallText = Row(row, SmallBytes);
        var largeText = Row(row, CapBytes);
        var smallUtf8 = Encoding.UTF8.GetBytes(smallText);
        var largeUtf8 = Encoding.UTF8.GetBytes(largeText);
        var smallEnvelope = Canonical(smallText);
        var largeEnvelope = Canonical(largeText);

        await AssertLinear(
            row,
            "ScreenEnvelope",
            () => ContentScreener.ScreenEnvelope(smallEnvelope),
            () => ContentScreener.ScreenEnvelope(largeEnvelope));
        await AssertLinear(
            row,
            "ScreenText",
            () => ContentScreener.ScreenText(smallUtf8),
            () => ContentScreener.ScreenText(largeUtf8));
    }

    private static async Task AssertLinear(
        string row,
        string call,
        Func<Result<ScreeningResult>> small,
        Func<Result<ScreeningResult>> large)
    {
        var smallMin = double.MaxValue;
        var largeMin = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            smallMin = Math.Min(smallMin, await Guarded(row, call, small).ConfigureAwait(false));
            largeMin = Math.Min(largeMin, await Guarded(row, call, large).ConfigureAwait(false));
        }

        var ratio = largeMin / Math.Max(smallMin, 1);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{row}: {call} ratio {ratio:F2} ({largeMin:F0} / {smallMin:F0} ticks; {Machine()})"));

        // CI's logger prints no passing row's output, so CI names a file for the ratios too.
        if (Environment.GetEnvironmentVariable("CURIA_SCREEN_SCALING") is { Length: > 0 } scaling)
            await Record(scaling, ScalingLine(row, call, smallMin, largeMin, ratio)).ConfigureAwait(false);

        Assert.True(
            ratio <= MaxScalingRatio,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{row}: {call} at {CapBytes} bytes took {ratio:F1}x its time at {SmallBytes} bytes "
                + $"(fastest {largeMin:F0} vs {smallMin:F0} Stopwatch ticks of 3); linear is about 8, quadratic about 64, "
                + $"ceiling {MaxScalingRatio} (R10.69: screening is linear on every path; running on {Machine()})"));
    }

    /// <summary>Appends <paramref name="lines"/> to the timing file, one row's calls at a time.</summary>
    private static async Task Record(string path, string lines)
    {
        await TimingsLock.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, lines, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TimingsLock.Release();
        }
    }

    /// <summary>One TSV line: row, call, UTF-8 bytes, elapsed ms, architecture, processors. Never the screened text.</summary>
    private static string TimingLine(string row, string call, int bytes, long elapsed) => string.Create(
        CultureInfo.InvariantCulture,
        $"{row}\t{call}\t{bytes}\t{elapsed}\t{RuntimeInformation.ProcessArchitecture}\t{Environment.ProcessorCount}\n");

    /// <summary>
    /// One TSV line: row, call, small and large UTF-8 bytes, the fastest Stopwatch ticks of each,
    /// the ratio, architecture, processors. Never the screened text.
    /// </summary>
    private static string ScalingLine(string row, string call, double smallMin, double largeMin, double ratio) => string.Create(
        CultureInfo.InvariantCulture,
        $"{row}\t{call}\t{SmallBytes}\t{CapBytes}\t{smallMin:F0}\t{largeMin:F0}\t{ratio:F2}\t{RuntimeInformation.ProcessArchitecture}\t{Environment.ProcessorCount}\n");

    /// <summary>One call held to the budget, in milliseconds.</summary>
    private static async Task<long> Timed(string row, string call, Func<Result<ScreeningResult>> screen)
    {
        var elapsed = (long)(await Guarded(row, call, screen).ConfigureAwait(false) * 1000 / Stopwatch.Frequency);

        Assert.True(
            elapsed <= BudgetMilliseconds,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{row}: {call} took {elapsed} ms against a budget of {BudgetMilliseconds} ms {BudgetMachine}; running on {Machine()}"));
        return elapsed;
    }

    /// <summary>
    /// Runs one screening call on its own thread, returning its Stopwatch ticks, and fails at five
    /// budgets rather than waiting for a quadratic rule to finish: a timed-out call keeps running
    /// until the process ends, which is why the red run is filtered to this class alone.
    /// </summary>
    private static async Task<double> Guarded(string row, string call, Func<Result<ScreeningResult>> screen)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(BudgetMilliseconds * 5);

        var work = Task.Run(
            () =>
            {
                var stopwatch = Stopwatch.StartNew();
                Assert.True(screen().TryGetValue(out _, out var error), error?.Type);
                return (double)stopwatch.ElapsedTicks;
            },
            CancellationToken.None);

        try
        {
            return await work.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{row}: {call} did not finish within {BudgetMilliseconds * 5} ms (budget {BudgetMilliseconds} ms {BudgetMachine}; running on {Machine()})"));
            throw;
        }
    }

    private static string Machine() => string.Create(
        CultureInfo.InvariantCulture,
        $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} processors");

    /// <summary>The canonical <c>{"body": text}</c> envelope, as the screener's envelope path reads it.</summary>
    private static byte[] Canonical(string text)
    {
        Assert.True(
            CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([new("body", new JsonValue.String(text))]))
                .TryGetValue(out var envelope, out var error),
            error?.Type);
        return envelope.Span.ToArray();
    }

    /// <summary>Each row at <paramref name="bytes"/> UTF-8 bytes: the unit repeated to that length.</summary>
    private static string Row(string row, int bytes) => row switch
    {
        "r" => Repeat("r", bytes),
        "space" => Repeat(" ", bytes),
        "tab" => Repeat("\t", bytes),
        "ai-then-spaces" => "ai" + new string(' ', bytes - 2),
        "html-comment-open" => Repeat("<!--", bytes),
        "http-scheme" => Repeat("http://", bytes),
        "a-colon" => Repeat("a:", bytes),
        "token-assignment" => Repeat("token = ", bytes),
        "ignore-all" => Repeat("ignore all ", bytes),
        "zero-width-space" => new string((char)0x200B, bytes / 3),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row"),
    };

    /// <summary>An ASCII unit repeated, cut to exactly <paramref name="bytes"/>: one char is one UTF-8 byte.</summary>
    private static string Repeat(string unit, int bytes)
    {
        var builder = new StringBuilder(bytes + unit.Length);
        while (builder.Length < bytes)
            builder.Append(unit);
        return builder.ToString(0, bytes);
    }
}
