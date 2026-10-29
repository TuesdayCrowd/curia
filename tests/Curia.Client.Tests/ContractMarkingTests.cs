using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G47, R10.90), probes CHN46, CHN52 and CHN53 (the contract's part): the client reads the Reader Contract's <c>marking</c> member in three
/// states (absent is not stated, an object of three strings is its value, anything else is a malformed contract), <c>curia contract</c> prints it and says
/// <c>NOT STATED</c> for a Forum that served none, and a member the reader does not know is tolerated. The member's name and its three members are
/// R10.90's code spans, read from the errata by <see cref="PublishedChainWords"/>; the contract is scripted, so the oracle is the JSON each row writes.
///
/// <para>CHN52 is red by assertion on the unmodified tree because PR 3's Task 0 declared <see cref="ReaderContractDocument.Marking"/> as an inert seam
/// (every contract reads <see langword="null"/>) and <see cref="Program.PrintContractAsync"/> over the stub's new <see cref="StubLog.ContractJson"/> knob.</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ContractMarkingTests : IDisposable
{
    private const string Rule = "The Forum's own mark is one token at the start of a word; an author's token is written twice.";

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Result<ReaderContractDocument> Read(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var parseError), parseError?.Type);
        return ForumDocuments.ReadContract(value!);
    }

    private static string Contract(string tail) =>
        """{"version":"v1","clauses":[{"number":1,"force":"MUST","text":"A clause.","client_must_implement":false}]""" + tail + "}";

    private static string Marking(string token = "marking_token", string escape = "doubled", string rule = Rule)
    {
        var names = PublishedChainWords.Marking();
        return $"\"{names.Member}\":{{\"{names.TokenMember}\":\"{token}\",\"{names.Escape}\":\"{escape}\",\"{names.Rule}\":\"{rule}\"}}";
    }

    /// <summary>CHN52. A contract with no <c>marking</c> reads as none (a Forum that served none); one with the three strings reads them.</summary>
    [Fact]
    public void R10_90_CHN52_AMarkingIsReadAndAnAbsentOneIsNotStated()
    {
        Assert.True(Read(Contract(string.Empty)).TryGetValue(out var absent, out var e1), e1?.Type);
        Assert.True(Read(Contract("," + Marking())).TryGetValue(out var present, out var e2), e2?.Type);

        Assert.Null(absent!.Marking);
        Assert.True(present!.Marking is not null, "Expected: the marking member read Actual: null (the member was dropped)");
        Assert.Equal(("marking_token", "doubled", Rule), (present.Marking!.TokenMember, present.Marking.Escape, present.Marking.Rule));
    }

    /// <summary>CHN52. A <c>marking</c> that is a string, an array, <c>null</c>, or an object with a member missing or not a string is a malformed contract.</summary>
    [Theory]
    [InlineData("\"marking\":\"doubled\"")]
    [InlineData("\"marking\":[]")]
    [InlineData("\"marking\":null")]
    [InlineData("\"marking\":{}")]
    [InlineData("\"marking\":{\"token_member\":\"marking_token\",\"escape\":\"doubled\"}")]
    [InlineData("\"marking\":{\"token_member\":\"marking_token\",\"escape\":\"doubled\",\"rule\":7}")]
    [InlineData("\"marking\":{\"token_member\":null,\"escape\":\"doubled\",\"rule\":\"r\"}")]
    [InlineData("\"marking\":{\"token_member\":\"marking_token\",\"escape\":[],\"rule\":\"r\"}")]
    public void R10_90_CHN52_AMarkingThatCannotBeReadIsAMalformedContract(string member)
    {
        Assert.Equal("marking", PublishedChainWords.Marking().Member);

        var read = Read(Contract("," + member));

        Assert.False(read.TryGetValue(out var document, out var error), $"{member} was read as a contract whose marking is {(document?.Marking is null ? "none" : "a value")}");
        Assert.Equal("curia/client/response-malformed", error!.Type);
    }

    /// <summary>CHN53, the contract's part (a control, green now). An unknown member of the contract, and of a clause, is read and changes nothing.</summary>
    [Fact]
    public void R10_90_CHN53_AnUnknownMemberOfTheContractOrAClauseIsTolerated()
    {
        Assert.True(Read(Contract(string.Empty)).TryGetValue(out var plain, out var e1), e1?.Type);
        var withExtras = """{"version":"v1","extra_probe":[1],"clauses":[{"number":1,"force":"MUST","text":"A clause.","client_must_implement":false,"extra_probe":{"a":null}}]}""";
        Assert.True(Read(withExtras).TryGetValue(out var extra, out var e2), e2?.Type);

        Assert.Equal(plain!.Version, extra!.Version);
        Assert.Equal(plain.Clauses, extra.Clauses);
    }

    private async Task<(int Exit, string Stdout)> ContractOutputAsync(string? served)
    {
        _log.ContractJson = served;
        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(
            () => Program.PrintContractAsync(_log.Client(), StubLog.Forum, TestContext.Current.CancellationToken));
        return (exit, stdout);
    }

    /// <summary>CHN46. <c>curia contract</c> prints the member (its token member, its escape and its rule) and says NOT STATED for a Forum that served none.</summary>
    [Fact]
    public async Task R10_90_CHN46_CuriaContractPrintsTheMemberOrSaysNotStated()
    {
        var notStated = PublishedChainWords.ServedWords().NotStated;
        var marking = PublishedChainWords.Marking();

        var (exitPlain, plain) = await ContractOutputAsync(Contract(string.Empty));
        var (exitMarked, marked) = await ContractOutputAsync(Contract("," + Marking()));

        Assert.Equal((0, 0), (exitPlain, exitMarked));
        Assert.True(plain.Contains(marking.Member, StringComparison.Ordinal) && plain.Contains(notStated, StringComparison.Ordinal), $"Expected: a marking line saying {notStated} Actual: {plain}");
        var missing = new[] { marking.TokenMemberValue, marking.EscapeValue, Rule }.Where(w => !marked.Contains(w, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"curia contract does not print the marking member's {string.Join(", ", missing.Select(m => $"\"{m}\""))}. Actual: {marked}");
    }

    /// <summary>CHN46. A contract whose <c>marking</c> cannot be read fails (a malformed contract) and prints no rule.</summary>
    [Fact]
    public async Task R10_90_CHN46_AnUnreadableMarkingPrintsNoRule()
    {
        var (exit, stdout) = await ContractOutputAsync(Contract(",\"marking\":\"doubled\""));

        Assert.True(exit != 0 && !stdout.Contains("Reader Contract, v1", StringComparison.Ordinal), $"Expected: a failure and no contract Actual: exit {exit}: {stdout}");
    }
}
