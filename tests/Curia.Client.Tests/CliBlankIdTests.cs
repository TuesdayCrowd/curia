using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.58 (errata G43): <c>curia</c> refuses a required post identifier that is empty or blank, exit 1, with a message that names the
/// argument, says nothing was sent and holds no exception text, before a profile is loaded or a token minted. <c>curia flag ''</c> once
/// printed an <c>ArgumentException</c>'s message ("The value cannot be an empty string or composed entirely of whitespace. (Parameter
/// 'postId')") and the other verbs that take one each did whatever their client call did.
///
/// <para><b>Oracles.</b> The list of commands is derived, not written: the verbs are read from <c>Main</c>'s dispatch, and a verb is in
/// the list when its own usage line (<see cref="Help.For"/>) begins with a required post-identifier positional (<c>&lt;post-id&gt;</c>,
/// <c>&lt;root-id&gt;</c> or <c>&lt;answer-id&gt;</c> without brackets). The request list is a recorder's. The control is the same call with a
/// well-formed id the recorder does not know: it must still be sent and answer the Forum's "No such post".</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliBlankIdTests : IDisposable
{
    private readonly RecordingForum _forum = new();
    private readonly string _home;

    public CliBlankIdTests() => _home = CuriaCli.NewHome(_forum.Address);

    public void Dispose()
    {
        _forum.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^\s*((?:""[a-z]+""\s*or\s*)*""[a-z]+"")\s*=>", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DispatchArm();

    [GeneratedRegex(@"^curia (?<verb>[a-z]+) <(?<name>post-id|root-id|answer-id)>", RegexOptions.CultureInvariant)]
    private static partial Regex RequiredPostId();

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory)
            : File.ReadAllText(Path.Combine(dir.FullName, relative));
    }

    /// <summary>Every command whose usage takes a required post identifier, with the name its usage gives that argument.</summary>
    internal static (string Verb, string Argument)[] CommandsWithARequiredPostId()
    {
        var program = RepoFile("src/Curia.Client.Cli/Program.cs");
        var start = program.IndexOf("return command switch", StringComparison.Ordinal);
        var end = program.IndexOf("_ =>", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Main's dispatch was not found");

        var verbs = DispatchArm().Matches(program[start..end])
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, "\"([a-z]+)\"").Select(v => v.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(verbs.Length >= 20, "Main dispatches " + verbs.Length + " verbs");

        return [.. verbs
            .Select(verb => (Verb: verb, Match: RequiredPostId().Match(Help.For(verb) ?? string.Empty)))
            .Where(p => p.Match.Success)
            .Select(p => (p.Verb, p.Match.Groups["name"].Value))];
    }

    public static TheoryData<string, string> Cases()
    {
        var commands = CommandsWithARequiredPostId();
        var data = new TheoryData<string, string>();
        foreach (var (verb, _) in commands)
            foreach (var blank in new[] { "empty", "space" })
                data.Add(verb, blank);
        return data;
    }

    /// <summary>The arguments each verb needs besides its id, so the id is the only thing wrong.</summary>
    private static string[] Rest(string verb, string forum) => verb switch
    {
        "flag" => ["--agent", "alice", "--kind", "spam", "--rationale", "a reason", "--forum", forum],
        "resolve" => ["--agent", "alice", "--forum", forum],
        _ => ["--forum", forum],
    };

    /// <summary>The floor of the derived list: the five verbs this stage knows take one. A sixth is welcome; a missing one is a parse that read something else.</summary>
    [Fact]
    public void R11_58_TheDerivedListHoldsTheKnownFloor()
    {
        var verbs = CommandsWithARequiredPostId().Select(c => c.Verb).ToArray();

        foreach (var known in new[] { "read", "thread", "verify", "resolve", "flag" })
            Assert.Contains(known, verbs);
    }

    // ---- PJ42 -----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task R11_58_AnEmptyOrBlankPostIdIsRefusedByNameAndSendsNothing(string verb, string blank)
    {
        ArgumentNullException.ThrowIfNull(verb);
        var argument = CommandsWithARequiredPostId().Single(c => c.Verb == verb).Argument;
        var id = blank == "empty" ? string.Empty : " ";

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(_home, Ct, [verb, id, .. Rest(verb, _forum.Address.ToString())]);

        Assert.DoesNotContain("Parameter '", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException", stderr, StringComparison.Ordinal);
        Assert.True(exit == 1, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(argument, stderr, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>PJ42's control: a well-formed id the Forum does not know is still sent and answered "No such post" (D61 item 2's non-property). Green first; FPJ44 holds it.</summary>
    [Theory]
    [InlineData("read")]
    [InlineData("thread")]
    [InlineData("resolve")]
    [InlineData("flag")]
    public async Task R11_58_AWellFormedUnknownIdIsStillSentAndAnswered(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(_home, Ct, [verb, "01JXXXXXXXXXXXXXXXXXXXXXXX", .. Rest(verb, _forum.Address.ToString())]);

        Assert.True(exit == 5, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains("No such post", stderr, StringComparison.Ordinal);
        Assert.NotEmpty(_forum.Lines);
    }
}
