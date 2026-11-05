using System.Diagnostics.CodeAnalysis;
using System.Net;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.56 (errata G62), NMN21 to NMN25: <c>GET /v1/search</c> refuses a present <c>kind</c>, <c>tags</c> or <c>cursor</c> that names nothing and <c>GET /v1/inbox</c> a present
/// <c>tags</c> or <c>cursor</c>, <c>400</c>, as <c>curia/search/blank-parameter</c> and <c>curia/inbox/blank-parameter</c>, naming the parameter and no value; for the two lists a
/// value of commas and white space names nothing too, and <c>a,,b</c> and <c>question,</c> name something and are served.
///
/// <para><b>Oracles.</b> The types, the status and the order of names are R9.54's (<see cref="PublishedBoardWords"/>), which R9.56 extends; the seven spellings are
/// <see cref="BlankParameterTests.Spellings"/>. Each refusal has its control: the same request with a value, <c>200</c>, is the first assertion of the fact and green on the seam.
/// "No read of its own" is a counting <see cref="IEventReader"/> compared with the unknown-name refusal of the same route, which is made before it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BlankListParameterTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Spellings = BlankParameterTests.Spellings;

    /// <summary>
    /// NMN21. The search route's <c>kind</c>, <c>tags</c> and <c>cursor</c> and the inbox's <c>tags</c> and <c>cursor</c>, each in the seven spellings, are <c>400</c> with the
    /// route's type and <c>parameter=&lt;name&gt;</c>; the same request with a value of its own is <c>200</c> (the controls come first). Predicted red on the seam: <c>200</c> for
    /// every blank.
    /// </summary>
    [Fact]
    public async Task R9_56_NMN21_ABlankKindTagsOrCursorIsRefusedByName()
    {
        var (searchType, inboxType) = PublishedBoardWords.BlankTypes();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "nmn21", Ct);
        var expected = new List<string>();
        var actual = new List<string>();

        var controls = new[] { "q=x&kind=question", "q=x&tags=a", "q=x" };
        foreach (var control in controls)
        {
            expected.Add($"{control}: 200");
            actual.Add($"{control}: {(int)(await SearchAsync(forum.Client, control, Ct)).Status}");
        }

        expected.Add("inbox tags=a: 200");
        actual.Add($"inbox tags=a: {(int)(await InboxAsync(forum, forum.Client, actor, "tags=a", Ct)).Status}");

        foreach (var parameter in new[] { "kind", "tags", "cursor" })
        {
            foreach (var spelling in Spellings)
            {
                var reply = await SearchAsync(forum.Client, $"q=x&{parameter}={spelling}", Ct);
                expected.Add($"search {parameter}={spelling}: 400 {searchType} parameter={parameter}");
                actual.Add($"search {parameter}={spelling}: {reply.Says} {Detail(reply)}");
            }
        }

        foreach (var parameter in new[] { "tags", "cursor" })
        {
            foreach (var spelling in Spellings)
            {
                var reply = await InboxAsync(forum, forum.Client, actor, $"{parameter}={spelling}", Ct);
                expected.Add($"inbox {parameter}={spelling}: 400 {inboxType} parameter={parameter}");
                actual.Add($"inbox {parameter}={spelling}: {reply.Says} {Detail(reply)}");
            }
        }

        Said(expected, actual);
    }

    private static string Detail(NameReply reply) =>
        reply.Json.ValueKind == System.Text.Json.JsonValueKind.Object && reply.Json.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString()! : string.Empty;

    /// <summary>
    /// NMN22. The list rule: a list that holds nothing but commas and white space names nothing (<c>kind=,</c>, <c>tags=,%20,</c>) and is refused; <c>tags=a,,b</c> and
    /// <c>kind=question,</c> name something and are served (non-property (e)). Predicted red: <c>200</c> for the first two; the last two are controls, green on the seam.
    /// </summary>
    [Fact]
    public async Task R9_56_NMN22_AListOfNothingButCommasNamesNothingAndAListThatNamesSomethingIsServed()
    {
        var rows = new (string Query, string Expected)[]
        {
            ("q=x&tags=a,,b", "200"),
            ("q=x&kind=question,", "200"),
            ("q=x&kind=,", "400 curia/search/blank-parameter"),
            ("q=x&tags=,%20,", "400 curia/search/blank-parameter"),
            ("q=x&kind=%20,%09,", "400 curia/search/blank-parameter"),
        };
        var actual = new List<string>();
        foreach (var (query, _) in rows)
        {
            var reply = await SearchAsync(forum.Client, query, Ct);
            actual.Add($"{query}: {reply.Says}");
        }

        Said(rows.Select(r => $"{r.Query}: {r.Expected}"), actual);
    }

    /// <summary>
    /// NMN23. The order (R9.29's): an unknown name is told before a blank; a blank is told before a bad value (<c>limit=0&amp;kind=%20</c> names the blank and not the limit); and the
    /// blank refusal reads the log no more than the unknown-name refusal of the same route does. Predicted red on the seam: the middle row answers the limit's refusal, and the blank's
    /// read count is the served request's.
    /// </summary>
    [Fact]
    public async Task R9_56_NMN23_ABlankIsToldAfterTheNamesAndBeforeTheValuesAndReadsNoLogOfItsOwn()
    {
        var counter = new Counter();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.CountRequestReads(counter)));
        using var client = host.CreateClient();

        async Task<(string Says, int Reads)> RunAsync(string query)
        {
            counter.Count = 0;
            var reply = await SearchAsync(client, query, Ct);
            return (reply.Says + " " + Detail(reply), counter.Count);
        }

        var unknownBeforeBlank = await RunAsync("q=x&bogus=1&kind=");
        var blankBeforeValue = await RunAsync("limit=0&q=x&kind=%20");
        var unknown = await RunAsync("q=x&bogus=1");
        var blankKind = await RunAsync("q=x&kind=%20");

        Said(
            ["an unknown name first: 400 curia/search/unknown-parameter", "a blank before a bad value: 400 curia/search/blank-parameter parameter=kind", "the blank's reads equal the unknown name's"],
            [$"an unknown name first: {unknownBeforeBlank.Says.Split(" parameter=")[0]}", $"a blank before a bad value: {blankBeforeValue.Says}", blankKind.Reads == unknown.Reads ? "the blank's reads equal the unknown name's" : $"the blank read {blankKind.Reads} times and the unknown name {unknown.Reads}"]);
    }

    /// <summary>
    /// NMN24. R9.38 (rev.): the inbox's blank cursor is refused (<c>blank-parameter</c> naming <c>cursor</c>), a malformed one is still <c>cursor-malformed</c> and an absent one is a
    /// first page. The absent row is a control, green on the seam. Predicted red: <c>200</c> for the blank.
    /// </summary>
    [Fact]
    public async Task R9_56_NMN24_TheInboxRefusesABlankCursorAndStillRefusesAMalformedOneAndReadsNoneAsAFirstPage()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "nmn24", Ct);
        var absent = await InboxAsync(forum, forum.Client, actor, string.Empty, Ct);
        var blank = await InboxAsync(forum, forum.Client, actor, "cursor=", Ct);
        var malformed = await InboxAsync(forum, forum.Client, actor, "cursor=garbage", Ct);

        Said(
            ["no cursor: 200", "a blank cursor: 400 curia/inbox/blank-parameter", "a malformed cursor: 400 curia/inbox/cursor-malformed"],
            [$"no cursor: {absent.Says}", $"a blank cursor: {blank.Says}", $"a malformed cursor: {malformed.Says}"]);
    }

    /// <summary>
    /// NMN25. A repeated parameter is joined as before (non-property (i)): <c>kind=question&amp;kind=</c> reads <c>question,</c> and is served; <c>board=%20&amp;board=meta</c> reads
    /// <c> ,meta</c> and is served with no result. A control, green on the seam.
    /// </summary>
    [Fact]
    public async Task R9_56_NMN25_ARepeatedParameterIsJoinedAsItWas()
    {
        var kind = await SearchAsync(forum.Client, "q=x&kind=question&kind=", Ct);
        var board = await SearchAsync(forum.Client, "board=%20&board=meta", Ct);

        Said(["kind=question&kind=: 200", "board=%20&board=meta: 200, 0 results"], [$"kind=question&kind=: {(int)kind.Status}", $"board=%20&board=meta: {(int)board.Status}, {board.Results} results"]);
        Assert.Equal(HttpStatusCode.OK, kind.Status);
    }
}
