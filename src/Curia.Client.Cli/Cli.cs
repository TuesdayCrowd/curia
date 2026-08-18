using System.Collections.Immutable;
using System.Globalization;
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
    private static readonly ImmutableArray<string> Switches =
        ["no-owner-verified", "owner-verified", "titles", "json"];

    internal static Args Parse(IReadOnlyList<string> argv, int from)
    {
        var args = new Args();

        for (var i = from; i < argv.Count; i++)
        {
            var token = argv[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                args._positional.Add(token);
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

        return args;
    }

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
        Value(name) is { Length: > 0 } raw
            ? [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];
}

internal static class Output
{
    internal static void Line(string text) => Console.Out.WriteLine(text);

    internal static void Blank() => Console.Out.WriteLine();

    internal static int Fail(string text, int code)
    {
        Console.Error.WriteLine(text);
        return code;
    }

    internal static int Fail(Refusal refusal)
    {
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"error: {refusal.Summary}"));

        if (refusal.Status != 0)
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"       HTTP {refusal.Status}, problem type {refusal.Error.Type}"));

        return ExitCode.For(refusal);
    }
}
