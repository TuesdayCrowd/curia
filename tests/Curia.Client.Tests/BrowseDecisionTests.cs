using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.73 (errata G22): the one decision <c>curia browse</c> makes before it writes a byte, as a truth table. Every row
/// that declines gives a reason, because a decline with no reason is a person told nothing and a model told nothing about
/// why the bytes it got are not a screen.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BrowseDecisionTests
{
    private static readonly Dictionary<string, string> Good = new(StringComparer.Ordinal)
    {
        ["TERM"] = "xterm-256color",
        ["LANG"] = "en_US.UTF-8",
    };

    private static ScreenDecision Decide(
        string[] flags,
        Dictionary<string, string>? environment = null,
        bool inputRedirected = false,
        bool outputRedirected = false,
        bool windows = false)
    {
        var args = Args.Parse(["browse", "board", "general", .. flags], 1);
        return BrowseCommand.DrawsAScreen(args, new TerminalFacts(inputRedirected, outputRedirected, windows, environment ?? Good));
    }

    private static Dictionary<string, string> With(params (string Name, string? Value)[] changes)
    {
        var environment = new Dictionary<string, string>(Good, StringComparer.Ordinal);
        foreach (var (name, value) in changes)
        {
            if (value is null) environment.Remove(name);
            else environment[name] = value;
        }

        return environment;
    }

    [Fact]
    public void R10_73_ABothEndsTerminalInAUtf8LocaleOnAnAllowedTermDrawsAScreen()
    {
        var decision = Decide([]);

        Assert.True(decision.Draws);
        Assert.False(decision.Asked);
    }

    [Theory]
    [InlineData(true, false, "standard input")]
    [InlineData(false, true, "standard output")]
    [InlineData(true, true, "standard input")]
    public void R10_73_ARedirectedEndIsTheLineFormWithItsReason(bool input, bool output, string reason)
    {
        var decision = Decide([], inputRedirected: input, outputRedirected: output);

        Assert.False(decision.Draws);
        Assert.False(decision.Asked);
        Assert.Contains(reason, decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C", null, null, false)]
    [InlineData(null, null, "C", false)]
    [InlineData(null, "C", "en_US.UTF-8", false)]
    [InlineData("en_US.ISO-8859-1", null, "en_US.UTF-8", false)]
    [InlineData(null, "en_US.ISO8859-1", null, false)]
    [InlineData(null, null, null, false)]
    [InlineData("en_US.utf8", null, "C", true)]
    [InlineData("C.UTF-8", "C", "C", true)]
    [InlineData(null, "de_DE.UTF-8", "C", true)]
    [InlineData(null, null, "fr_FR.UTF-8", true)]
    public void R10_73_TheCodesetIsReadFromLcAllThenLcCtypeThenLang(string? all, string? ctype, string? lang, bool draws)
    {
        var decision = Decide([], With(("LANG", lang), ("LC_CTYPE", ctype), ("LC_ALL", all)));

        Assert.Equal(draws, decision.Draws);
        if (!draws) Assert.Contains("UTF-8", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("dumb", false)]
    [InlineData("linux", false)]
    [InlineData("vt100", false)]
    [InlineData("xtermfoo", false)]
    [InlineData("xterm", true)]
    [InlineData("xterm-256color", true)]
    [InlineData("screen", true)]
    [InlineData("screen-256color", true)]
    [InlineData("tmux", true)]
    [InlineData("tmux-256color", true)]
    [InlineData("alacritty", true)]
    [InlineData("foot", true)]
    [InlineData("wezterm", true)]
    [InlineData("rxvt-unicode", true)]
    [InlineData("rxvt-unicode-256color", true)]
    public void R10_73_ATermOffTheListIsTheLineFormAndOnTheListIsAScreen(string? term, bool draws)
    {
        var decision = Decide([], With(("TERM", term)));

        Assert.Equal(draws, decision.Draws);
        if (!draws) Assert.Contains("TERM", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void R10_73_ScreenDrawsOnATermOffTheListButNeverOnARedirectedOrNonUtf8Terminal()
    {
        Assert.True(Decide(["--screen"], With(("TERM", "linux"))).Draws);
        Assert.True(Decide(["--screen"], With(("TERM", null))).Draws);
        Assert.False(Decide(["--screen"], outputRedirected: true).Draws);
        Assert.False(Decide(["--screen"], inputRedirected: true).Draws);
        Assert.False(Decide(["--screen"], With(("LANG", "C"))).Draws);
        Assert.False(Decide(["--screen"], windows: true).Draws);
    }

    [Fact]
    public void R10_73_PlainAndCuriaBrowsePlainAreTheCallersAskForTheLineFormAndNeedNoReasonOnStderr()
    {
        var plain = Decide(["--plain"]);
        var variable = Decide([], With(("CURIA_BROWSE", "plain")));

        Assert.False(plain.Draws);
        Assert.True(plain.Asked);
        Assert.False(variable.Draws);
        Assert.True(variable.Asked);
        Assert.NotEmpty(plain.Reason);
        Assert.NotEmpty(variable.Reason);
        Assert.True(Decide([], With(("CURIA_BROWSE", "screen"))).Draws);
        Assert.True(Decide([], With(("CURIA_BROWSE", ""))).Draws);
    }

    [Fact]
    public void R10_73_WindowsIsTheLineFormOnly()
    {
        var decision = Decide([], windows: true);

        Assert.False(decision.Draws);
        Assert.Contains("Windows", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--agent", "quill", "--agent")]
    [InlineData("--marking", "none", "--marking")]
    [InlineData("--marking", "datamark", "--marking")]
    public void R10_73_AnOptionTheScreenCannotHonourDeclinesItWithTheReasonStated(string flag, string value, string named)
    {
        var decision = Decide([flag, value]);

        Assert.False(decision.Draws);
        Assert.False(decision.Asked);
        Assert.Contains(named, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void R10_73_TheForumFlagAndVariableAreAcceptedByTheScreen()
    {
        Assert.True(Decide(["--forum", "http://localhost:5999"]).Draws);
        Assert.True(Decide([], With(("CURIA_FORUM", "http://localhost:5999"))).Draws);
    }

    [Fact]
    public void R10_73_EveryDeclineGivesAReason()
    {
        var declines = new[]
        {
            Decide([], inputRedirected: true), Decide([], outputRedirected: true), Decide([], windows: true),
            Decide([], With(("TERM", "dumb"))), Decide([], With(("LANG", "C"))), Decide(["--agent", "x"]), Decide(["--marking", "none"]),
            Decide(["--plain"]), Decide([], With(("CURIA_BROWSE", "plain"))),
        };

        Assert.All(declines, d => Assert.False(d.Draws));
        Assert.All(declines, d => Assert.False(string.IsNullOrWhiteSpace(d.Reason)));
    }

    [Fact]
    public void R10_73_PlainAndScreenAreSwitchesSoTheyNeverTakeTheNextToken()
    {
        var args = Args.Parse(["browse", "--plain", "board", "--screen", "general"], 1);

        Assert.Equal(["board", "general"], args.Positional);
        Assert.True(args.Has("plain"));
        Assert.True(args.Has("screen"));
    }
}
