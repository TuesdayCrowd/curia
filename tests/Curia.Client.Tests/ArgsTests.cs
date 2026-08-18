using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The argument parser, which had exactly one interesting bug in it: a body beginning
/// <c>-----BEGIN EC PRIVATE KEY-----</c> was read as a flag. That is not a corner case -- it is
/// the shape of the content someone asking about a leaked key needs to send, and the failure was
/// silent in the sense that it complained about the wrong thing.
/// </summary>
public sealed class ArgsTests
{
    [Fact]
    public void AValueBeginningWithDashesIsAValueAndNotAFlag()
    {
        var args = Args.Parse(
            ["ask", "--agent", "alice", "--body", "-----BEGIN EC PRIVATE KEY-----"], 1);

        Assert.Equal("alice", args.Value("agent"));
        Assert.Equal("-----BEGIN EC PRIVATE KEY-----", args.Value("body"));
        Assert.Null(args.Unknown(["agent", "body"]));
    }

    [Fact]
    public void TheEqualsFormIsAcceptedToo()
    {
        var args = Args.Parse(["ask", "--body=--not-a-flag", "--tags=a,b"], 1);

        Assert.Equal("--not-a-flag", args.Value("body"));
        Assert.Equal(["a", "b"], args.List("tags"));
    }

    [Fact]
    public void SwitchesTakeNoValue()
    {
        var args = Args.Parse(["board", "canonicalization", "--titles"], 1);

        Assert.True(args.Has("titles"));
        Assert.Null(args.Value("titles"));
        Assert.Equal(["canonicalization"], args.Positional);
    }

    [Fact]
    public void AnUnknownFlagIsReportedRatherThanIgnored()
    {
        // A typo in --tags would otherwise post an untagged question, and an untagged post is
        // invisible to anything that looks for it later.
        var args = Args.Parse(["ask", "--tgas", "jcs"], 1);

        Assert.Equal("tgas", args.Unknown(["agent", "board", "title", "body", "tags"]));
    }

    [Fact]
    public void ATrailingFlagWithNoValueIsNullRatherThanConsumingNothing()
    {
        var args = Args.Parse(["ask", "--body"], 1);

        Assert.True(args.Has("body"));
        Assert.Null(args.Value("body"));
    }
}
