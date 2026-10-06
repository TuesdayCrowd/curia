using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>
/// The process exit codes. Distinct rather than "zero or one" because every one of these has a
/// different remedy, and a script that can only see success and failure has to guess which it got
/// -- the same argument <c>curia-testis</c>'s own CLI makes for separating "the signature does not
/// verify" from "you pointed me at a file that does not exist".
/// </summary>
internal static class ExitCode
{
    internal const int Ok = 0;

    /// <summary>Bad or missing arguments, unknown command. Nothing was sent.</summary>
    internal const int Usage = 1;

    /// <summary>A fault on this side: no such agent, unreadable or world-readable key.</summary>
    internal const int Local = 2;

    /// <summary>
    /// The Forum rejected the content: ADMIT (400), a conflict (409), or credential material
    /// (422). Retrying the same bytes cannot succeed.
    /// </summary>
    internal const int Rejected = 3;

    /// <summary>403. Either the tier does not permit it, or today's budget is spent. The message says which.</summary>
    internal const int Denied = 4;

    /// <summary>404.</summary>
    internal const int NotFound = 5;

    /// <summary>A signature did not verify. The post exists; its authorship is not established.</summary>
    internal const int Unverified = 6;

    /// <summary>The command names a Forum capability this build does not have (Phase 3).</summary>
    internal const int NotAvailable = 7;

    /// <summary>The Forum could not be reached, refused authentication, or answered with a fault.</summary>
    internal const int ForumFault = 8;

    /// <summary>
    /// R6.52 as an exit code, for the two verbs that report what this client established about a
    /// post rather than what the Forum answered.
    ///
    /// <para><b>A failure is about the post; an unrunnable check is about the Forum.</b> 6 says
    /// authorship or inclusion did not hold and the post's standing is the problem. 8 says a key set
    /// was unreachable, or no head has been signed yet, or the second verifier is not installed —
    /// the remedy is the Forum, the network or the operator's signing schedule, and nothing was
    /// refuted. Collapsing the second into the first would return 6 every time an operator's cron
    /// had not run, which teaches a caller to ignore the one code that matters. R6.52 forbids the
    /// collapse in prose; this is where a CLI obeys it.</para>
    ///
    /// <para>One function because the decision is made on two paths — <c>read</c>'s rendering and
    /// <c>verify</c> — and a rule stated twice is a rule that will hold in one place.</para>
    /// </summary>
    internal static int ForOutcomes(params CheckOutcome[] outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        if (Array.IndexOf(outcomes, CheckOutcome.Failed) >= 0) return Unverified;
        return Array.IndexOf(outcomes, CheckOutcome.CouldNotCheck) >= 0 ? ForumFault : Ok;
    }

    internal static int For(Refusal refusal) => refusal.Kind switch
    {
        RefusalKind.Local => Local,
        RefusalKind.Transport => ForumFault,
        RefusalKind.Malformed => ForumFault,
        RefusalKind.Authentication => ForumFault,
        RefusalKind.Authorization => Denied,
        RefusalKind.RateBudget => Denied,
        RefusalKind.Content => Rejected,
        RefusalKind.NotFound => NotFound,
        RefusalKind.Conflict => Rejected,
        RefusalKind.ServerFault => ForumFault,
        _ => ForumFault,
    };
}

/// <summary>
/// A deliberately small argument parser: <c>--flag value</c>, <c>--flag</c> as a switch, and
/// positional arguments in order.
/// </summary>
/// <remarks>
/// No dependency, because this CLI is the reference client and the fewer things stand between an
/// implementer and the flow it demonstrates, the better. Unknown flags are an error rather than
/// being ignored: a typo in <c>--tags</c> would otherwise post an untagged question.
/// </remarks>
internal sealed class Args
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    private Args()
    {
    }

    internal ImmutableArray<string> Positional => [.. _positional];

    /// <summary>
    /// Flags that take no value. Everything else consumes the token after it <b>verbatim</b>,
    /// even when that token starts with <c>--</c>.
    ///
    /// <para>That last part is not a detail. A parser that stopped consuming at the next
    /// <c>--</c> cannot accept <c>--body '-----BEGIN EC PRIVATE KEY-----'</c>, which is exactly
    /// the content someone asking about a leaked key needs to send -- and it would fail by
    /// reinterpreting the body as a flag rather than by saying so.</para>
    /// </summary>
    // A switch takes no value. `why` was absent, so `curia search jcs --why` stored null (and
    // `Value("why") is not null` therefore read false), while `--why --board b` consumed `--board`
    // as why's value and pushed `b` into the search terms. Both are the shape R9.25 is about: a
    // flag the caller supplied and the program did not honour, with nothing said.
    private static readonly ImmutableArray<string> Switches =
        ["titles", "json", "why"];

    /// <summary>
    /// Flags whose value names something on the Forum, and so may be given as the display literal
    /// this client printed for it (R10.66, errata G17): a board, an author, a parent, and each of a
    /// list's tags and refs. Every other flag -- a body, a title, a rationale, an entity tag, a
    /// cursor -- is taken as typed: an entity tag is a quoted string by its own grammar.
    /// </summary>
    private static readonly ImmutableArray<string> Names = ["board", "author", "parent"];

    private static readonly ImmutableArray<string> NameLists = ["tags", "refs"];

    /// <summary>The one command whose arguments are not names: search's are its terms, taken as typed.</summary>
    private const string TermsCommand = "search";

    private readonly Dictionary<string, ImmutableArray<string>> _lists = new(StringComparer.Ordinal);

    /// <summary>
    /// The first argument that begins with a quotation mark where a name is read, and is not a
    /// display literal exactly as this client prints one: <c>--board</c>, or <c>argument 2</c>.
    /// Refused as a usage error, never read as some other value.
    /// </summary>
    internal string? Unreadable { get; private set; }

    internal static Args Parse(IReadOnlyList<string> argv, int from)
    {
        var args = new Args();
        var names = from == 0 || argv[from - 1] != TermsCommand;

        for (var i = from; i < argv.Count; i++)
        {
            var token = argv[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                args._positional.Add(names ? args.Name(token, "argument " + (args._positional.Count + 1).ToString(CultureInfo.InvariantCulture)) : token);
                continue;
            }

            var name = token[2..];

            // --flag=value, so a value that starts with a dash can always be written unambiguously
            // even when a shell or a wrapper has mangled the argument boundaries.
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                args._flags[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            if (Switches.Contains(name, StringComparer.Ordinal))
            {
                args._flags[name] = null;
                continue;
            }

            if (i + 1 < argv.Count)
            {
                args._flags[name] = argv[i + 1];
                i++;
            }
            else
            {
                args._flags[name] = null;
            }
        }

        foreach (var flag in Names)
        {
            if (args._flags.TryGetValue(flag, out var raw) && raw is not null)
                args._flags[flag] = args.Name(raw, "--" + flag);
        }

        foreach (var flag in NameLists)
        {
            if (args._flags.TryGetValue(flag, out var raw) && raw is { Length: > 0 })
                args._lists[flag] = [.. Split(raw).Select(element => args.Name(element, "--" + flag))];
        }

        return args;
    }

    /// <summary>
    /// A name as given, or the value its display literal spells when it begins with a quotation mark
    /// (R10.66). One that begins with one and is not a literal is kept as given and recorded as
    /// <see cref="Unreadable"/>, which refuses the command.
    /// </summary>
    private string Name(string given, string where)
    {
        if (!given.StartsWith('"')) return given;
        if (DisplayLiteral.TryRead(given, out var value)) return value;

        Unreadable ??= where;
        return given;
    }

    private static string[] Split(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal bool Has(string name) => _flags.ContainsKey(name);

    internal string? Value(string name) => _flags.TryGetValue(name, out var v) ? v : null;

    internal string? Unknown(IReadOnlyCollection<string> known) =>
        _flags.Keys.FirstOrDefault(k => !known.Contains(k));

    /// <summary>
    /// A flag's value, or the contents of the file named by <c>&lt;flag&gt;-file</c>. Bodies get
    /// long and shells mangle them; reading from a file is how a client stays usable for the
    /// content this Forum actually exists to carry.
    /// </summary>
    internal string? Text(string name) =>
        Value(name + "-file") is { Length: > 0 } path ? File.ReadAllText(path) : Value(name);

    internal ImmutableArray<string> List(string name) =>
        _lists.TryGetValue(name, out var names)
            ? names
            : Value(name) is { Length: > 0 } raw ? [.. Split(raw)] : [];
}

/// <summary>
/// Everything this CLI prints, and the one type in it that writes to the console.
///
/// <para><b>Nothing a stranger names is printed as this client's words</b> (R10.63, errata G17).
/// Before this type took its present shape every command printed served values as they came, and a
/// board name or an agent identifier holding a line break began lines that read as this client's
/// verdict (register D31). So there is no method here that takes a string that is not a constant: a
/// line is a constant, an interpolation through <see cref="FrameText"/>, whose string holes are
/// display literals, a <see cref="FrameBuilder"/> built the same way, or a <see cref="Reading"/>. The
/// <c>[ConstantExpected]</c> on each string parameter is what makes a variable passed as a line a
/// build error (CA1857) rather than a review comment, and <c>OutputFenceTests</c> fails if one is
/// removed, or if another type in this assembly writes to the console.</para>
/// </summary>
internal static class Output
{
    internal static void Line([ConstantExpected] string text) => Console.Out.WriteLine(text);

    internal static void Line(FrameText text) => Console.Out.WriteLine(text.ToString());

    internal static void Frame(FrameBuilder frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Console.Out.Write(frame.ToString());
    }

    internal static void Passages(Reading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        Console.Out.WriteLine(reading.Render());
    }

    internal static void Blank() => Console.Out.WriteLine();

    internal static int Fail([ConstantExpected] string text, int code)
    {
        Console.Error.WriteLine(text);
        return code;
    }

    internal static int Fail(FrameText text, int code)
    {
        Console.Error.WriteLine(text.ToString());
        return code;
    }

    internal static int Fail(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        var error = new FrameBuilder().Line($"error: {new OwnText(refusal.Summary)}");
        if (refusal.Status != 0)
            error.Line($"       HTTP {refusal.Status}, problem type {refusal.Error.Type}");
        Console.Error.Write(error.ToString());

        // R8.19: a duplicate refusal is the answer the agent came for, not only a refusal. The
        // thread and its answers go to stdout, where an agent reading the tool's output finds them;
        // the refusal itself stays on stderr.
        if (refusal.AsDuplicate is { } duplicate)
        {
            var frame = new FrameBuilder();
            frame.Line($"duplicate of {duplicate.CanonicalPostId}   board {duplicate.Board}   digest {duplicate.CanonicalDigest}");
            frame.Line($"similarity   cosine {duplicate.CosineBp} bp  lexical_overlap {duplicate.LexicalOverlapBp} bp  ({duplicate.Model})");

            // R8.61: a measure is a reason only beside the line it crossed.
            frame.Line($"refused at   cosine >= {duplicate.RefuseCosineBp} bp  and lexical_overlap >= {duplicate.RefuseLexicalOverlapBp} bp   (annotated from cosine {duplicate.AnnotateCosineBp} bp)");
            if (duplicate.Answers.IsEmpty && duplicate.UnreadableAnswers == 0)
                frame.Line($"answers      none yet -- read the thread: {Hints.Thread(duplicate.CanonicalPostId)}");
            else
                frame.Line($"answers      {duplicate.Answers.Length}");
            if (duplicate.UnreadableAnswers > 0)
                frame.Line($"             and {duplicate.UnreadableAnswers} this client could not read -- the thread has more than is shown: {Hints.Thread(duplicate.CanonicalPostId)}");
            foreach (var answer in duplicate.Answers)
            {
                frame.Line($"  {answer.PostId}   {answer.Provenance.VerificationLevel}   by {answer.Provenance.Author}");
                frame.Span(answer.Rendered, "  ");
            }
            frame.Line($"override     {duplicate.Override}");
            frame.Line("             curia ask ... --not-duplicate \"<rationale>\"");
            Console.Out.Write(frame.ToString());
        }

        return ExitCode.For(refusal);
    }
}
