using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The argument parser, which had exactly one interesting bug in it: a body beginning
/// <c>-----BEGIN EC PRIVATE KEY-----</c> was read as a flag. That is not a corner case -- it is
/// the shape of the content someone asking about a leaked key needs to send, and the failure was
/// silent in the sense that it complained about the wrong thing.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim, mirroring this " +
        "solution's existing convention.")]
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

    /// <summary>
    /// `why` was not in the switch list, so `curia search jcs --why` stored null and the caller's
    /// request for R9.8's breakdown was dropped without a word — and `--why --board b` was worse,
    /// consuming `--board` as why's value and pushing `b` into the search terms, so the search ran
    /// against different terms on a different board than the one asked for. Both are R9.25's
    /// subject in the reference client: a member supplied and not honoured, silently.
    /// </summary>
    [Fact]
    public void R9_25_WhyIsASwitchAndDoesNotSwallowTheNextFlag()
    {
        var trailing = Args.Parse(["search", "jcs", "--why"], 1);
        Assert.True(trailing.Has("why"));
        Assert.Equal(["jcs"], trailing.Positional);

        var followed = Args.Parse(["search", "jcs", "--why", "--board", "canon"], 1);
        Assert.True(followed.Has("why"));
        Assert.Equal("canon", followed.Value("board"));
        Assert.Equal(["jcs"], followed.Positional);
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

    /// <summary>
    /// R10.66 (errata G17): a name is taken back as the display literal this client printed for it,
    /// in every slot that takes one -- a command's arguments, <c>--board</c>, <c>--author</c>,
    /// <c>--parent</c>, and each tag and ref -- and read as the value it spells. A board named in
    /// another script prints as escapes, and its reader should not have to decode them by hand.
    /// </summary>
    [Fact]
    public void R10_66_ANameIsTakenBackAsTheLiteralThisClientPrintedForIt()
    {
        var board = "caf" + (char)0xE9 + "-" + (char)0x6A5F + (char)0x68B0;
        var author = "https://agents.example/a" + (char)0x0A + "b";

        var listed = Args.Parse(["board", DisplayLiteral.Of(board), "--titles"], 1);
        Assert.Null(listed.Unreadable);
        Assert.Equal([board], listed.Positional);

        var searched = Args.Parse(
            ["search", "terms", "--board", DisplayLiteral.Of(board), "--author", DisplayLiteral.Of(author), "--tags", "x," + DisplayLiteral.Of(board)],
            1);
        Assert.Null(searched.Unreadable);
        Assert.Equal(board, searched.Value("board"));
        Assert.Equal(author, searched.Value("author"));
        Assert.Equal(["x", board], searched.List("tags"));

        // A name given as itself is taken as given.
        Assert.Equal(["01M0572TG0RAWZ1W6J2SZ5ZQ4E"], Args.Parse(["read", "01M0572TG0RAWZ1W6J2SZ5ZQ4E"], 1).Positional);
    }

    /// <summary>
    /// What is not a name is taken as typed, quotation marks and all: search's terms, a body, a
    /// title, and an entity tag, which is a quoted string by its own grammar.
    /// </summary>
    [Fact]
    public void R10_66_WhatIsNotANameIsTakenAsTyped()
    {
        var searched = Args.Parse(["search", "\"exact\"", "\"unterminated"], 1);
        Assert.Null(searched.Unreadable);
        Assert.Equal(["\"exact\"", "\"unterminated"], searched.Positional);

        var read = Args.Parse(["read", "01M0572TG0RAWZ1W6J2SZ5ZQ4E", "--if-none-match", "\"abc\""], 1);
        Assert.Equal("\"abc\"", read.Value("if-none-match"));

        var asked = Args.Parse(["ask", "--body", "\"quoted\"", "--title", "\"t\""], 1);
        Assert.Null(asked.Unreadable);
        Assert.Equal("\"quoted\"", asked.Value("body"));
        Assert.Equal("\"t\"", asked.Value("title"));
    }

    /// <summary>
    /// An argument where a name is read that begins with a quotation mark, and is not a literal
    /// exactly as this client prints one, is refused and named -- never read as some other value.
    /// So is the literal of a surrogate without its pair: no name on the Forum can hold one, and the
    /// request would carry U+FFFD in its place.
    /// </summary>
    [Theory]
    [InlineData("board", "\"caf\\u" + "00E9\"", "argument 1")]
    [InlineData("read", "\"\\u" + "0041\"", "argument 1")]
    [InlineData("thread", "\"unterminated", "argument 1")]
    [InlineData("recheck", "\"a\"b\"", "argument 1")]
    [InlineData("board", "\"b\\u" + "d800\"", "argument 1")]
    public void R10_66_AnArgumentThatLooksLikeALiteralAndIsNotOneIsRefused(string command, string argument, string named)
    {
        Assert.Equal(named, Args.Parse([command, argument], 1).Unreadable);
        Assert.Equal("--board", Args.Parse(["search", "x", "--board", argument], 1).Unreadable);
        Assert.Equal("--tags", Args.Parse(["ask", "--tags", "ok," + argument], 1).Unreadable);
    }
}
